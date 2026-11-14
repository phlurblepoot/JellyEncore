namespace Encore.Discovery;

/// <summary>Why a Discovery call failed. Quota covers Deezer's error code 4 and a ListenBrainz 429.</summary>
public enum DiscoveryFailure
{
    Quota,
    Http,
    Timeout,
    Transport,
    BadResponse,
}

/// <summary>
/// Failure of a Deezer or ListenBrainz call. Messages name the source, the endpoint path (no query) and the status
/// only: never the query string, the body, or any Deezer preview or track_token value.
/// </summary>
public class DiscoveryException(string source, string endpoint, DiscoveryFailure kind, string message, int? statusCode = null)
    : Exception(message)
{
    public string SourceName { get; } = source;

    /// <summary>Path only, e.g. /artist/399/radio.</summary>
    public string Endpoint { get; } = endpoint;

    public DiscoveryFailure Kind { get; } = kind;

    /// <summary>HTTP status, or the Deezer error code for an error-in-200; null for a timeout or transport failure.</summary>
    public int? StatusCode { get; } = statusCode;
}

public sealed class DeezerException(string endpoint, DiscoveryFailure kind, string message, int? statusCode = null)
    : DiscoveryException("deezer", endpoint, kind, message, statusCode);

public sealed class ListenBrainzException(string endpoint, DiscoveryFailure kind, string message, int? statusCode = null)
    : DiscoveryException("listenbrainz", endpoint, kind, message, statusCode);

/// <summary>Fixed v0.2 settings of the two Discovery clients (design section 3.2). Tests shorten them.</summary>
public sealed record DiscoveryOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public int DeezerLimit { get; init; } = 40;

    public TimeSpan DeezerWindow { get; init; } = TimeSpan.FromSeconds(5);

    public int ListenBrainzLimit { get; init; } = 30;

    public TimeSpan ListenBrainzWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Wait after Deezer's quota body (code 4) before the one retry.</summary>
    public TimeSpan QuotaWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Wait before the one retry after an HTTP 5xx.</summary>
    public TimeSpan ServerErrorDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>ListenBrainz wait when a 429 names no delay.</summary>
    public TimeSpan DefaultRateLimitWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Longest rate-limit wait honoured; a picking run is cancelled after 25 s anyway.</summary>
    public TimeSpan MaxRateLimitWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long ListenBrainz is skipped after it rejected the request (HTTP 400).</summary>
    public TimeSpan ListenBrainzSkip { get; init; } = TimeSpan.FromHours(24);

    public int CacheCapacity { get; init; } = 5_000;

    public TimeSpan ArtistTtl { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan MissTtl { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan ListTtl { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan TrackTtl { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Sliding-window token bucket on a <see cref="TimeProvider"/>: at most <c>limit</c> grants per <c>window</c>, handed
/// out in call order. A caller over the limit waits until the grant that is <c>limit</c> places before it ages out.
/// <see cref="PauseUntil"/> blocks all later grants (ListenBrainz rate-limit headers).
/// </summary>
public sealed class RequestLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _grants = new();
    private readonly object _gate = new();
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public RequestLimiter(int limit, TimeSpan window, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
        _window = window;
        _time = time;
    }

    /// <summary>Completes when the caller may send one request.</summary>
    public async Task AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var at = now;
            if (_blockedUntil > at)
            {
                at = _blockedUntil;
            }

            if (_grants.Count >= _limit)
            {
                var free = _grants.Peek() + _window;
                if (free > at)
                {
                    at = free;
                }
            }

            _grants.Enqueue(at);
            while (_grants.Count > _limit)
            {
                _grants.Dequeue();
            }

            wait = at - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Nothing is granted before <paramref name="until"/> (an earlier pause is never shortened).</summary>
    public void PauseUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }
    }
}

/// <summary>
/// In-memory cache with a per-entry lifetime and a size cap: when full, the oldest stored entry goes first.
/// A cached null is a real answer ("not found"). Thread safe.
/// </summary>
public sealed class TtlCache
{
    private sealed record Entry(object? Value, DateTimeOffset Expires, LinkedListNode<string> Node);

    private readonly int _capacity;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Entry> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly object _gate = new();

    public TtlCache(int capacity, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _time = time;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool TryGet<T>(string key, out T? value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var entry))
            {
                if (entry.Expires > _time.GetUtcNow() && (entry.Value is null || entry.Value is T))
                {
                    value = (T?)entry.Value;
                    return true;
                }

                Remove(key, entry);
            }
        }

        value = default;
        return false;
    }

    public void Set(string key, object? value, TimeSpan ttl)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var old))
            {
                Remove(key, old);
            }

            while (_items.Count >= _capacity && _order.First is { } oldest)
            {
                _items.Remove(oldest.Value);
                _order.RemoveFirst();
            }

            var node = _order.AddLast(key);
            _items[key] = new Entry(value, _time.GetUtcNow() + ttl, node);
        }
    }

    private void Remove(string key, Entry entry)
    {
        _items.Remove(key);
        _order.Remove(entry.Node);
    }
}

/// <summary>
/// Everything the two typed clients share, held once as a singleton (typed HttpClients are transient): the clock,
/// both limiters, the cache, and when ListenBrainz is skipped until.
/// </summary>
public sealed class DiscoveryRuntime
{
    private long _lbSkipUntilTicks;

    public DiscoveryRuntime(DiscoveryOptions? options = null, TimeProvider? time = null)
    {
        Options = options ?? new DiscoveryOptions();
        Time = time ?? TimeProvider.System;
        Deezer = new RequestLimiter(Options.DeezerLimit, Options.DeezerWindow, Time);
        ListenBrainz = new RequestLimiter(Options.ListenBrainzLimit, Options.ListenBrainzWindow, Time);
        Cache = new TtlCache(Options.CacheCapacity, Time);
    }

    public DiscoveryOptions Options { get; }

    public TimeProvider Time { get; }

    public RequestLimiter Deezer { get; }

    public RequestLimiter ListenBrainz { get; }

    public TtlCache Cache { get; }

    private long _deezerFailedTicks;

    /// <summary>When the last Deezer call failed (for /health), or null when none has.</summary>
    public DateTimeOffset? DeezerLastFailureUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _deezerFailedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void NoteDeezerFailure() => Interlocked.Exchange(ref _deezerFailedTicks, Time.GetUtcNow().UtcTicks);

    public bool ListenBrainzSkipped => Time.GetUtcNow().UtcTicks < Interlocked.Read(ref _lbSkipUntilTicks);

    public void SkipListenBrainz(TimeSpan duration) =>
        Interlocked.Exchange(ref _lbSkipUntilTicks, (Time.GetUtcNow() + duration).UtcTicks);
}

/// <summary>Name folding for matching an artist query to Deezer's names: the TrackKey artist folding (design section 5.1).</summary>
internal static class NameFold
{
    public static string Fold(string? name) => KeyText.ArtistKey(name);
}

/// <summary>Discovery log events (v0.2 design section 13: 7020-7040, numbered like the design ids).</summary>
internal static partial class DiscoveryLog
{
    [LoggerMessage(EventId = 7020, EventName = "discovery.source_failed", Level = LogLevel.Warning,
        Message = "Discovery source {Source} failed at {Endpoint} (status {Status}): {Error}")]
    public static partial void SourceFailed(this ILogger logger, string source, string endpoint, int status, string error);

    [LoggerMessage(EventId = 7030, EventName = "discovery.quota", Level = LogLevel.Warning,
        Message = "Discovery source {Source} is rate limited; waiting {WaitSeconds} s")]
    public static partial void Quota(this ILogger logger, string source, double waitSeconds);

    [LoggerMessage(EventId = 7040, EventName = "discovery.artist_not_found", Level = LogLevel.Debug,
        Message = "No {Source} artist found for {Name}")]
    public static partial void ArtistNotFound(this ILogger logger, string source, string name);
}
