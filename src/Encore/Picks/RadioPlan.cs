using Encore.Jellyfin;

namespace Encore.Picks;

/// <summary>What one fire hands out.</summary>
/// <param name="Batch">The songs to send now, in order. Empty means <c>no_picks</c>.</param>
/// <param name="Pick">The picker run that refilled the plan, or null when the plan was already full.</param>
/// <param name="Remaining">The plan left after the hand-out.</param>
public sealed record PlanHandout(IReadOnlyList<PickedItem> Batch, PickResult? Pick, IReadOnlyList<PickedItem> Remaining)
{
    public bool IsEmpty => Batch.Count == 0;
}

/// <summary>
/// The look-ahead plan of one radio run (v0.1 design, section 3 "Top-up rule" and section 4).
/// A <c>start</c> fire makes a new plan; a <c>topup</c> fire reuses it. Each fire:
/// drops planned songs that are now excluded (the user may have queued one), refills only the
/// shortfall up to <c>lookahead</c>, then hands out the first <c>batch</c>.
/// Songs handed out by this plan are never planned again, even in dry run where nothing is stored.
/// Not thread-safe: owned by the orchestrator loop.
/// </summary>
public sealed class RadioPlan
{
    private readonly List<PickedItem> _items = [];
    private readonly HashSet<Guid> _handedOut = [];

    public RadioPlan(int lookahead, int batch)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lookahead, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch, lookahead);
        Lookahead = lookahead;
        Batch = batch;
    }

    public int Lookahead { get; }

    public int Batch { get; }

    /// <summary>Songs planned but not handed out yet, in order.</summary>
    public IReadOnlyList<PickedItem> Items => _items;

    /// <summary>Every song this plan has handed out.</summary>
    public IReadOnlyCollection<Guid> HandedOut => _handedOut;

    /// <summary>
    /// Re-checks, refills and hands out one batch.
    /// </summary>
    /// <param name="exclusions">From <see cref="RecentExclusions.Build"/>: queue, seed and recent window. The plan adds its own items.</param>
    public async Task<PlanHandout> NextBatchAsync(
        IPicker picker,
        Guid userId,
        BaseItemDto seed,
        IReadOnlyList<HistorySong> sessionHistory,
        IReadOnlySet<Guid> exclusions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(exclusions);

        _items.RemoveAll(i => i.Id == seed.Id || exclusions.Contains(i.Id) || _handedOut.Contains(i.Id));

        PickResult? pick = null;
        var need = Lookahead - _items.Count;
        if (need > 0)
        {
            var avoid = new HashSet<Guid>(exclusions);
            avoid.UnionWith(_handedOut);
            pick = await picker.PickAsync(new PickRequest
            {
                UserId = userId,
                Seed = seed,
                SessionHistory = sessionHistory,
                Exclusions = avoid,
                Planned = [.. _items],
                Need = need,
            }, ct).ConfigureAwait(false);

            // Guard against a picker that returns a duplicate or an excluded id.
            var planned = new HashSet<Guid>(_items.Select(i => i.Id));
            foreach (var item in pick.Items)
            {
                if (_items.Count >= Lookahead)
                {
                    break;
                }

                if (item.Id != seed.Id && !avoid.Contains(item.Id) && planned.Add(item.Id))
                {
                    _items.Add(item);
                }
            }
        }

        var take = Math.Min(Batch, _items.Count);
        var batch = _items.GetRange(0, take);
        _items.RemoveRange(0, take);
        foreach (var item in batch)
        {
            _handedOut.Add(item.Id);
        }

        return new PlanHandout(batch, pick, [.. _items]);
    }
}
