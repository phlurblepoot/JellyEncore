using Encore.Config;

namespace Encore.Logging;

/// <summary>One <c>[LoggerMessage]</c> method per log event (v0.1 design, section 8). Never pass the API key here.</summary>
public static partial class Log
{
    [LoggerMessage(EventId = 1000, EventName = "startup.config", Level = LogLevel.Information,
        Message = "Encore {Version} configured: jellyfin {Url}, users [{AllowedUsers}], lookahead {Lookahead}, batch {Batch}, poll {PollSeconds}s, window {RecentWindow}, dryRun {DryRun}, dataDir {DataDir}, deviceId {DeviceId}, apiKey {ApiKey}")]
    private static partial void StartupConfig(ILogger logger, string version, string url, string allowedUsers,
        int lookahead, int batch, int pollSeconds, string recentWindow, bool dryRun, string dataDir, string deviceId, string apiKey);

    [LoggerMessage(EventId = 1001, EventName = "startup.config_invalid", Level = LogLevel.Error,
        Message = "Invalid configuration: {Variable}: {Reason}")]
    public static partial void StartupConfigInvalid(this ILogger logger, string variable, string reason);

    /// <summary>Startup summary. The key is reported only as "set".</summary>
    public static void StartupConfig(this ILogger logger, EncoreOptions o, string version) =>
        StartupConfig(logger, version, o.JellyfinUrl.ToString(), string.Join(", ", o.AllowedUsers), o.Lookahead, o.Batch,
            o.PollSeconds, o.RecentWindow.ToString(), o.DryRun, o.DataDir, o.DeviceId, "set");
}
