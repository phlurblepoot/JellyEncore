using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Picks;
using Encore.Trigger;

namespace Encore.Orchestration;

/// <summary>The radio run of one session: its id, its plan and the songs it has added so far.</summary>
internal sealed class SessionRun(string runId, IRadioPlan plan)
{
    public string RunId { get; } = runId;

    /// <summary>The v0.1 <see cref="RadioPlan"/>, or the <see cref="PoolPlan"/> when fetch mode is live.</summary>
    public IRadioPlan Plan { get; } = plan;

    /// <summary>True once the fetch side was told the run ended (replaced, run cap, session gone, shutdown).</summary>
    public bool Ended { get; set; }

    /// <summary>Fetch mode dry_run: shadow plans made so far (their pick ids are <c>{runId}:dry{n}:{m}</c>).</summary>
    public int ShadowFires { get; set; }

    /// <summary>Fetch mode dry_run: keys already asked for in this run, so a shadow plan does not search them again.</summary>
    public HashSet<TrackKey> ShadowRequested { get; } = [];

    /// <summary>The last song of the newest send of any kind (a re-send puts its songs at the end of the queue).</summary>
    public Guid? LastAddedItemId { get; set; }

    /// <summary>Songs added by the run, re-sends included (run cap).</summary>
    public int AddedCount { get; set; }

    /// <summary>Every song the run sent (T28), for the Finamp near-end rule right after a re-send.</summary>
    public HashSet<Guid> AddedIds { get; } = [];
}

/// <summary>
/// A send waiting for its delivery verdict (next song, a stop, or the session ending). The songs keep their pick
/// source, so a re-send writes the same sources.
/// </summary>
/// <param name="DoneAt">When the PlayLast call returned or timed out (for the delivery lag).</param>
/// <param name="IsResend">A re-send: if it is lost too, nothing is sent again.</param>
/// <param name="MissingLoggedAt">Set when a stop judged it missing; a new song within the grace takes the re-send.</param>
internal sealed record PendingDelivery(
    long TriggerId,
    string RunId,
    IReadOnlyList<PickedItem> Items,
    DateTimeOffset DoneAt,
    bool IsResend,
    DateTimeOffset? MissingLoggedAt = null)
{
    public IReadOnlyList<Guid> ItemIds { get; } = [.. Items.Select(i => i.Id)];

    public PendingSend ToTrigger() => new(TriggerId, ItemIds, DoneAt, IsResend, MissingLoggedAt);
}

/// <summary>Everything the orchestrator keeps for one Jellyfin session. Owned by the loop, never shared.</summary>
internal sealed class SessionTrack
{
    /// <summary>The trigger's memory; null only before the first evaluation.</summary>
    public SessionMemory? Memory { get; set; }

    public bool Allowed { get; set; }

    public SessionRun? Run { get; set; }

    /// <summary>True once the run was looked up in the store (done once per session tracker).</summary>
    public bool RunLoaded { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    /// <summary>When the last sent or dry-run fire happened on this session (cooldown).</summary>
    public DateTimeOffset? LastFireAt { get; set; }

    /// <summary>The last snapshot of the session, reused for the fire retry.</summary>
    public SessionInfoDto? LastDto { get; set; }

    public TriggerSession? LastSession { get; set; }

    /// <summary>When the last snapshot of the session was received, and whether it came from a poll (delivery lag).</summary>
    public DateTimeOffset LastSnapshotAt { get; set; }

    public bool LastSnapshotFromPoll { get; set; }

    /// <summary>Sends waiting for a verdict, oldest first (normally none or one).</summary>
    public List<PendingDelivery> Pending { get; } = [];

    /// <summary>
    /// The missing send(s) the current play instance re-sends (the trigger holds the ids in the instance's ResendIds).
    /// Cleared when a new instance starts or the re-send went out.
    /// </summary>
    public PendingDelivery? Resend { get; set; }

    /// <summary>When the session was first seen stopped while a verdict was pending (empty report, or a send judged at the stop).</summary>
    public DateTimeOffset? StoppedSince { get; set; }
}
