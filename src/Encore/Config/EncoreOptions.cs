using System.Text;
using Microsoft.Extensions.Logging;

namespace Encore.Config;

/// <summary>Validated Encore settings. The API key is held here but never printed: <see cref="ToString"/> redacts it.</summary>
public sealed record EncoreOptions
{
    public required Uri JellyfinUrl { get; init; }

    public required string JellyfinApiKey { get; init; }

    public required IReadOnlyList<string> AllowedUsers { get; init; }

    /// <summary>T117: ENCORE_ALLOWED_USERS contained *: every Jellyfin user gets radio and no names are resolved.</summary>
    public bool AllowedUsersAll { get; init; }

    public int Lookahead { get; init; } = 5;

    public int Batch { get; init; } = 2;

    public int PollSeconds { get; init; } = 10;

    public RecentWindow RecentWindow { get; init; } = RecentWindow.Session;

    public bool DryRun { get; init; } = true;

    public required string DataDir { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    public FetchMode FetchMode { get; init; } = FetchMode.Off;

    /// <summary>slskd base URL without a trailing slash; null when fetching is off.</summary>
    public Uri? SlskdUrl { get; init; }

    /// <summary>slskd API key; null when fetching is off. Never printed.</summary>
    public string? SlskdApiKey { get; init; }

    public string SlskdDownloadsDir { get; init; } = "/slskd-downloads";

    public string RadioDir { get; init; } = "/music-radio";

    public string RadioJellyfinDir { get; init; } = "/music-radio";

    public SearchLimit SearchLimit { get; init; } = new(34, 220);

    public int FetchConcurrency { get; init; } = 2;

    /// <summary>ENCORE_REQUESTS: song and album requests (v0.3).</summary>
    public bool RequestsEnabled { get; init; }

    /// <summary>ENCORE_REQUEST_USERS: Jellyfin names or ids allowed to request besides admins. Empty = admins only.</summary>
    public IReadOnlyList<string> RequestUsers { get; init; } = [];

    /// <summary>ENCORE_REQUEST_USERS was "*": every account may request.</summary>
    public bool RequestUsersAll { get; init; }

    public int RequestSongsPerDay { get; init; } = 25;

    public int RequestAlbumsPerDay { get; init; } = 2;

    /// <summary>Exact origins (scheme://host[:port], lower case) allowed to call the API cross-origin. Empty = no CORS.</summary>
    public IReadOnlyList<string> RequestOrigins { get; init; } = [];

    /// <summary>Encore as browsers reach it, no trailing slash; its path equals <see cref="PathBase"/>.</summary>
    public Uri? PublicUrl { get; init; }

    /// <summary>"" or "/segment[/segment]", no trailing slash.</summary>
    public string PathBase { get; init; } = "";

    /// <summary>Networks whose X-Forwarded-* headers are honoured. Empty = forwarded headers ignored.</summary>
    public IReadOnlyList<System.Net.IPNetwork> TrustedProxies { get; init; } = [];

    /// <summary>Jellyfin as browsers reach it, no trailing slash.</summary>
    public Uri? JellyfinPublicUrl { get; init; }

    /// <summary>Lidarr base URL without a trailing slash; null = album requests off.</summary>
    public Uri? LidarrUrl { get; init; }

    /// <summary>Lidarr API key; null when no Lidarr URL. Never printed.</summary>
    public string? LidarrApiKey { get; init; }

    public string? LidarrRootFolder { get; init; }

    public PathMapping LidarrPathMap { get; init; } = new("/media/music", "/media/music");

    public RetentionMode RetentionMode { get; init; } = RetentionMode.Off;

    public int RetentionDays { get; init; } = 30;

    public int RetentionUploads { get; init; } = 3;

    public int RetentionPlays { get; init; } = 3;

    /// <summary>Hour of day (container local time) of the daily pass.</summary>
    public int RetentionHour { get; init; } = 4;

    public int RetentionMaxPromotions { get; init; } = 20;

    public int RetentionMaxDeletes { get; init; } = 100;

    /// <summary>Playlist ids (32 lower-case hex, no dashes) never counted nor re-added.</summary>
    public IReadOnlyList<string> RetentionIgnorePlaylists { get; init; } = [];

    /// <summary>Encore's writable view of the music library.</summary>
    public string LibraryDir { get; init; } = "/library";

    /// <summary>The library folder as Jellyfin sees it.</summary>
    public string LibraryJellyfinDir { get; init; } = "/media/music";

    /// <summary>slskd's view of the music-radio folder.</summary>
    public string SlskdRadioDir { get; init; } = "/music-radio";

    /// <summary>Stable per-install device id (<c>encore-</c> plus 12 hex digits), set once the data dir is validated.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>Jellyfin socket URL: same host, ws/wss scheme, <c>/socket</c> path. Carries no key.</summary>
    public Uri WebSocketUrl
    {
        get
        {
            var builder = new UriBuilder(JellyfinUrl)
            {
                Scheme = JellyfinUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Path = JellyfinUrl.AbsolutePath.TrimEnd('/') + "/socket",
            };
            return builder.Uri;
        }
    }

    // The compiler-generated printing would include the key, so replace it.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"JellyfinUrl = {JellyfinUrl}, JellyfinApiKey = <redacted>, AllowedUsers = [{(AllowedUsersAll ? "*" : string.Join(", ", AllowedUsers))}],")
            .Append($"Lookahead = {Lookahead}, Batch = {Batch}, PollSeconds = {PollSeconds}, RecentWindow = {RecentWindow}, ")
            .Append($"DryRun = {DryRun}, DataDir = {DataDir}, LogLevel = {LogLevel}, DeviceId = {DeviceId}, ")
            .Append($"FetchMode = {FetchMode}, SlskdUrl = {SlskdUrl}, SlskdApiKey = <redacted>, SlskdDownloadsDir = {SlskdDownloadsDir}, ")
            .Append($"RadioDir = {RadioDir}, RadioJellyfinDir = {RadioJellyfinDir}, SearchLimit = {SearchLimit}, FetchConcurrency = {FetchConcurrency}, ")
            .Append($"RequestsEnabled = {RequestsEnabled}, RequestUsers = [{(RequestUsersAll ? "*" : string.Join(", ", RequestUsers))}], ")
            .Append($"RequestSongsPerDay = {RequestSongsPerDay}, RequestAlbumsPerDay = {RequestAlbumsPerDay}, ")
            .Append($"RequestOrigins = [{string.Join(", ", RequestOrigins)}], PublicUrl = {PublicUrl}, PathBase = {PathBase}, ")
            .Append($"TrustedProxies = [{string.Join(", ", TrustedProxies)}], JellyfinPublicUrl = {JellyfinPublicUrl}, ")
            .Append($"LidarrUrl = {LidarrUrl}, LidarrApiKey = <redacted>, LidarrRootFolder = {LidarrRootFolder}, LidarrPathMap = {LidarrPathMap}, ")
            .Append($"RetentionMode = {RetentionMode}, RetentionDays = {RetentionDays}, RetentionUploads = {RetentionUploads}, RetentionPlays = {RetentionPlays}, ")
            .Append($"RetentionHour = {RetentionHour}, RetentionMaxPromotions = {RetentionMaxPromotions}, RetentionMaxDeletes = {RetentionMaxDeletes}, ")
            .Append($"RetentionIgnorePlaylists = [{string.Join(", ", RetentionIgnorePlaylists)}], LibraryDir = {LibraryDir}, ")
            .Append($"LibraryJellyfinDir = {LibraryJellyfinDir}, SlskdRadioDir = {SlskdRadioDir}");
        return true;
    }
}
