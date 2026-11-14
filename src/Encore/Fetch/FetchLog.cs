namespace Encore.Fetch;

/// <summary>Fetch worker and coordinator events (v0.2 design section 13: 7100 and 75xx, plus 7640).</summary>
internal static partial class FetchLog
{
    [LoggerMessage(EventId = 7100, EventName = "slskd.state", Level = LogLevel.Information,
        Message = "slskd reachable {Reachable}, logged in {LoggedIn}, state {State}")]
    public static partial void SlskdState(this ILogger logger, bool reachable, bool loggedIn, string state);

    [LoggerMessage(EventId = 7500, EventName = "fetch.requested", Level = LogLevel.Debug,
        Message = "Job {JobId} for pick {PickId} (run {RunId}, session {SessionId}): {Artist} - {Title} from {Origin}, dry run {DryRun}, joined {Joined}")]
    public static partial void FetchRequested(
        this ILogger logger, string jobId, string pickId, string runId, string sessionId, string artist, string title, string origin, bool dryRun, bool joined);

    [LoggerMessage(EventId = 7510, EventName = "fetch.state", Level = LogLevel.Debug,
        Message = "Job {JobId}: {From} -> {To}")]
    public static partial void FetchState(this ILogger logger, string jobId, string from, string to);

    [LoggerMessage(EventId = 7520, EventName = "fetch.ready", Level = LogLevel.Information,
        Message = "Job {JobId} ready as {ItemId}: {Artist} - {Title} from {Peer} in {TotalMs} ms")]
    public static partial void FetchReady(this ILogger logger, string jobId, Guid itemId, string artist, string title, string? peer, long totalMs);

    [LoggerMessage(EventId = 7530, EventName = "fetch.failed", Level = LogLevel.Information,
        Message = "Job {JobId} failed: {Artist} - {Title}: {Reason}")]
    public static partial void FetchFailed(this ILogger logger, string jobId, string artist, string title, string reason);

    [LoggerMessage(EventId = 7540, EventName = "fetch.cancelled", Level = LogLevel.Information,
        Message = "Job {JobId} cancelled: {Reason}")]
    public static partial void FetchCancelled(this ILogger logger, string jobId, string reason);

    [LoggerMessage(EventId = 7550, EventName = "fetch.unavailable", Level = LogLevel.Warning,
        Message = "Fetching unavailable: {Reason}")]
    public static partial void FetchUnavailable(this ILogger logger, string reason);

    [LoggerMessage(EventId = 7551, EventName = "fetch.available", Level = LogLevel.Information,
        Message = "Fetching available")]
    public static partial void FetchAvailable(this ILogger logger);

    [LoggerMessage(EventId = 7560, EventName = "fetch.job_error", Level = LogLevel.Error,
        Message = "Job {JobId} failed at {Stage}: {Error}")]
    public static partial void FetchJobError(this ILogger logger, string jobId, string stage, string error);

    /// <summary>Not in the design table: a fetch-store write failed; the job goes on.</summary>
    [LoggerMessage(EventId = 7570, EventName = "fetch.store_failed", Level = LogLevel.Warning,
        Message = "Job {JobId}: fetch store {Operation} failed: {Error}")]
    public static partial void FetchStoreFailed(this ILogger logger, string jobId, string operation, string error);

    /// <summary>Not in the design table: telling Jellyfin about a new file failed; the 30 min owned-index refresh finds it later.</summary>
    [LoggerMessage(EventId = 7580, EventName = "fetch.ingest_failed", Level = LogLevel.Warning,
        Message = "Job {JobId}: Jellyfin ingest of {JellyfinPath} failed: {Error}")]
    public static partial void FetchIngestFailed(this ILogger logger, string jobId, string jellyfinPath, string error);

    [LoggerMessage(EventId = 7640, EventName = "run.ended", Level = LogLevel.Debug,
        Message = "Run {RunId} on session {SessionId} ended ({Reason}), {CancelledJobs} fetch jobs cancelled")]
    public static partial void RunEnded(this ILogger logger, string runId, string sessionId, string reason, int cancelledJobs);
}
