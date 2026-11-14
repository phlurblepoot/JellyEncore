using System.Diagnostics;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Jellyfin;

namespace Encore.Picks;

/// <summary>
/// One planned song of a <see cref="PoolPlan"/>: either a server copy (<see cref="Item"/> set, ready at once) or a
/// Soulseek fetch (<see cref="PickId"/> set, ready when its job is).
/// </summary>
public sealed class PoolPick
{
    internal PoolPick(TrackKey? key, string? artistKey, PickSource source, PickedItem? item, Candidate? candidate)
    {
        Key = key;
        ArtistKey = artistKey;
        Source = source;
        Item = item;
        Candidate = candidate;
    }

    /// <summary>The song's TrackKey; null for an owned-chain song the index does not know.</summary>
    public TrackKey? Key { get; }

    /// <summary>The artist for the plan's artist rules; null counts as a distinct artist.</summary>
    public string? ArtistKey { get; }

    public PickSource Source { get; }

    /// <summary>The song to send: the server copy, or a fetched song put back after a failed send. Null while fetching.</summary>
    public PickedItem? Item { get; }

    /// <summary>The outside suggestion behind the pick; null for owned-chain songs.</summary>
    public Candidate? Candidate { get; }

    /// <summary>The fetch request id (<c>{runId}:{n}</c>); null for a server copy.</summary>
    public string? PickId { get; internal set; }

    /// <summary>The fire that planned the pick (a fetch is not counted at that fire).</summary>
    public int PlannedAtFire { get; internal set; }

    /// <summary>How many hand-outs passed this fetch over while it was pending.</summary>
    public int PassOvers { get; internal set; }

    /// <summary>True when the pick waits for a Soulseek fetch.</summary>
    public bool IsFetch => Item is null;

    /// <summary>The pick as a <see cref="PickedItem"/>; a fetch not ready yet has <see cref="Guid.Empty"/> as its id.</summary>
    public PickedItem ToItem() => Item
        ?? new PickedItem(Guid.Empty, Candidate?.Title, null, Candidate?.Artist, Source, PickCopy.Soulseek);

    public override string ToString() =>
        $"{Key?.Value ?? "?"} {ToItem().ArtistName ?? "?"} - {ToItem().Name ?? "?"} [{ToItem().WireSource()}]";
}

/// <summary>Lets a log line through at most once per interval. Thread safe.</summary>
public sealed class IntervalGate(TimeSpan every)
{
    private readonly object _lock = new();
    private DateTimeOffset? _last;

    public bool Allow(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_last is { } last && now - last < every)
            {
                return false;
            }

            _last = now;
            return true;
        }
    }
}

/// <summary>
/// The one pick pool of fetch mode live (v0.2 design section 4; the user's model, no owned:new ratio). Each fire:
/// <list type="number">
/// <item>drops planned picks that are now excluded (queued by the user, recently played, already handed out);</item>
/// <item>refills up to <c>lookahead</c> from the outside suggestions (<see cref="ICandidateSource"/>, 10 s cut-off) and
/// then the v0.1 owned chain, in that order, deduped by TrackKey, with the artist rules keyed by ArtistKey. An owned
/// suggestion is the server copy, ready at once; an unowned one is requested from the fetch side now;</item>
/// <item>keeps a reserve of up to <c>batch</c> owned songs from the owned chain as filler;</item>
/// <item>hands out the first <c>batch</c> ready picks in plan order: a fetch planned at this fire is not counted, a
/// pending one is passed over at most <see cref="FetchLimits.MaxPassOvers"/> times and then skipped, a failed one is
/// skipped at once; the reserve fills what is left.</item>
/// </list>
/// The radio never waits for a fetch. In shadow mode (fetch mode dry_run) <see cref="PlanShadowAsync"/> plans from the
/// outside suggestions only, requests dry-run fetches and logs; it never hands anything out.
/// Not thread-safe: owned by the orchestrator loop.
/// </summary>
public sealed class PoolPlan : IRadioPlan
{
    private readonly IFetchRequests _fetch;
    private readonly ICandidateSource _candidates;
    private readonly IOwnedIndex _owned;
    private readonly ILogger _log;
    private readonly IntervalGate _indexNotReady;
    private readonly TimeSpan _externalTimeout;
    private readonly string? _pickIdPrefix;
    private readonly List<PoolPick> _items = [];
    private readonly List<PickedItem> _reserve = [];
    private readonly HashSet<Guid> _handedOut = [];
    private readonly HashSet<TrackKey> _handedKeys = [];
    private readonly Dictionary<Guid, TrackKey> _handedKeyById = [];
    private int _fire;
    private int _nextPick;

    /// <param name="indexNotReady">Shared gate for <c>pool.index_not_ready</c> (once per minute across plans); a new one when null.</param>
    /// <param name="externalTimeout">The outside lookup cut-off; <see cref="FetchLimits.ExternalLookupTimeout"/> when null.</param>
    /// <param name="pickIdPrefix">Prefix of the fetch pick ids; the run id when null (pick ids are <c>{prefix}:{n}</c>).</param>
    public PoolPlan(
        int lookahead,
        int batch,
        IFetchRequests fetch,
        ICandidateSource candidates,
        IOwnedIndex owned,
        ILogger logger,
        IntervalGate? indexNotReady = null,
        TimeSpan? externalTimeout = null,
        string? pickIdPrefix = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lookahead, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch, lookahead);
        Lookahead = lookahead;
        Batch = batch;
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _indexNotReady = indexNotReady ?? new IntervalGate(TimeSpan.FromMinutes(1));
        _externalTimeout = externalTimeout ?? FetchLimits.ExternalLookupTimeout;
        _pickIdPrefix = pickIdPrefix;
    }

    public int Lookahead { get; }

    public int Batch { get; }

    /// <summary>Picks planned but not handed out yet, in order.</summary>
    public IReadOnlyList<PoolPick> Items => _items;

    /// <summary>Owned filler songs, not counted for the artist rules until used.</summary>
    public IReadOnlyList<PickedItem> Reserve => _reserve;

    public IReadOnlyCollection<Guid> HandedOut => _handedOut;

    public void PutBack(IReadOnlyList<PickedItem> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var back = new List<PoolPick>();
        foreach (var item in batch)
        {
            _handedOut.Remove(item.Id);
            TrackKey? key = null;
            if (_handedKeyById.Remove(item.Id, out var k))
            {
                _handedKeys.Remove(k);
                key = k;
            }

            if (_items.All(i => i.Item?.Id != item.Id))
            {
                back.Add(new PoolPick(key, ArtistKeyOf(item, key), item.Source, item, null));
            }
        }

        _items.InsertRange(0, back);
    }

    public async Task<PlanHandout> NextBatchAsync(PlanContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _fire++;
        var seed = context.Seed;
        var exclusions = context.Exclusions;
        var (seedKey, seedArtistKey) = SeedKeys(seed);
        var excludedKeys = ExcludedKeys(exclusions, seedKey);
        var skipped = new List<string>();

        // Step 1: drop picks that are now excluded (the user queued one, it was played, or it went out already).
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var pick = _items[i];
            var id = pick.Item?.Id ?? (pick.PickId is { } pid && _fetch.GetReadiness(pid) is FetchReadiness.Ready ready ? ready.ItemId : null);
            if ((id is { } itemId && IsExcluded(itemId, seed.Id, exclusions)) || (pick.Key is { } key && (excludedKeys.Contains(key) || _handedKeys.Contains(key))))
            {
                _items.RemoveAt(i);
                skipped.Add($"{KeyText(pick)}:dropped_excluded");
                if (pick.IsFetch && pick.PickId is { } abandoned)
                {
                    _fetch.Abandon(abandoned, "excluded");
                }
            }
        }

        _reserve.RemoveAll(r => IsExcluded(r.Id, seed.Id, exclusions) || (_owned.KeyOf(r.Id) is { } key && (excludedKeys.Contains(key) || _handedKeys.Contains(key))));

        // Step 2: refill. Outside suggestions and the owned chain run at once; no I/O when nothing is missing.
        var need = Lookahead - _items.Count;
        var reserveNeed = Batch - _reserve.Count;
        PickResult? pickResult = null;
        if (need > 0 || reserveNeed > 0)
        {
            var avoid = Avoided(exclusions);
            var planKeys = PlanKeys(excludedKeys);
            var external = need > 0
                ? ExternalAsync(seed, avoid, planKeys, ct)
                : Task.FromResult<IReadOnlyList<ResolvedCandidate>>([]);
            IReadOnlyList<ResolvedCandidate> candidates;
            try
            {
                pickResult = await context.Picker.PickAsync(new PickRequest
                {
                    UserId = context.UserId,
                    Seed = seed,
                    SessionHistory = context.SessionHistory,
                    Exclusions = avoid,
                    Planned = [.. _items.Where(i => i.Item is not null).Select(i => i.Item!)],
                    Need = need + reserveNeed,
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                candidates = await external.ConfigureAwait(false);
            }

            // Step 3: the owned chain, guarded against duplicates and excluded songs; its first songs top up the reserve.
            var chain = new List<(PickedItem Item, TrackKey? Key)>();
            var chainIds = new HashSet<Guid>();
            foreach (var item in pickResult.Items)
            {
                var key = _owned.KeyOf(item.Id);
                if (item.Id == seed.Id || avoid.Contains(item.Id) || !chainIds.Add(item.Id) || (key is { } k && planKeys.Contains(k)))
                {
                    continue;
                }

                chain.Add((item, key));
            }

            while (_reserve.Count < Batch && chain.Count > 0)
            {
                _reserve.Add(chain[0].Item);
                chain.RemoveAt(0);
            }

            if (need > 0)
            {
                Plan(context, need, seedArtistKey, candidates, chain, shadow: false);
            }
        }

        // Step 7: hand-out.
        return HandOut(context, seed, exclusions, seedArtistKey, pickResult, skipped);
    }

    /// <summary>
    /// Fetch mode dry_run: builds a throw-away plan from the outside suggestions only (no owned chain), requests a
    /// dry-run fetch (search and score only) for each unowned pick, and logs <c>pool.planned</c> with mode dry_run.
    /// Never hands anything out. Returns the keys of the dry-run requests made.
    /// </summary>
    /// <param name="alreadyRequested">Keys this run already asked for in an earlier dry-run fire; they are not asked again.</param>
    public async Task<IReadOnlyList<TrackKey>> PlanShadowAsync(PlanContext context, IReadOnlySet<Guid> planned, IReadOnlySet<TrackKey> alreadyRequested, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(alreadyRequested);
        _fire++;
        var (seedKey, seedArtistKey) = SeedKeys(context.Seed);
        var avoid = new HashSet<Guid>(context.Exclusions);
        avoid.UnionWith(planned);
        var keys = ExcludedKeys(avoid, seedKey);
        keys.UnionWith(alreadyRequested);
        var candidates = await ExternalAsync(context.Seed, avoid, keys, ct).ConfigureAwait(false);
        Plan(context, Lookahead, seedArtistKey, candidates, [], shadow: true);
        return [.. _items.Where(i => i.IsFetch && i.Key is not null).Select(i => i.Key!.Value)];
    }

    /// <summary>Steps 4-6: builds the pool, selects up to <paramref name="need"/> picks and requests the unowned ones.</summary>
    private void Plan(PlanContext context, int need, string? seedArtistKey, IReadOnlyList<ResolvedCandidate> candidates,
        List<(PickedItem Item, TrackKey? Key)> chain, bool shadow)
    {
        // Step 4: outside suggestions first, then the rest of the owned chain; first occurrence of a key wins.
        var seenKeys = new HashSet<TrackKey>(_items.Where(i => i.Key is not null).Select(i => i.Key!.Value));
        seenKeys.UnionWith(_handedKeys);
        foreach (var r in _reserve)
        {
            if (_owned.KeyOf(r.Id) is { } rk)
            {
                seenKeys.Add(rk);
            }
        }

        var seenIds = new HashSet<Guid>(_items.Where(i => i.Item is not null).Select(i => i.Item!.Id));
        seenIds.UnionWith(_reserve.Select(r => r.Id));
        seenIds.UnionWith(_handedOut);
        var availability = _fetch.Availability;
        var pool = new List<PoolPick>();
        var unownedDropped = 0;
        string? dropReason = null;
        var external = 0;
        foreach (var rc in candidates)
        {
            var candidate = rc.Candidate;
            if (candidate.Key.Value is null || !seenKeys.Add(candidate.Key))
            {
                continue;
            }

            var source = SourceOf(candidate.Origin);
            if (rc.Owned is { } owned)
            {
                if (!seenIds.Add(owned.ItemId))
                {
                    continue;
                }

                var item = new PickedItem(owned.ItemId, owned.Title, owned.ArtistId, owned.ArtistName, source, PickCopy.Server);
                pool.Add(new PoolPick(candidate.Key, candidate.Key.ArtistKey, source, item, candidate));
            }
            else if (availability.Available)
            {
                pool.Add(new PoolPick(candidate.Key, candidate.Key.ArtistKey, source, null, candidate));
            }
            else
            {
                unownedDropped++;
                dropReason ??= availability.Reason;
                continue;
            }

            external++;
        }

        var ownedChain = 0;
        foreach (var (item, key) in chain)
        {
            if ((key is { } k && !seenKeys.Add(k)) || !seenIds.Add(item.Id))
            {
                continue;
            }

            pool.Add(new PoolPick(key, ArtistKeyOf(item, key), item.Source, item, null));
            ownedChain++;
        }

        // Steps 5 and 6: first fit with the v0.1 artist rules keyed by ArtistKey; unowned picks are requested now.
        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in _items)
        {
            if (p.ArtistKey is { } a)
            {
                perArtist[a] = perArtist.GetValueOrDefault(a) + 1;
            }
        }

        var prev = _items.Count > 0 ? _items[^1].ArtistKey : seedArtistKey;
        var added = new List<PoolPick>();
        while (added.Count < need)
        {
            PoolPick? first = null;
            PoolPick? chosen = null;
            foreach (var c in pool)
            {
                if (c.ArtistKey is { } a && perArtist.GetValueOrDefault(a) >= PickLimits.MaxPerArtist)
                {
                    continue;
                }

                first ??= c;
                if (c.ArtistKey is null || !string.Equals(c.ArtistKey, prev, StringComparison.Ordinal))
                {
                    chosen = c;
                    break;
                }
            }

            chosen ??= first;
            if (chosen is null)
            {
                break;
            }

            pool.Remove(chosen);
            if (chosen.IsFetch)
            {
                var pickId = $"{_pickIdPrefix ?? context.RunId}:{++_nextPick}";
                var request = new FetchRequest(pickId, context.RunId, context.SessionId, context.UserId, chosen.Candidate!,
                    DryRun: shadow, Deadline: context.Now + FetchLimits.JobDeadline);
                if (!_fetch.Request(request))
                {
                    unownedDropped++;
                    dropReason ??= _fetch.Availability.Reason ?? "refused";
                    continue;
                }

                chosen.PickId = pickId;
            }

            chosen.PlannedAtFire = _fire;
            _items.Add(chosen);
            added.Add(chosen);
            if (chosen.ArtistKey is { } artist)
            {
                perArtist[artist] = perArtist.GetValueOrDefault(artist) + 1;
            }

            prev = chosen.ArtistKey;
        }

        if (added.Count > 0 || unownedDropped > 0)
        {
            _log.PoolPlanned(shadow ? "dry_run" : "live", context.RunId, context.SessionId, string.Join("; ", added),
                external, ownedChain, unownedDropped, dropReason ?? "");
        }
    }

    private PlanHandout HandOut(PlanContext context, BaseItemDto seed, IReadOnlySet<Guid> exclusions, string? seedArtistKey,
        PickResult? pickResult, List<string> skipped)
    {
        var chosen = new List<(PickedItem Item, TrackKey? Key)>();
        var passedOver = new List<string>();
        var skippedCount = 0;
        var i = 0;
        while (i < _items.Count && chosen.Count < Batch)
        {
            var pick = _items[i];
            if (pick.Item is { } item)
            {
                _items.RemoveAt(i);
                if (IsExcluded(item.Id, seed.Id, exclusions) || chosen.Any(c => c.Item.Id == item.Id))
                {
                    skipped.Add($"{KeyText(pick)}:dropped_excluded");
                    continue;
                }

                chosen.Add((item, pick.Key));
                continue;
            }

            switch (_fetch.GetReadiness(pick.PickId!))
            {
                case FetchReadiness.Ready ready:
                    _items.RemoveAt(i);
                    if (IsExcluded(ready.ItemId, seed.Id, exclusions) || chosen.Any(c => c.Item.Id == ready.ItemId))
                    {
                        skipped.Add($"{KeyText(pick)}:dropped_excluded");
                        continue;
                    }

                    var c = pick.Candidate!;
                    chosen.Add((new PickedItem(ready.ItemId, c.Title, null, c.Artist, pick.Source, PickCopy.Soulseek), pick.Key));
                    continue;
                case FetchReadiness.Failed failed:
                    _items.RemoveAt(i);
                    skipped.Add($"{KeyText(pick)}:skipped_failed({failed.Reason})");
                    skippedCount++;
                    continue;
                default:
                    if (pick.PlannedAtFire == _fire)
                    {
                        // Planned at this fire: it keeps its place and is not counted.
                        i++;
                    }
                    else if (pick.PassOvers < FetchLimits.MaxPassOvers)
                    {
                        pick.PassOvers++;
                        passedOver.Add(KeyText(pick));
                        i++;
                    }
                    else
                    {
                        _items.RemoveAt(i);
                        _fetch.Abandon(pick.PickId!, "late");
                        skipped.Add($"{KeyText(pick)}:skipped_late");
                        skippedCount++;
                    }

                    continue;
            }
        }

        // Filler: owned reserve songs, preferring one whose artist differs from the previous song's.
        var filler = 0;
        while (chosen.Count < Batch && _reserve.Count > 0)
        {
            var prev = chosen.Count > 0 ? ArtistKeyOf(chosen[^1].Item, chosen[^1].Key) : seedArtistKey;
            var index = _reserve.FindIndex(r => ArtistKeyOf(r, null) is not { } a || !string.Equals(a, prev, StringComparison.Ordinal));
            if (index < 0)
            {
                index = 0;
            }

            var item = _reserve[index];
            _reserve.RemoveAt(index);
            if (chosen.Any(c => c.Item.Id == item.Id))
            {
                continue;
            }

            chosen.Add((item, _owned.KeyOf(item.Id)));
            filler++;
        }

        foreach (var (item, key) in chosen)
        {
            _handedOut.Add(item.Id);
            if ((key ?? _owned.KeyOf(item.Id)) is { } k)
            {
                _handedKeys.Add(k);
                _handedKeyById[item.Id] = k;
            }
        }

        var batch = chosen.Select(c => c.Item).ToList();
        _log.PoolHandout(context.RunId, context.SessionId, string.Join("; ", batch.Select(b => $"{b.Id:N}:{b.WireSource()}")),
            filler, string.Join("; ", passedOver), string.Join("; ", skipped));
        return new PlanHandout(batch, pickResult, [.. _items.Select(p => p.ToItem())], passedOver.Count, skippedCount);
    }

    private async Task<IReadOnlyList<ResolvedCandidate>> ExternalAsync(BaseItemDto seed, IReadOnlySet<Guid> excludedIds, IReadOnlySet<TrackKey> excludedKeys, CancellationToken ct)
    {
        if (!_owned.IsLoaded)
        {
            if (_indexNotReady.Allow(DateTimeOffset.UtcNow))
            {
                _log.PoolIndexNotReady();
            }

            return [];
        }

        var clock = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_externalTimeout);
        try
        {
            return await _candidates.GetAsync(new CandidateQuery(seed, excludedIds, excludedKeys), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.PoolExternalTimeout(seed.Id, (long)Stopwatch.GetElapsedTime(clock).TotalMilliseconds);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.PoolExternalFailed(seed.Id, ex.Message.Length <= 300 ? ex.Message : ex.Message[..300]);
            return [];
        }
    }

    private bool IsExcluded(Guid id, Guid seedId, IReadOnlySet<Guid> exclusions) =>
        id == seedId || exclusions.Contains(id) || _handedOut.Contains(id);

    /// <summary>Ids the owned chain and the outside lookup must not return.</summary>
    private HashSet<Guid> Avoided(IReadOnlySet<Guid> exclusions)
    {
        var avoid = new HashSet<Guid>(exclusions);
        avoid.UnionWith(_handedOut);
        avoid.UnionWith(_items.Where(i => i.Item is not null).Select(i => i.Item!.Id));
        avoid.UnionWith(_reserve.Select(r => r.Id));
        return avoid;
    }

    /// <summary>Keys already used: excluded songs, handed-out picks, the plan and the reserve.</summary>
    private HashSet<TrackKey> PlanKeys(HashSet<TrackKey> excludedKeys)
    {
        var keys = new HashSet<TrackKey>(excludedKeys);
        keys.UnionWith(_handedKeys);
        keys.UnionWith(_items.Where(i => i.Key is not null).Select(i => i.Key!.Value));
        foreach (var r in _reserve)
        {
            if (_owned.KeyOf(r.Id) is { } k)
            {
                keys.Add(k);
            }
        }

        return keys;
    }

    /// <summary>The keys of the excluded ids the index knows, and the seed's own key.</summary>
    private HashSet<TrackKey> ExcludedKeys(IEnumerable<Guid> exclusions, TrackKey? seedKey)
    {
        var keys = new HashSet<TrackKey>();
        foreach (var id in exclusions)
        {
            if (_owned.KeyOf(id) is { } k)
            {
                keys.Add(k);
            }
        }

        if (seedKey is { } s)
        {
            keys.Add(s);
        }

        return keys;
    }

    private (TrackKey? Key, string? ArtistKey) SeedKeys(BaseItemDto seed)
    {
        var (_, artistName) = OwnedIndex.ArtistOf(seed);
        var key = _owned.KeyOf(seed.Id) ?? TrackKey.Create(artistName, seed.Name);
        var artistKey = key?.ArtistKey;
        if (string.IsNullOrEmpty(artistKey))
        {
            artistKey = Encore.Discovery.KeyText.ArtistKey(artistName);
        }

        return (key, string.IsNullOrEmpty(artistKey) ? null : artistKey);
    }

    /// <summary>ArtistKey of a song: from its TrackKey, else its folded artist name, else its artist id; null = distinct.</summary>
    private string? ArtistKeyOf(PickedItem item, TrackKey? key)
    {
        var k = key ?? _owned.KeyOf(item.Id);
        if (k is { } known && !string.IsNullOrEmpty(known.ArtistKey))
        {
            return known.ArtistKey;
        }

        var folded = Encore.Discovery.KeyText.ArtistKey(item.ArtistName);
        if (!string.IsNullOrEmpty(folded))
        {
            return folded;
        }

        return item.ArtistId?.ToString("N");
    }

    private static string KeyText(PoolPick pick) => pick.Key?.Value ?? pick.Item?.Id.ToString("N") ?? "?";

    private static PickSource SourceOf(CandidateOrigin origin) => origin switch
    {
        CandidateOrigin.DeezerRadio => PickSource.DeezerRadio,
        CandidateOrigin.DeezerRelated => PickSource.DeezerRelated,
        _ => PickSource.ListenBrainzSimilar,
    };
}
