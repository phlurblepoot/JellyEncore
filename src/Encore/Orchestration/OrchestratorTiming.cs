namespace Encore.Orchestration;

/// <summary>
/// Clock settings of the orchestrator loop. The defaults are the design's values (section 1.1, section 7); tests shorten them.
/// Not configurable from the environment.
/// </summary>
public sealed record OrchestratorTiming
{
    /// <summary>How often the loop does its housekeeping (heartbeat, retries, allow-list due, pruning check).</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Allow-list refresh interval when every name resolves.</summary>
    public TimeSpan AllowListRefresh { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Allow-list recheck interval when some names do not resolve (a typo, or a user not yet created).</summary>
    public TimeSpan AllowListProblemRecheck { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>First retry after a failed GET /Users; doubles up to <see cref="AllowListRetryMax"/>.</summary>
    public TimeSpan AllowListRetryMin { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan AllowListRetryMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A session missing from the snapshots for this long is dropped from memory.</summary>
    public TimeSpan SessionForgetAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How often old rows are pruned while running (also once at startup).</summary>
    public TimeSpan PruneEvery { get; init; } = TimeSpan.FromDays(1);

    /// <summary>A radio run can be resumed after a restart when its last added song is younger than this.</summary>
    public TimeSpan RunResumeWindow { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Longest time one fire may spend picking songs. The loop does not heartbeat during a fire, and the health endpoint
    /// reports "stopped" after 60 s without one; a hung Jellyfin could otherwise hold the picker for minutes
    /// (8 calls, each up to 10 s per attempt). PlayLast keeps its own HTTP timeout and is not covered by this.
    /// </summary>
    public TimeSpan PickTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long a session may show no playing song, with an empty or missing queue report, before a batch still
    /// waiting for its verdict is judged unconfirmed. Long enough to cover a track change (stop, then start).
    /// </summary>
    public TimeSpan StoppedVerdictGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Cut-off of the outside suggestion lookup inside a fire (fetch mode dry_run and live); within <see cref="PickTimeout"/>.</summary>
    public TimeSpan ExternalLookupTimeout { get; init; } = Encore.Fetch.FetchLimits.ExternalLookupTimeout;

    /// <summary>How many earlier songs of the session are offered to the picker as secondary seeds.</summary>
    public int HistoryLimit { get; init; } = 20;
}
