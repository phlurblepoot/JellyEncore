using Encore.Config;

namespace Encore.Logging;

/// <summary>One <c>[LoggerMessage]</c> method per log event (v0.1 design, section 8). Never pass the API key here.</summary>
public static partial class Log
{
    [LoggerMessage(EventId = 1000, EventName = "startup.config", Level = LogLevel.Information,
        Message = "Encore {Version} configured: jellyfin {Url}, users [{AllowedUsers}], everyone {AllowedEveryone}, lookahead {Lookahead}, batch {Batch}, poll {PollSeconds}s, window {RecentWindow}, dryRun {DryRun}, dataDir {DataDir}, deviceId {DeviceId}, apiKey {ApiKey}, fetchMode {FetchMode}, slskdUrl {SlskdUrl}, slskdApiKey {SlskdApiKey}, slskdDownloadsDir {SlskdDownloadsDir}, radioDir {RadioDir}, radioJellyfinDir {RadioJellyfinDir}, searchLimit {SearchLimit}, fetchConcurrency {FetchConcurrency}, requests {Requests}, requestUsers [{RequestUsers}], songsPerDay {SongsPerDay}, albumsPerDay {AlbumsPerDay}, origins [{Origins}], publicUrl {PublicUrl}, pathBase {PathBase}, trustedProxies [{TrustedProxies}], jellyfinPublicUrl {JellyfinPublicUrl}, lidarrUrl {LidarrUrl}, lidarrApiKey {LidarrApiKey}, lidarrRootFolder {LidarrRootFolder}, lidarrPathMap {LidarrPathMap}, retentionMode {RetentionMode}, retentionDays {RetentionDays}, retentionUploads {RetentionUploads}, retentionPlays {RetentionPlays}, retentionHour {RetentionHour}, maxPromotions {MaxPromotions}, maxDeletes {MaxDeletes}, ignorePlaylists [{IgnorePlaylists}], libraryDir {LibraryDir}, libraryJellyfinDir {LibraryJellyfinDir}, slskdRadioDir {SlskdRadioDir}")]
    private static partial void StartupConfig(ILogger logger, string version, string url, string allowedUsers, bool allowedEveryone,
        int lookahead, int batch, int pollSeconds, string recentWindow, bool dryRun, string dataDir, string deviceId, string apiKey,
        string fetchMode, string slskdUrl, string slskdApiKey, string slskdDownloadsDir, string radioDir, string radioJellyfinDir,
        string searchLimit, int fetchConcurrency, string requests, string requestUsers, int songsPerDay, int albumsPerDay,
        string origins, string publicUrl, string pathBase, string trustedProxies, string jellyfinPublicUrl, string lidarrUrl,
        string lidarrApiKey, string lidarrRootFolder, string lidarrPathMap, string retentionMode, int retentionDays, int retentionUploads,
        int retentionPlays, int retentionHour, int maxPromotions, int maxDeletes, string ignorePlaylists, string libraryDir,
        string libraryJellyfinDir, string slskdRadioDir);

    [LoggerMessage(EventId = 1010, EventName = "startup.config_warning", Level = LogLevel.Warning,
        Message = "Configuration warning: {Variable}: {Reason}")]
    public static partial void StartupConfigWarning(this ILogger logger, string variable, string reason);

    [LoggerMessage(EventId = 1001, EventName = "startup.config_invalid", Level = LogLevel.Error,
        Message = "Invalid configuration: {Variable}: {Reason}")]
    public static partial void StartupConfigInvalid(this ILogger logger, string variable, string reason);

    /// <summary>Startup summary. The key is reported only as "set".</summary>
    public static void StartupConfig(this ILogger logger, EncoreOptions o, string version) =>
        StartupConfig(logger, version, o.JellyfinUrl.ToString(), string.Join(", ", o.AllowedUsers), o.AllowedUsersAll, o.Lookahead, o.Batch,
            o.PollSeconds, o.RecentWindow.ToString(), o.DryRun, o.DataDir, o.DeviceId, "set",
            o.FetchMode.ToString(), o.SlskdUrl?.ToString() ?? "none", o.SlskdApiKey is null ? "unset" : "set", o.SlskdDownloadsDir,
            o.RadioDir, o.RadioJellyfinDir, o.SearchLimit.ToString(), o.FetchConcurrency,
            o.RequestsEnabled ? "on" : "off", o.RequestUsersAll ? "*" : string.Join(", ", o.RequestUsers), o.RequestSongsPerDay, o.RequestAlbumsPerDay,
            string.Join(", ", o.RequestOrigins), o.PublicUrl?.ToString() ?? "none", o.PathBase.Length == 0 ? "none" : o.PathBase,
            string.Join(", ", o.TrustedProxies), o.JellyfinPublicUrl?.ToString() ?? "none", o.LidarrUrl?.ToString() ?? "none",
            o.LidarrApiKey is null ? "unset" : "set", o.LidarrRootFolder ?? "none", o.LidarrPathMap.ToString(),
            o.RetentionMode.ToString(), o.RetentionDays, o.RetentionUploads, o.RetentionPlays, o.RetentionHour, o.RetentionMaxPromotions,
            o.RetentionMaxDeletes, string.Join(", ", o.RetentionIgnorePlaylists), o.LibraryDir, o.LibraryJellyfinDir, o.SlskdRadioDir);
}
