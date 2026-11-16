namespace Encore.Retention;

/// <summary>Retention log events (v0.4 design section 12). This file holds uploads and shares, 8300-8350.</summary>
internal static partial class RetentionLog
{
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
