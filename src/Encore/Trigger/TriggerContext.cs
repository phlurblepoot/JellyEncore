namespace Encore.Trigger;

/// <summary>Everything outside the session snapshot that the trigger needs. Built by the orchestrator per evaluation.</summary>
public sealed record TriggerContext
{
    /// <summary>Resolved allow-list. Matching is by user id, so a renamed user still matches.</summary>
    public required IReadOnlySet<Guid> AllowedUserIds { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>Encore's own DeviceId; a session with this DeviceId is ignored.</summary>
    public string? OwnDeviceId { get; init; }

    /// <summary>ENCORE_BATCH: songs sent per fire, used by the hourly caps.</summary>
    public int Batch { get; init; } = 2;

    /// <summary>
    /// True when the store holds a trigger with this key younger than <see cref="TriggerLimits.DedupeTtl"/>
    /// whose outcome was sent or dry_run. Only consulted for the Full model.
    /// </summary>
    public Func<string, bool> IsDuplicate { get; init; } = static _ => false;

    /// <summary>Time of the last fire on this session that sent (or dry-ran) songs; null when none.</summary>
    public DateTimeOffset? LastSessionFireAt { get; init; }

    /// <summary>Songs Encore added to this session in the last hour.</summary>
    public int SessionItemsLastHour { get; init; }

    /// <summary>Songs Encore added across all sessions in the last hour.</summary>
    public int GlobalItemsLastHour { get; init; }

    /// <summary>
    /// When set, the engine calls this instead of reading <see cref="SessionItemsLastHour"/>, and only once it reaches
    /// the rate-limit guard, so a store query is not run for every snapshot.
    /// </summary>
    public Func<int>? SessionItemsLastHourSource { get; init; }

    /// <summary>Lazy source for <see cref="GlobalItemsLastHour"/>; see <see cref="SessionItemsLastHourSource"/>.</summary>
    public Func<int>? GlobalItemsLastHourSource { get; init; }

    /// <summary>The session's hourly count: the lazy source when there is one.</summary>
    public int SessionItemsInLastHour() => SessionItemsLastHourSource?.Invoke() ?? SessionItemsLastHour;

    /// <summary>The global hourly count: the lazy source when there is one.</summary>
    public int GlobalItemsInLastHour() => GlobalItemsLastHourSource?.Invoke() ?? GlobalItemsLastHour;

    /// <summary>The session's current radio run, or null when there is none.</summary>
    public RunInfo? Run { get; init; }

    /// <summary>T28: the session's sends still waiting for a delivery verdict (normally none or one).</summary>
    public IReadOnlyList<PendingSend> PendingSends { get; init; } = [];

    /// <summary>T28: when the snapshot was received (for <see cref="TriggerLimits.DeliveryLag"/>); defaults to <see cref="Now"/>.</summary>
    public DateTimeOffset? SnapshotAt { get; init; }

    /// <summary>T28: true when the snapshot came from a poll, not the socket.</summary>
    public bool SnapshotFromPoll { get; init; }

    /// <summary>ENCORE_POLL_SECONDS, added to the delivery lag for poll reports.</summary>
    public int PollSeconds { get; init; } = 10;
}

/// <summary>What the trigger needs to know about the session's current radio run.</summary>
/// <param name="LastAddedItemId">The last song of the newest send of any kind.</param>
/// <param name="AddedCount">Songs the run added, re-sends included (run cap).</param>
/// <param name="AddedIds">T28: every song the run sent (near-end freshness after a re-send; T77: the order-independent fire kind and fallback (b)).</param>
public sealed record RunInfo(Guid? LastAddedItemId, int AddedCount, IReadOnlyCollection<Guid>? AddedIds = null)
{
    /// <summary>True when the run sent this song (the last added song always counts, even without <see cref="AddedIds"/>).</summary>
    public bool Added(Guid id) => AddedIds?.Contains(id) == true || LastAddedItemId == id;
}
