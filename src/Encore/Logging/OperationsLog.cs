namespace Encore.Logging;

/// <summary>
/// Log events of the store, the allow-list, the orchestrator and its fires (design section 8, numbered x10 like
/// <see cref="Log"/>). Never pass the API key, headers or response bodies here.
/// </summary>
internal static partial class OperationsLog
{
    [LoggerMessage(EventId = 1020, EventName = "store.ready", Level = LogLevel.Information,
        Message = "Store ready at {Path}, schema {SchemaVersion}, pruned {PrunedRows} rows")]
    public static partial void StoreReady(this ILogger logger, string path, int schemaVersion, int prunedRows);

    [LoggerMessage(EventId = 1030, EventName = "store.error", Level = LogLevel.Error,
        Message = "Store error during {Operation}: {Error}")]
    public static partial void StoreError(this ILogger logger, string operation, string error);

    [LoggerMessage(EventId = 2000, EventName = "allowlist.resolved", Level = LogLevel.Information,
        Message = "Allow-list resolved: {Users}")]
    public static partial void AllowListResolved(this ILogger logger, string users);

    [LoggerMessage(EventId = 2010, EventName = "allowlist.unresolved", Level = LogLevel.Warning,
        Message = "Allow-list entries not resolved to a Jellyfin user: {Names}")]
    public static partial void AllowListUnresolved(this ILogger logger, string names);

    [LoggerMessage(EventId = 2020, EventName = "allowlist.resolve_failed", Level = LogLevel.Warning,
        Message = "Allow-list lookup failed: {Error}; retrying in {RetryInSeconds}s")]
    public static partial void AllowListResolveFailed(this ILogger logger, string error, double retryInSeconds);

    [LoggerMessage(EventId = 4020, EventName = "track.changed", Level = LogLevel.Debug,
        Message = "Track changed on session {SessionId}: item {ItemId}, queue {EntryQueueLen}, previous {PrevQueueLen}, repeat {Repeat}, instance {Sequence}")]
    public static partial void TrackChanged(this ILogger logger, string sessionId, Guid itemId, int entryQueueLen, int prevQueueLen, string? repeat, long sequence);

    [LoggerMessage(EventId = 4100, EventName = "trigger.skipped", Level = LogLevel.Information,
        Message = "Radio not started on session {SessionId} ({UserName}, {Client}): {Reason}")]
    public static partial void TriggerSkipped(this ILogger logger, string sessionId, string? userName, string? client, string reason);

    /// <summary>The quiet form at Debug (event 4101), for the reasons that happen all the time (<c>not_last</c>, <c>not_allowed</c>).</summary>
    [LoggerMessage(EventId = 4101, EventName = "trigger.skipped_quiet", Level = LogLevel.Debug,
        Message = "Radio not started on session {SessionId} ({UserName}, {Client}): {Reason}")]
    public static partial void TriggerSkippedDebug(this ILogger logger, string sessionId, string? userName, string? client, string reason);

    [LoggerMessage(EventId = 4110, EventName = "trigger.fired", Level = LogLevel.Information,
        Message = "Radio {Kind} on session {SessionId}: model {Model}, item {ItemId} {ItemName}, run {RunId}, key {DedupeKey}")]
    public static partial void TriggerFired(this ILogger logger, string kind, string sessionId, string model, Guid itemId, string? itemName, string runId, string dedupeKey);

    [LoggerMessage(EventId = 4120, EventName = "trigger.gave_up", Level = LogLevel.Warning,
        Message = "Radio gave up on session {SessionId} after {Attempts} attempts: {LastError}")]
    public static partial void TriggerGaveUp(this ILogger logger, string sessionId, int attempts, string lastError);

    [LoggerMessage(EventId = 6000, EventName = "queue.would_add", Level = LogLevel.Information,
        Message = "Dry run, would queue {Kind} on session {SessionId}: [{Items}]")]
    public static partial void QueueWouldAdd(this ILogger logger, string kind, string sessionId, string items);

    [LoggerMessage(EventId = 6010, EventName = "queue.added", Level = LogLevel.Information,
        Message = "Queued {Kind} on session {SessionId}: [{Items}], http {HttpStatus}")]
    public static partial void QueueAdded(this ILogger logger, string kind, string sessionId, string items, int httpStatus);

    /// <summary>PlayLast got no answer (timeout or a connection broken after sending). Not retried; the delivery check decides.</summary>
    [LoggerMessage(EventId = 6015, EventName = "queue.add_no_reply", Level = LogLevel.Warning,
        Message = "No answer to the queue request on session {SessionId}: {Error}; not retrying, the queue check decides. Items [{Items}]")]
    public static partial void QueueAddNoReply(this ILogger logger, string sessionId, string error, string items);

    [LoggerMessage(EventId = 6020, EventName = "queue.add_failed", Level = LogLevel.Error,
        Message = "Could not queue on session {SessionId}: {Error} (status {HttpStatus}), items [{Items}]")]
    public static partial void QueueAddFailed(this ILogger logger, string sessionId, string error, int? httpStatus, string items);

    [LoggerMessage(EventId = 6030, EventName = "queue.confirmed", Level = LogLevel.Debug,
        Message = "Queued songs confirmed in run {RunId}: [{ItemIds}]")]
    public static partial void QueueConfirmed(this ILogger logger, string runId, string itemIds);

    [LoggerMessage(EventId = 6040, EventName = "queue.unconfirmed", Level = LogLevel.Warning,
        Message = "Queued songs not seen in run {RunId}: [{ItemIds}], judged at {JudgedAt}, next item {NextItemId}")]
    public static partial void QueueUnconfirmed(this ILogger logger, string runId, string itemIds, string judgedAt, Guid? nextItemId);

    [LoggerMessage(EventId = 8010, EventName = "worker.crashed", Level = LogLevel.Critical,
        Message = "Component {Component} failed on a step and went on: {Error}")]
    public static partial void WorkerCrashed(this ILogger logger, string component, string error);
}
