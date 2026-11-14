using Encore.Jellyfin;
using Encore.Picks;
using Encore.Trigger;

namespace Encore.Orchestration;

/// <summary>The radio run of one session: its id, its plan and the songs it has added so far.</summary>
internal sealed class SessionRun(string runId, RadioPlan plan)
{
    public string RunId { get; } = runId;

    public RadioPlan Plan { get; } = plan;

    public Guid? LastAddedItemId { get; set; }

    public int AddedCount { get; set; }
}

/// <summary>Songs sent in the last fire, waiting for the session's next play instance to confirm them.</summary>
internal sealed record PendingDelivery(long TriggerId, string RunId, IReadOnlyList<Guid> ItemIds);

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

    public PendingDelivery? Pending { get; set; }
}
