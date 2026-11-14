using System.Collections.Concurrent;
using Encore.Jellyfin;
using Encore.Store;

namespace Encore.Discovery;

/// <summary>Where an outside suggestion came from. Wire names: deezer_radio, deezer_related, lb_similar.</summary>
public enum CandidateOrigin
{
    DeezerRadio,
    DeezerRelated,
    ListenBrainzSimilar,
}

public static class CandidateOriginNames
{
    public static string ToWire(this CandidateOrigin origin) => origin switch
    {
        CandidateOrigin.DeezerRadio => "deezer_radio",
        CandidateOrigin.DeezerRelated => "deezer_related",
        CandidateOrigin.ListenBrainzSimilar => "lb_similar",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };
}

/// <summary>
/// One outside suggestion (design section 3.1). <see cref="Title"/> is Deezer's full title (version included, so a
/// live take stays a live take); <see cref="Key"/> folds it. <see cref="ArtistMbid"/> is set for ListenBrainz picks.
/// </summary>
public sealed record Candidate(
    TrackKey Key, string Artist, string Title, string? Album, int? DurationS,
    long? DeezerTrackId, long? DeezerArtistId, Guid? ArtistMbid,
    CandidateOrigin Origin, int Rank);

/// <param name="Seed">The current song.</param>
/// <param name="ExcludedIds">Recently played or queued item ids (<c>RecentExclusions.Build</c>), plus the plan and handed-out picks.</param>
/// <param name="ExcludedKeys">Keys of the plan and the handed-out picks (keys of excluded ids are added here too).</param>
public sealed record CandidateQuery(BaseItemDto Seed, IReadOnlySet<Guid> ExcludedIds, IReadOnlySet<TrackKey> ExcludedKeys);

/// <summary>A suggestion and its server copy; <see cref="Owned"/> null means it needs a fetch.</summary>
public sealed record ResolvedCandidate(Candidate Candidate, OwnedEntry? Owned);

public interface ICandidateSource
{
    /// <summary>External suggestions for the seed, filtered (design section 4.2), owned ones resolved. Throws only OperationCanceledException.</summary>
    Task<IReadOnlyList<ResolvedCandidate>> GetAsync(CandidateQuery query, CancellationToken ct);
}

/// <summary>Track keys that recently failed to fetch (<c>fetch_negative</c>, until_utc &gt; now), as <see cref="TrackKey.Value"/> strings.</summary>
public interface INegativeCache
{
    IReadOnlySet<string> Keys(DateTimeOffset now);
}

public sealed class NullNegativeCache : INegativeCache
{
    public static readonly NullNegativeCache Instance = new();

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    public IReadOnlySet<string> Keys(DateTimeOffset now) => Empty;
}

/// <summary>The negative cache read from the fetch store.</summary>
public sealed class FetchStoreNegativeCache(FetchStore store) : INegativeCache
{
    public IReadOnlySet<string> Keys(DateTimeOffset now) => store.NegativeKeys(now);
}

/// <summary>Fixed sizes of the outside lists (design sections 4.2 and 11).</summary>
public static class CandidateLimits
{
    public const int Radio = 25;
    public const int RelatedFetched = 10;
    public const int RelatedArtists = 5;
    public const int TopPerArtist = 5;
    public const int ListenBrainzFetched = 10;
    public const int ListenBrainzArtists = 5;
    public const int MaxInFlight = 4;
    public const int MinDurationS = 60;
    public const int MaxDurationS = 12 * 60;

    public static readonly TimeSpan MbidTtl = TimeSpan.FromHours(24);
}

/// <summary>
/// Builds the outside candidate list for a seed (design section 4.2): Deezer artist radio (R), the top songs of five
/// Deezer related artists (D) and of five ListenBrainz similar artists not already in D (L, only with an artist
/// MusicBrainz id), merged R1, D1, L1, R2 ..., deduped by <see cref="TrackKey"/> (first wins). Drops: no key, the
/// seed's own key, length outside 60 s to 12 min, excluded ids and keys (and the keys of excluded ids, so another copy
/// of a recently played song is dropped too), and unowned songs on the negative cache. Owned songs are KEPT with their
/// server copy. A failing source gives an empty list and <c>discovery.source_failed</c>; only cancellation throws.
/// </summary>
public sealed class CandidateSource(
    IDeezerClient deezer,
    IListenBrainzClient listenBrainz,
    IJellyfinClient jellyfin,
    IOwnedIndex owned,
    INegativeCache negative,
    DiscoveryRuntime runtime,
    ILogger<CandidateSource> logger) : ICandidateSource
{
    private sealed record Source(DeezerTrack Track, CandidateOrigin Origin, Guid? Mbid);

    private sealed class Call(CancellationToken ct)
    {
        public SemaphoreSlim Gate { get; } = new(CandidateLimits.MaxInFlight, CandidateLimits.MaxInFlight);

        public CancellationToken Token { get; } = ct;

        public ConcurrentDictionary<string, bool> Logged { get; } = new(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ResolvedCandidate>> GetAsync(CandidateQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var seed = query.Seed;
        var (artistId, artistName) = OwnedIndex.ArtistOf(seed);
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return [];
        }

        var call = new Call(ct);
        using var gate = call.Gate;

        var mbid = artistId is { } id ? await ArtistMbidAsync(id, call).ConfigureAwait(false) : null;
        var dzArtist = await GuardAsync(call, "deezer", () => deezer.FindArtistAsync(artistName, ct), null).ConfigureAwait(false);
        logger.Seed(seed.Id, artistName, dzArtist?.Id ?? 0, mbid?.ToString("D") ?? "");

        IReadOnlyList<DeezerTrack> noTracks = [];
        IReadOnlyList<DeezerArtist> noArtists = [];
        var radioTask = dzArtist is null
            ? Task.FromResult(noTracks)
            : GuardAsync(call, "deezer", () => deezer.GetArtistRadioAsync(dzArtist.Id, CandidateLimits.Radio, ct), noTracks);
        var relatedArtistsTask = dzArtist is null
            ? Task.FromResult(noArtists)
            : GuardAsync(call, "deezer", () => deezer.GetRelatedArtistsAsync(dzArtist.Id, CandidateLimits.RelatedFetched, ct), noArtists);
        var relatedTask = RelatedAsync(relatedArtistsTask, dzArtist, call);
        var lbTask = mbid is { } m ? SimilarAsync(m, relatedArtistsTask, artistName, call) : Task.FromResult(new List<Source>());

        await Task.WhenAll(radioTask, relatedTask, lbTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var radio = radioTask.Result.Select(t => new Source(t, CandidateOrigin.DeezerRadio, null)).ToList();
        var related = relatedTask.Result;
        var similar = lbTask.Result;
        var merged = RoundRobin([radio, related, similar]);

        var excludedKeys = new HashSet<TrackKey>(query.ExcludedKeys);
        foreach (var excludedId in query.ExcludedIds)
        {
            if (owned.KeyOf(excludedId) is { } k)
            {
                excludedKeys.Add(k);
            }
        }

        var seedKey = TrackKey.Create(artistName, seed.Name);
        IReadOnlySet<string>? negativeKeys = null;
        var seen = new HashSet<TrackKey>();
        var result = new List<ResolvedCandidate>();
        int dropped = 0, ownedCount = 0, rank = 0;
        foreach (var source in merged)
        {
            var track = source.Track;
            var title = string.IsNullOrWhiteSpace(track.Title) ? track.TitleShort : track.Title;
            if (!TrackKey.TryCreate(track.Artist.Name, title, out var key))
            {
                dropped++;
                continue;
            }

            if (!seen.Add(key))
            {
                continue;
            }

            rank++;
            if (key == seedKey
                || track.DurationS < CandidateLimits.MinDurationS
                || track.DurationS > CandidateLimits.MaxDurationS
                || excludedKeys.Contains(key))
            {
                dropped++;
                continue;
            }

            var copy = owned.Resolve(key, track.DurationS);
            if (copy is not null && query.ExcludedIds.Contains(copy.ItemId))
            {
                dropped++;
                continue;
            }

            if (copy is null)
            {
                negativeKeys ??= NegativeKeys(call);
                if (negativeKeys.Contains(key.Value))
                {
                    dropped++;
                    continue;
                }
            }
            else
            {
                ownedCount++;
            }

            var candidate = new Candidate(
                key, track.Artist.Name, title, string.IsNullOrWhiteSpace(track.Album.Title) ? null : track.Album.Title,
                track.DurationS, track.Id, track.Artist.Id == 0 ? null : track.Artist.Id, source.Mbid, source.Origin, rank);
            result.Add(new ResolvedCandidate(candidate, copy));
        }

        logger.Candidates(seed.Id, radio.Count, related.Count, similar.Count, result.Count, ownedCount, result.Count - ownedCount, dropped);
        return result;
    }

    /// <summary>D: the first five related artists, each with its top five, interleaved a1t1, a2t1 ... a1t2 ...</summary>
    private async Task<List<Source>> RelatedAsync(Task<IReadOnlyList<DeezerArtist>> artistsTask, DeezerArtist? seedArtist, Call call)
    {
        var artists = (await artistsTask.ConfigureAwait(false))
            .Where(a => seedArtist is null || a.Id != seedArtist.Id)
            .Take(CandidateLimits.RelatedArtists)
            .ToList();
        var tops = await Task.WhenAll(artists.Select(a => TopAsync(a.Id, call))).ConfigureAwait(false);
        return Interleave(tops.Select(t => t.Select(x => new Source(x, CandidateOrigin.DeezerRelated, null)).ToList()).ToList());
    }

    /// <summary>L: ListenBrainz similar artists not already in D (by ArtistKey), first five, each found on Deezer with its top five.</summary>
    private async Task<List<Source>> SimilarAsync(Guid mbid, Task<IReadOnlyList<DeezerArtist>> relatedTask, string seedArtistName, Call call)
    {
        var similarTask = GuardAsync<IReadOnlyList<LbSimilarArtist>>(
            call, "listenbrainz", () => listenBrainz.GetSimilarArtistsAsync(mbid, CandidateLimits.ListenBrainzFetched, call.Token), []);
        var similar = await similarTask.ConfigureAwait(false);
        var related = await relatedTask.ConfigureAwait(false);
        var taken = new HashSet<string>(StringComparer.Ordinal) { KeyText.ArtistKey(seedArtistName) };
        foreach (var a in related.Take(CandidateLimits.RelatedArtists))
        {
            taken.Add(KeyText.ArtistKey(a.Name));
        }

        var chosen = similar
            .Where(s => KeyText.ArtistKey(s.Name) is { Length: > 0 } k && taken.Add(k))
            .Take(CandidateLimits.ListenBrainzArtists)
            .ToList();
        var lists = await Task.WhenAll(chosen.Select(async s =>
        {
            var artist = await GuardAsync(call, "deezer", () => deezer.FindArtistAsync(s.Name, call.Token), null).ConfigureAwait(false);
            IReadOnlyList<DeezerTrack> top = artist is null ? [] : await TopAsync(artist.Id, call).ConfigureAwait(false);
            return top.Select(t => new Source(t, CandidateOrigin.ListenBrainzSimilar, s.Mbid)).ToList();
        })).ConfigureAwait(false);
        return Interleave(lists);
    }

    private Task<IReadOnlyList<DeezerTrack>> TopAsync(long artistId, Call call) =>
        GuardAsync<IReadOnlyList<DeezerTrack>>(call, "deezer", () => deezer.GetArtistTopAsync(artistId, CandidateLimits.TopPerArtist, call.Token), []);

    /// <summary>The artist's MusicBrainz id from Jellyfin (fields=ProviderIds), cached 24 h per artist id (a missing id too).</summary>
    private async Task<Guid?> ArtistMbidAsync(Guid artistId, Call call)
    {
        var cacheKey = "jf:mbid:" + artistId.ToString("N");
        if (runtime.Cache.TryGet<string>(cacheKey, out var cached))
        {
            return Guid.TryParse(cached, out var hit) ? hit : null;
        }

        try
        {
            await call.Gate.WaitAsync(call.Token).ConfigureAwait(false);
            IReadOnlyList<BaseItemDto> items;
            try
            {
                items = await jellyfin.GetItemsByIdsAsync([artistId], "ProviderIds", call.Token).ConfigureAwait(false);
            }
            finally
            {
                call.Gate.Release();
            }

            string? raw = null;
            var artist = items.FirstOrDefault(i => i.Id == artistId);
            artist?.ProviderIds?.TryGetValue("MusicBrainzArtist", out raw);
            var mbid = Guid.TryParse(raw, out var parsed) ? parsed : (Guid?)null;
            runtime.Cache.Set(cacheKey, mbid?.ToString("D") ?? "", CandidateLimits.MbidTtl);
            return mbid;
        }
        catch (Exception e) when (!(e is OperationCanceledException && call.Token.IsCancellationRequested))
        {
            LogFailure(call, "jellyfin", e);
            return null;
        }
    }

    private IReadOnlySet<string> NegativeKeys(Call call)
    {
        try
        {
            return negative.Keys(runtime.Time.GetUtcNow());
        }
        catch (Exception e)
        {
            LogFailure(call, "store", e);
            return new HashSet<string>();
        }
    }

    /// <summary>Runs one outside call under the in-flight gate; a failure (not the caller's cancellation) is logged and gives <paramref name="fallback"/>.</summary>
    private async Task<T> GuardAsync<T>(Call call, string source, Func<Task<T>> action, T fallback)
    {
        try
        {
            await call.Gate.WaitAsync(call.Token).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                call.Gate.Release();
            }
        }
        catch (Exception e) when (!(e is OperationCanceledException && call.Token.IsCancellationRequested))
        {
            LogFailure(call, source, e);
            return fallback;
        }
    }

    /// <summary>One <c>discovery.source_failed</c> per source per lookup.</summary>
    private void LogFailure(Call call, string source, Exception e)
    {
        if (!call.Logged.TryAdd(source, true))
        {
            return;
        }

        var (endpoint, status) = e switch
        {
            DiscoveryException d => (d.Endpoint, d.StatusCode ?? 0),
            JellyfinException j => ("/Items", j.StatusCode ?? 0),
            _ => ("", 0),
        };
        logger.SourceFailed(source, endpoint, status, OwnedIndex.Describe(e));
    }

    private static List<T> Interleave<T>(IReadOnlyList<List<T>> lists)
    {
        var result = new List<T>();
        for (var i = 0; ; i++)
        {
            var any = false;
            foreach (var list in lists)
            {
                if (i < list.Count)
                {
                    result.Add(list[i]);
                    any = true;
                }
            }

            if (!any)
            {
                return result;
            }
        }
    }

    private static List<T> RoundRobin<T>(IReadOnlyList<List<T>> lists) => Interleave(lists);
}

/// <summary>Candidate and owned-index log events (design section 13: 7000, 7010, 7050, 7060).</summary>
internal static partial class DiscoveryLog
{
    [LoggerMessage(EventId = 7000, EventName = "discovery.seed", Level = LogLevel.Debug,
        Message = "Discovery seed {SeedItemId} by {Artist}: Deezer artist {DeezerArtistId}, MusicBrainz artist {ArtistMbid}")]
    public static partial void Seed(this ILogger logger, Guid seedItemId, string artist, long deezerArtistId, string artistMbid);

    [LoggerMessage(EventId = 7010, EventName = "discovery.candidates", Level = LogLevel.Debug,
        Message = "Discovery for {SeedItemId}: radio {Radio}, related {Related}, lb {Lb}; kept {Total} ({Owned} owned, {Unowned} unowned), dropped {Dropped}")]
    public static partial void Candidates(this ILogger logger, Guid seedItemId, int radio, int related, int lb, int total, int owned, int unowned, int dropped);

    [LoggerMessage(EventId = 7050, EventName = "owned_index.loaded", Level = LogLevel.Information,
        Message = "Owned index loaded: {Items} items, {Keys} keys, {RadioItems} in music-radio, {DurationMs} ms")]
    public static partial void OwnedIndexLoaded(this ILogger logger, int items, int keys, int radioItems, long durationMs);

    [LoggerMessage(EventId = 7060, EventName = "owned_index.failed", Level = LogLevel.Warning,
        Message = "Owned index refresh failed; the old index is kept: {Error}")]
    public static partial void OwnedIndexFailed(this ILogger logger, string error);
}
