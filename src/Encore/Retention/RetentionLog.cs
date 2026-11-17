namespace Encore.Retention;

/// <summary>Retention log events (v0.4 design section 12). This file holds uploads and shares, 8300-8350.</summary>
internal static partial class RetentionLog
{
    // ---- the pass, signals and decisions (T127): 8100-8140, 8200-8210, 8900 ----

    [LoggerMessage(EventId = 8100, EventName = "retention.pass_started", Level = LogLevel.Information,
        Message = "Retention pass {PassId} started ({Mode}), {Tracked} tracked songs")]
    public static partial void PassStarted(this ILogger logger, string passId, string mode, int tracked);

    [LoggerMessage(EventId = 8110, EventName = "retention.pass_done", Level = LogLevel.Information,
        Message = "Retention pass {PassId} done ({Mode}): promote {Promote}, dedupe {Dedupe}, keep {Keep}, delete {Delete}, wait {Wait}, skip {Skip}, untracked {Untracked}, {DurationMs} ms")]
    public static partial void PassDone(this ILogger logger, string passId, string mode, int promote, int dedupe, int keep, int delete, int wait, int skip, int untracked, long durationMs);

    [LoggerMessage(EventId = 8120, EventName = "retention.decision", Level = LogLevel.Information,
        Message = "Pass {PassId} row {RowId} {Artist} - {Title}: {Decision} ({Reason}), age {AgeDays} d, uploads {Uploads}, plays {Plays}, target {TargetPath}, dry run {DryRun}")]
    public static partial void Decision(this ILogger logger, string passId, long rowId, string artist, string title, string decision, string? reason, double ageDays, int uploads, int plays, string? targetPath, bool dryRun);

    [LoggerMessage(EventId = 8130, EventName = "retention.kept", Level = LogLevel.Information,
        Message = "Row {RowId} kept for good: {Reason}")]
    public static partial void Kept(this ILogger logger, long rowId, string reason);

    [LoggerMessage(EventId = 8140, EventName = "retention.untracked", Level = LogLevel.Warning,
        Message = "Untracked audio file in the radio folder (left alone): {Path}")]
    public static partial void Untracked(this ILogger logger, string path);

    [LoggerMessage(EventId = 8200, EventName = "signals.read", Level = LogLevel.Information,
        Message = "Signals read: {Users} users, {Favorites} favorites, {Playlists} counted playlists, {PlaylistsSkipped} skipped, SmartLists installed {SmartListsInstalled}, {Matched} tracked songs matched")]
    public static partial void SignalsRead(this ILogger logger, int users, int favorites, int playlists, int playlistsSkipped, bool smartListsInstalled, int matched);

    [LoggerMessage(EventId = 8210, EventName = "signals.failed", Level = LogLevel.Warning,
        Message = "Signal read failed ({What}): status {Status}, {Error}; this pass skips deletes")]
    public static partial void SignalsFailed(this ILogger logger, string what, int? status, string error);

    [LoggerMessage(EventId = 8900, EventName = "retention.store_failed", Level = LogLevel.Error,
        Message = "Retention store failed during {Operation}: {Error}")]
    public static partial void StoreFailed(this ILogger logger, string operation, string error);

    [LoggerMessage(EventId = 8300, EventName = "uploads.read", Level = LogLevel.Information,
        Message = "Read {Pages} slskd event pages ({Events} events), {NewUploads} new uploads, watermark {Watermark}")]
    public static partial void UploadsRead(this ILogger logger, int pages, int events, int newUploads, string watermark);

    [LoggerMessage(EventId = 8310, EventName = "uploads.failed", Level = LogLevel.Warning,
        Message = "slskd read failed during {What}: status {Status}, {Error}")]
    public static partial void UploadsFailed(this ILogger logger, string what, int? status, string error);

    [LoggerMessage(EventId = 8320, EventName = "shares.rescan", Level = LogLevel.Information,
        Message = "Asked slskd to rescan its shares ({Reason})")]
    public static partial void SharesRescan(this ILogger logger, string reason);

    [LoggerMessage(EventId = 8330, EventName = "shares.rescan_deferred", Level = LogLevel.Debug,
        Message = "Share rescan deferred: {Why}")]
    public static partial void SharesRescanDeferred(this ILogger logger, string why);

    [LoggerMessage(EventId = 8340, EventName = "shares.state",
        Message = "slskd shares: radio shared {RadioShared}, {Shares} shares")]
    public static partial void SharesState(this ILogger logger, LogLevel level, bool radioShared, int shares);

    [LoggerMessage(EventId = 8350, EventName = "uploads.active_skip", Level = LogLevel.Information,
        Message = "Row {RowId} is being uploaded to {PeerCount} peers, deferred")]
    public static partial void UploadsActiveSkip(this ILogger logger, long rowId, int peerCount);
}
