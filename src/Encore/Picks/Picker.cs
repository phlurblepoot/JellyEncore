using Encore.Jellyfin;

namespace Encore.Picks;

/// <summary>
/// Finds owned songs to follow a seed song (v0.1 design, section 4). Source chain, stopping once
/// <see cref="PickRequest.Need"/> songs survive the filters, within <see cref="PickLimits.MaxCalls"/> REST calls:
/// <list type="number">
/// <item>similar artists of the seed artist, then their songs;</item>
/// <item>similar songs of the seed, seed artist excluded;</item>
/// <item>instant mix of the seed;</item>
/// <item>other songs by the seed artist;</item>
/// <item>step 1 again with up to 2 earlier distinct artists of the session.</item>
/// </list>
/// Filters: Audio only, not excluded, length 60 s to 12 min (unknown allowed), at most 2 per artist per plan,
/// and no artist twice in a row while another artist is available. A failed source (not an auth failure) is
/// logged and skipped; <see cref="JellyfinAuthException"/> and cancellation propagate.
/// </summary>
public sealed class Picker(IJellyfinClient client, ILogger<Picker> logger) : IPicker
{
    public async Task<PickResult> PickAsync(PickRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new Run(client, logger, request);
        if (request.Need <= 0)
        {
            return new PickResult([], 0, 0, []);
        }

        await run.ExecuteAsync(ct).ConfigureAwait(false);
        var items = run.Select();
        var shortfall = Math.Max(0, request.Need - items.Count);
        if (items.Count == 0)
        {
            logger.PicksNone(request.Seed.Id, string.Join(", ", run.Tried.Select(s => s.ToWire())));
        }
        else
        {
            logger.PicksPlanned(items.Count, string.Join("; ", items), shortfall, run.Calls);
        }

        return new PickResult(items, run.Calls, shortfall, run.Tried);
    }

    /// <summary>The artist an item counts as: first track artist, else first album artist.</summary>
    public static NameGuidPair? ArtistOf(BaseItemDto item) =>
        item.ArtistItems?.FirstOrDefault(a => a.Id != Guid.Empty)
        ?? item.AlbumArtists?.FirstOrDefault(a => a.Id != Guid.Empty);

    /// <summary>True when the item may be planned at all (type and length; exclusions are checked separately).</summary>
    public static bool PassesItemFilters(BaseItemDto item)
    {
        if (item.Id == Guid.Empty || !string.Equals(item.Type, "Audio", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item.RunTimeTicks is { } ticks)
        {
            var length = TimeSpan.FromTicks(ticks);
            return length >= PickLimits.MinLength && length <= PickLimits.MaxLength;
        }

        return true;
    }

    /// <summary>
    /// Orders candidates for the plan: keeps the candidate order (source preference) but takes the first
    /// candidate whose artist differs from the previous song's, and allows at most
    /// <see cref="PickLimits.MaxPerArtist"/> per artist counting <paramref name="planned"/>. Songs without an
    /// artist count as distinct artists.
    /// </summary>
    public static List<PickedItem> Select(
        IReadOnlyList<PickedItem> candidates, IReadOnlyList<PickedItem> planned, Guid? previousArtist, int need)
    {
        var perArtist = new Dictionary<Guid, int>();
        foreach (var p in planned)
        {
            if (p.ArtistId is { } a)
            {
                perArtist[a] = perArtist.GetValueOrDefault(a) + 1;
            }
        }

        var prev = planned.Count > 0 ? planned[^1].ArtistId : previousArtist;
        var available = new List<PickedItem>(candidates);
        var result = new List<PickedItem>();
        while (result.Count < need)
        {
            PickedItem? first = null;
            PickedItem? pick = null;
            foreach (var c in available)
            {
                if (c.ArtistId is { } a && perArtist.GetValueOrDefault(a) >= PickLimits.MaxPerArtist)
                {
                    continue;
                }

                first ??= c;
                if (c.ArtistId is null || c.ArtistId != prev)
                {
                    pick = c;
                    break;
                }
            }

            pick ??= first;
            if (pick is null)
            {
                break;
            }

            available.Remove(pick);
            result.Add(pick);
            if (pick.ArtistId is { } artist)
            {
                perArtist[artist] = perArtist.GetValueOrDefault(artist) + 1;
            }

            prev = pick.ArtistId;
        }

        return result;
    }

    private sealed class Run(IJellyfinClient client, ILogger logger, PickRequest request)
    {
        private readonly HashSet<Guid> _excluded = BuildExcluded(request);
        private readonly HashSet<Guid> _seen = [];
        private readonly List<PickedItem> _pool = [];
        private readonly Guid? _seedArtist = ArtistOf(request.Seed)?.Id;

        public int Calls { get; private set; }

        public List<PickSource> Tried { get; } = [];

        public List<PickedItem> Select() => Picker.Select(_pool, request.Planned, _seedArtist, request.Need);

        private int Missing => request.Need - Select().Count;

        public async Task ExecuteAsync(CancellationToken ct)
        {
            var seed = request.Seed;
            if (_seedArtist is { } seedArtist)
            {
                await ArtistStepAsync(seedArtist, ct).ConfigureAwait(false);
            }

            if (Missing > 0 && HasBudget(1))
            {
                var exclude = _seedArtist is { } sa ? new[] { sa } : null;
                await SourceAsync(PickSource.SimilarSong,
                    () => client.GetSimilarSongsAsync(seed.Id, request.UserId, PickLimits.SimilarSongLimit, exclude, ct)).ConfigureAwait(false);
            }

            if (Missing > 0 && HasBudget(1))
            {
                await SourceAsync(PickSource.InstantMix,
                    () => client.GetInstantMixAsync(seed.Id, request.UserId, PickLimits.InstantMixLimit, ct)).ConfigureAwait(false);
            }

            if (Missing > 0 && HasBudget(1) && _seedArtist is { } own)
            {
                var limit = PickLimits.SongsPerNeed * Missing;
                await SourceAsync(PickSource.SeedArtist,
                    () => client.GetSongsByArtistsAsync([own], request.UserId, limit, ct)).ConfigureAwait(false);
            }

            var historyArtists = request.SessionHistory
                .Select(h => h.ArtistId)
                .OfType<Guid>()
                .Where(a => a != Guid.Empty && a != _seedArtist)
                .Distinct()
                .Take(PickLimits.MaxHistorySeeds);
            foreach (var artist in historyArtists)
            {
                if (Missing <= 0)
                {
                    break;
                }

                await ArtistStepAsync(artist, ct).ConfigureAwait(false);
            }
        }

        private bool HasBudget(int calls) => Calls + calls <= PickLimits.MaxCalls;

        /// <summary>Similar artists of <paramref name="artist"/>, then a random set of their songs (2 calls).</summary>
        private async Task ArtistStepAsync(Guid artist, CancellationToken ct)
        {
            if (!HasBudget(2))
            {
                return;
            }

            Tried.Add(PickSource.SimilarArtist);
            QueryResult<BaseItemDto> similar;
            Calls++;
            try
            {
                similar = await client.GetSimilarArtistsAsync(artist, request.UserId, PickLimits.SimilarArtistLimit, ct).ConfigureAwait(false);
            }
            catch (JellyfinRequestException ex)
            {
                logger.PicksSourceFailed(PickSource.SimilarArtist.ToWire(), request.Seed.Id, ex.Message);
                return;
            }

            var artistIds = similar.Items
                .Select(a => a.Id)
                .Where(id => id != Guid.Empty && id != artist && id != _seedArtist)
                .Distinct()
                .ToArray();
            if (artistIds.Length == 0)
            {
                logger.PicksSource(PickSource.SimilarArtist.ToWire(), request.Seed.Id, 0, 0);
                return;
            }

            var limit = PickLimits.SongsPerNeed * Math.Max(1, Missing);
            await SourceAsync(PickSource.SimilarArtist,
                () => client.GetSongsByArtistsAsync(artistIds, request.UserId, limit, ct), countAsTried: false).ConfigureAwait(false);
        }

        private async Task SourceAsync(PickSource source, Func<Task<QueryResult<BaseItemDto>>> call, bool countAsTried = true)
        {
            if (countAsTried)
            {
                Tried.Add(source);
            }

            QueryResult<BaseItemDto> result;
            Calls++;
            try
            {
                result = await call().ConfigureAwait(false);
            }
            catch (JellyfinRequestException ex)
            {
                logger.PicksSourceFailed(source.ToWire(), request.Seed.Id, ex.Message);
                return;
            }

            var kept = 0;
            foreach (var item in result.Items)
            {
                if (_excluded.Contains(item.Id) || !PassesItemFilters(item) || !_seen.Add(item.Id))
                {
                    continue;
                }

                var artist = ArtistOf(item);
                _pool.Add(new PickedItem(item.Id, item.Name, artist?.Id, artist?.Name, source));
                kept++;
            }

            logger.PicksSource(source.ToWire(), request.Seed.Id, result.Items.Count, kept);
        }

        private static HashSet<Guid> BuildExcluded(PickRequest request)
        {
            var set = new HashSet<Guid>(request.Exclusions) { request.Seed.Id };
            foreach (var p in request.Planned)
            {
                set.Add(p.Id);
            }

            return set;
        }
    }
}
