namespace Encore.Requests.Songs;

/// <summary>Request lifecycle log events 9200-9290 and 9490 (v0.3 design 13). Never pass a token or a Deezer preview link here.</summary>
public static partial class RequestLog
{
    [LoggerMessage(EventId = 9200, EventName = "request.created", Level = LogLevel.Information,
        Message = "Request {RequestId} ({Kind}) by {UserId}: {Artist} - {Title}, {State}")]
    public static partial void Created(this ILogger logger, string requestId, string kind, string userId, string artist, string? title, string state);

    [LoggerMessage(EventId = 9210, EventName = "request.state", Level = LogLevel.Information,
        Message = "Request {RequestId}: {From} -> {To} ({Detail})")]
    public static partial void StateChanged(this ILogger logger, string requestId, string from, string to, string? detail);

    [LoggerMessage(EventId = 9220, EventName = "request.ready", Level = LogLevel.Information,
        Message = "Request {RequestId} is ready as {ItemId} after {WaitedMs} ms")]
    public static partial void Ready(this ILogger logger, string requestId, string itemId, long waitedMs);

    [LoggerMessage(EventId = 9230, EventName = "request.failed", Level = LogLevel.Information,
        Message = "Request {RequestId} failed: {Reason} after {Attempts} attempts")]
    public static partial void Failed(this ILogger logger, string requestId, string reason, int attempts);

    [LoggerMessage(EventId = 9240, EventName = "request.retry_scheduled", Level = LogLevel.Information,
        Message = "Request {RequestId}: attempt {Attempt} failed, next try at {AtUtc}")]
    public static partial void RetryScheduled(this ILogger logger, string requestId, int attempt, DateTimeOffset atUtc);

    [LoggerMessage(EventId = 9250, EventName = "request.cancelled", Level = LogLevel.Information,
        Message = "Request {RequestId} cancelled by {ByUserId}")]
    public static partial void Cancelled(this ILogger logger, string requestId, string byUserId);

    [LoggerMessage(EventId = 9270, EventName = "request.limit", Level = LogLevel.Information,
        Message = "{UserId} reached the daily {Kind} request limit of {Limit}")]
    public static partial void Limit(this ILogger logger, string userId, string kind, int limit);

    [LoggerMessage(EventId = 9280, EventName = "request.joined", Level = LogLevel.Debug,
        Message = "Request {RequestId} for {TrackKey} joins {With}")]
    public static partial void Joined(this ILogger logger, string requestId, string trackKey, string with);

    [LoggerMessage(EventId = 9290, EventName = "request.resubmitted", Level = LogLevel.Information,
        Message = "Re-submitted {Count} song requests after a start")]
    public static partial void Resubmitted(this ILogger logger, int count);

    [LoggerMessage(EventId = 9490, EventName = "request.worker_error", Level = LogLevel.Error,
        Message = "The request worker failed at {Stage}: {Error}")]
    public static partial void WorkerError(this ILogger logger, string stage, string error);
}
