namespace Encore.Logging;

// Watcher log events (design section 8, ids x10 like the startup events). Never pass headers, bodies or the key.
public static partial class Log
{
    [LoggerMessage(EventId = 2100, EventName = "jellyfin.auth_failed", Level = LogLevel.Error,
        Message = "Jellyfin rejected the API key on {Endpoint} (HTTP {Status})")]
    public static partial void JellyfinAuthFailed(this ILogger logger, string endpoint, int? status);

    [LoggerMessage(EventId = 3000, EventName = "socket.connecting", Level = LogLevel.Information,
        Message = "Connecting to {Url} (attempt {Attempt})")]
    public static partial void SocketConnecting(this ILogger logger, string url, int attempt);

    [LoggerMessage(EventId = 3010, EventName = "socket.subscribed", Level = LogLevel.Information,
        Message = "Socket connected and subscribed to Sessions")]
    public static partial void SocketSubscribed(this ILogger logger);

    [LoggerMessage(EventId = 3020, EventName = "socket.closed", Level = LogLevel.Warning,
        Message = "Socket closed: {CloseStatus} {Reason} after {UptimeSeconds}s")]
    public static partial void SocketClosed(this ILogger logger, string closeStatus, string? reason, double uptimeSeconds);

    [LoggerMessage(EventId = 3030, EventName = "socket.reconnect_scheduled", Level = LogLevel.Information,
        Message = "Reconnecting in {DelaySeconds}s (attempt {Attempt})")]
    public static partial void SocketReconnectScheduled(this ILogger logger, double delaySeconds, int attempt);

    [LoggerMessage(EventId = 3040, EventName = "socket.message_skipped", Level = LogLevel.Debug,
        Message = "Skipped socket message {MessageType}")]
    public static partial void SocketMessageSkipped(this ILogger logger, string messageType);

    [LoggerMessage(EventId = 3100, EventName = "poll.started", Level = LogLevel.Information,
        Message = "Polling /Sessions while the socket is not delivering")]
    public static partial void PollStarted(this ILogger logger);

    [LoggerMessage(EventId = 3110, EventName = "poll.stopped", Level = LogLevel.Information,
        Message = "Stopped polling /Sessions: the socket is delivering")]
    public static partial void PollStopped(this ILogger logger);

    [LoggerMessage(EventId = 3120, EventName = "poll.failed", Level = LogLevel.Warning,
        Message = "GET /Sessions failed: {Error}")]
    public static partial void PollFailed(this ILogger logger, string error);
}
