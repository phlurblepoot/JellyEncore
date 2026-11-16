using System.Collections.Immutable;

namespace Encore.Trigger;

/// <summary>
/// Immutable per-session trigger memory. The orchestrator keeps the instance returned by
/// <see cref="TriggerEngine"/> and passes it back with the next snapshot.
/// </summary>
public sealed record SessionMemory
{
    public required string SessionId { get; init; }

    /// <summary>The current play instance, or the last one when <see cref="Idle"/> is true. Null before the first item.</summary>
    public PlayInstance? Instance { get; init; }

    /// <summary>True when the latest snapshot had no NowPlayingItem: the next item starts a new instance.</summary>
    public bool Idle { get; init; }

    /// <summary>
    /// EntryQueue of the previous instance on this session. Null means unknown: the first instance
    /// seen since Encore started, so a Finamp position decision cannot be made (<c>no_history</c>).
    /// </summary>
    public IReadOnlyList<QueueEntry>? PrevEntryQueue { get; init; }

    /// <summary>
    /// The songs the previous instance sent or dry-ran (its <see cref="PlayInstance.SentIds"/>), empty after a restart.
    /// Guard 9b (<c>already_queued</c>) uses it (T28).
    /// </summary>
    public IReadOnlyList<Guid> PrevSentIds { get; init; } = [];

    /// <summary>Whether <c>not_allowed</c> was already reported for this session.</summary>
    public bool NotAllowedReported { get; init; }

    /// <summary>Number of play instances seen on this session.</summary>
    public long InstanceCount { get; init; }

    /// <summary>
    /// T110: the song whose single-song start fired (or is firing). A repeat report of that song (a duplicate or a
    /// flip-flop that opens a new instance) does not start the radio again. Cleared when a different song plays.
    /// </summary>
    public Guid? SingleFiredItemId { get; init; }

    public static SessionMemory Create(string sessionId) => new() { SessionId = sessionId };
}

/// <summary>A run of snapshots with the same playing item and the same session PlaylistItemId.</summary>
public sealed record PlayInstance
{
    public required Guid ItemId { get; init; }

    public string? PlaylistItemId { get; init; }

    /// <summary>The queue from the instance's first snapshot (fresh, unlike later ones in the stop-gap race).</summary>
    public required IReadOnlyList<QueueEntry> EntryQueue { get; init; }

    /// <summary>1-based instance number within the session.</summary>
    public required long Sequence { get; init; }

    public InstanceState State { get; init; } = InstanceState.Evaluating;

    /// <summary>Fire attempts made in this instance.</summary>
    public int Attempts { get; init; }

    public DateTimeOffset? FailedAt { get; init; }

    /// <summary>Skip reasons already reported in this instance.</summary>
    public ImmutableHashSet<SkipReason> ReportedReasons { get; init; } = [];

    /// <summary>The songs this instance sent or dry-ran, recorded by <see cref="TriggerEngine.MarkFired(SessionMemory, IReadOnlyList{Guid})"/>.</summary>
    public IReadOnlyList<Guid> SentIds { get; init; } = [];

    /// <summary>Set at the instance's start when the delivery check calls for a re-send: the songs to send again (T28).</summary>
    public IReadOnlyList<Guid> ResendIds { get; init; } = [];

    /// <summary>The trigger id of the send that <see cref="ResendIds"/> repeats.</summary>
    public long? ResendOf { get; init; }

    /// <summary>
    /// T75: Finamp sent its queue-first report for this song (the song again, after a stop, with the list that follows
    /// the next song). The song is ending: the report joined this instance, which keeps its <see cref="EntryQueue"/>
    /// for the next song's position rules and never fires again.
    /// </summary>
    public bool QueueFirstSeen { get; init; }
}
