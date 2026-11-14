using System.Globalization;
using System.Text.Json;

namespace Encore.Discovery;

public sealed record LbSimilarArtist(Guid Mbid, string Name, long Score);

public interface IListenBrainzClient
{
    /// <summary>
    /// GET https://labs.api.listenbrainz.org/similar-artists/json?artist_mbids={mbid}&amp;algorithm={LbAlgorithm}.
    /// Best first. Empty when ListenBrainz rejects the request (HTTP 400) or is being skipped after such a rejection.
    /// </summary>
    Task<IReadOnlyList<LbSimilarArtist>> GetSimilarArtistsAsync(Guid artistMbid, int limit, CancellationToken ct);
}

/// <summary>
/// Keyless ListenBrainz Labs client. Honours the X-RateLimit headers (Remaining 0 pauses the limiter for Reset-In
/// seconds) and 429 (Reset-In, else Retry-After, else 10 s, one retry). HTTP 400 (the algorithm name changed) is
/// logged, answered with an empty list, and skips ListenBrainz for 24 h. No Authorization header is ever sent.
/// </summary>
public sealed class ListenBrainzClient(HttpClient http, DiscoveryRuntime runtime, ILogger<ListenBrainzClient> logger) : IListenBrainzClient
{
    public const string Algorithm = "session_based_days_9000_session_300_contribution_5_threshold_15_limit_50_skip_30";

    private const string Endpoint = "/similar-artists/json";

    public async Task<IReadOnlyList<LbSimilarArtist>> GetSimilarArtistsAsync(Guid artistMbid, int limit, CancellationToken ct)
    {
        if (runtime.ListenBrainzSkipped)
        {
            return [];
        }

        var key = "lb:similar:" + artistMbid.ToString("D");
        if (!runtime.Cache.TryGet<IReadOnlyList<LbSimilarArtist>>(key, out var all) || all is null)
        {
            var fetched = await FetchAsync(artistMbid, ct).ConfigureAwait(false);
            if (fetched is null)
            {
                return [];
            }

            all = fetched;
            runtime.Cache.Set(key, all, runtime.Options.ListTtl);
        }

        return limit >= all.Count ? all : all.Take(Math.Max(limit, 0)).ToList();
    }

    /// <summary>Null when ListenBrainz rejected the request (400).</summary>
    private async Task<IReadOnlyList<LbSimilarArtist>?> FetchAsync(Guid mbid, CancellationToken ct)
    {
        var options = runtime.Options;
        var path = "similar-artists/json?artist_mbids=" + mbid.ToString("D") + "&algorithm=" + Algorithm;
        var rateRetried = false;
        var serverRetried = false;
        while (true)
        {
            var raw = await DiscoveryTransport.GetAsync(
                http, runtime.ListenBrainz, runtime.Time, options.Timeout, path, Endpoint,
                (e, kind, message) => new ListenBrainzException(e, kind, message), ct).ConfigureAwait(false);

            if (raw.Status == 429)
            {
                var wait = RateLimitWait(raw, options);
                runtime.ListenBrainz.PauseUntil(runtime.Time.GetUtcNow() + wait);
                logger.Quota("listenbrainz", wait.TotalSeconds);
                if (!rateRetried)
                {
                    rateRetried = true;
                    continue;
                }

                throw new ListenBrainzException(Endpoint, DiscoveryFailure.Quota, $"{Endpoint}: ListenBrainz rate limit (HTTP 429)", 429);
            }

            if (raw.Status >= 500 && !serverRetried)
            {
                serverRetried = true;
                await Task.Delay(options.ServerErrorDelay, runtime.Time, ct).ConfigureAwait(false);
                continue;
            }

            if (raw.Status == 400)
            {
                var text = raw.Body.Length > 300 ? raw.Body[..300] : raw.Body;
                logger.SourceFailed("listenbrainz", Endpoint, 400, text);
                runtime.SkipListenBrainz(options.ListenBrainzSkip);
                return null;
            }

            if (raw.Status is < 200 or >= 300)
            {
                throw new ListenBrainzException(Endpoint, DiscoveryFailure.Http, $"{Endpoint} failed with HTTP {raw.Status}", raw.Status);
            }

            NoteRemaining(raw, options);
            return Parse(raw.Body);
        }
    }

    /// <summary>Remaining 0 means the window is used up: hold every later call for Reset-In seconds.</summary>
    private void NoteRemaining(RawResponse raw, DiscoveryOptions options)
    {
        if (Header(raw, "X-RateLimit-Remaining") is { } remaining
            && double.TryParse(remaining, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) && left <= 0
            && ResetIn(raw) is { } reset)
        {
            var wait = reset > options.MaxRateLimitWait ? options.MaxRateLimitWait : reset;
            runtime.ListenBrainz.PauseUntil(runtime.Time.GetUtcNow() + wait);
        }
    }

    private TimeSpan RateLimitWait(RawResponse raw, DiscoveryOptions options)
    {
        var wait = ResetIn(raw)
            ?? raw.Headers.RetryAfter?.Delta
            ?? (raw.Headers.RetryAfter?.Date is { } date ? date - runtime.Time.GetUtcNow() : options.DefaultRateLimitWait);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > options.MaxRateLimitWait ? options.MaxRateLimitWait : wait;
    }

    private static TimeSpan? ResetIn(RawResponse raw) =>
        Header(raw, "X-RateLimit-Reset-In") is { } v
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static string? Header(RawResponse raw, string name) =>
        raw.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static IReadOnlyList<LbSimilarArtist> Parse(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ListenBrainzException(Endpoint, DiscoveryFailure.BadResponse, $"{Endpoint} returned unreadable JSON");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ListenBrainzException(Endpoint, DiscoveryFailure.BadResponse, $"{Endpoint} did not return a list");
            }

            var result = new List<LbSimilarArtist>();
            foreach (var row in doc.RootElement.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
            {
                if (row.TryGetProperty("artist_mbid", out var m) && m.ValueKind == JsonValueKind.String
                    && Guid.TryParse(m.GetString(), out var mbid)
                    && row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(n.GetString()))
                {
                    long score = 0;
                    if (row.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
                    {
                        score = s.TryGetInt64(out var whole) ? whole : (long)Math.Round(s.GetDouble());
                    }

                    result.Add(new LbSimilarArtist(mbid, n.GetString()!, score));
                }
            }

            return result;
        }
    }
}
