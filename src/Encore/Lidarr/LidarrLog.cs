namespace Encore.Lidarr;

/// <summary>Lidarr events (v03 design section 13: 9300, 9310, 9320). Never log the key, URLs or bodies.</summary>
internal static partial class LidarrLog
{
    [LoggerMessage(EventId = 9300, EventName = "lidarr.state", Level = LogLevel.Information,
        Message = "Lidarr reachable {Reachable}, version {Version}, key rejected {KeyRejected}")]
    public static partial void LidarrStateChanged(this ILogger logger, bool reachable, string? version, bool keyRejected);

    [LoggerMessage(EventId = 9310, EventName = "lidarr.request_failed", Level = LogLevel.Warning,
        Message = "Lidarr {Operation} failed (status {Status}): {Error}")]
    public static partial void LidarrRequestFailed(this ILogger logger, string operation, int? status, string error);

    [LoggerMessage(EventId = 9320, EventName = "lidarr.auth_failed", Level = LogLevel.Error,
        Message = "Lidarr {Operation} rejected the API key (status {Status})")]
    public static partial void LidarrAuthFailed(this ILogger logger, string operation, int status);
}
