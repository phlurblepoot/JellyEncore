namespace Encore.Orchestration;

/// <summary>
/// Immutable orchestrator status for <c>/health</c>. The loop swaps it whole, so readers never see a half-written state.
/// Counters are since start.
/// </summary>
public sealed record OrchestratorStatus
{
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Set on every loop step. The health endpoint returns 503 when this is older than 60 s.</summary>
    public DateTimeOffset LastHeartbeatUtc { get; init; }

    public DateTimeOffset? JellyfinLastOkUtc { get; init; }

    /// <summary>The last Jellyfin failure text (never a header or key), or null after a success.</summary>
    public string? JellyfinLastError { get; init; }

    public IReadOnlyList<string> AllowListConfigured { get; init; } = [];

    /// <summary>Resolved user name to user id ("N" form).</summary>
    public IReadOnlyDictionary<string, string> AllowListResolved { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> AllowListUnresolved { get; init; } = [];

    public DateTimeOffset? AllowListResolvedUtc { get; init; }

    /// <summary>The last failed allow-list lookup, or null.</summary>
    public string? AllowListError { get; init; }

    public int SessionsTracked { get; init; }

    public int AllowListedPlaying { get; init; }

    /// <summary>Fire decisions taken (sent, dry run, no picks or failed).</summary>
    public int Fired { get; init; }

    public int Sent { get; init; }

    public int DryRun { get; init; }

    public int NoPicks { get; init; }

    public int Failed { get; init; }

    public DateTimeOffset? LastFireUtc { get; init; }

    /// <summary>sent, dry_run, no_picks or failed.</summary>
    public string? LastOutcome { get; init; }

    public int ItemsSent { get; init; }

    public int Unconfirmed { get; init; }

    public DateTimeOffset? LastSentUtc { get; init; }

    /// <summary>False after the last store operation threw.</summary>
    public bool StoreOk { get; init; } = true;
}
