namespace Encore.Fetch;

/// <summary>Search, download and scoring events of <see cref="SoulseekFetcher"/> (v0.2 design section 13, 71xx and 72xx).</summary>
internal static partial class SoulseekFetchLog
{
    [LoggerMessage(EventId = 7110, EventName = "slskd.request_failed", Level = LogLevel.Warning,
        Message = "slskd {Operation} failed (status {Status}): {Error}")]
    public static partial void RequestFailed(this ILogger logger, string operation, int? status, string error);

    [LoggerMessage(EventId = 7120, EventName = "slskd.auth_failed", Level = LogLevel.Error,
        Message = "slskd rejected the API key on {Operation} (status {Status})")]
    public static partial void AuthFailed(this ILogger logger, string operation, int? status);

    [LoggerMessage(EventId = 7130, EventName = "slskd.search_started", Level = LogLevel.Debug,
        Message = "Job {JobId}: search {QueryNo} \"{Query}\" started as {SearchId}")]
    public static partial void SearchStarted(this ILogger logger, string jobId, int queryNo, string query, Guid searchId);

    [LoggerMessage(EventId = 7140, EventName = "slskd.search_done", Level = LogLevel.Information,
        Message = "Job {JobId}: search {SearchId} gave {Responses} responses, {Files} files in {ElapsedMs} ms, ended by {EndedBy}")]
    public static partial void SearchDone(this ILogger logger, string jobId, Guid searchId, int responses, int files, long elapsedMs, string endedBy);

    [LoggerMessage(EventId = 7150, EventName = "slskd.budget_wait", Level = LogLevel.Debug,
        Message = "Job {JobId}: waited {WaitSeconds} s for a search slot, {SearchesInWindow} searches in the window")]
    public static partial void BudgetWait(this ILogger logger, string jobId, double waitSeconds, int searchesInWindow);

    [LoggerMessage(EventId = 7160, EventName = "slskd.enqueued", Level = LogLevel.Information,
        Message = "Job {JobId}: peer {PeerNo} {Peer} queued {File} ({Size} bytes) as transfer {TransferId}")]
    public static partial void Enqueued(this ILogger logger, string jobId, int peerNo, string peer, string file, long size, Guid transferId);

    [LoggerMessage(EventId = 7170, EventName = "slskd.transfer_stalled", Level = LogLevel.Warning,
        Message = "Job {JobId}: gave up on peer {Peer} ({Why}), state {State}, {Bytes} bytes")]
    public static partial void TransferStalled(this ILogger logger, string jobId, string peer, string why, string state, long bytes);

    [LoggerMessage(EventId = 7180, EventName = "slskd.downloaded", Level = LogLevel.Information,
        Message = "Job {JobId}: downloaded {Size} bytes from {Peer} in {ElapsedMs} ms at {AverageSpeed} B/s")]
    public static partial void Downloaded(this ILogger logger, string jobId, string peer, long size, long elapsedMs, double averageSpeed);

    [LoggerMessage(EventId = 7200, EventName = "score.result", Level = LogLevel.Debug,
        Message = "Job {JobId}: query {QueryNo} accepted {Accepted}, rejected {Rejected}, best {Best}")]
    public static partial void ScoreResult(this ILogger logger, string jobId, int queryNo, int accepted, string rejected, string best);

    [LoggerMessage(EventId = 7210, EventName = "score.none", Level = LogLevel.Information,
        Message = "Job {JobId}: no acceptable copy of {Artist} - {Title} after {Queries} queries, rejected {Rejected}")]
    public static partial void ScoreNone(this ILogger logger, string jobId, string artist, string title, int queries, string rejected);

    [LoggerMessage(EventId = 7220, EventName = "score.would_download", Level = LogLevel.Information,
        Message = "Job {JobId}: dry run would download {Artist} - {Title} from {Peer}: {File} ({Format}, {Kbps} kbps, {Size} bytes, eta {EtaSeconds} s), {Backups} backups")]
    public static partial void WouldDownload(
        this ILogger logger, string jobId, string artist, string title, string peer, string file, string format, int? kbps, long size, double etaSeconds, int backups);
}
