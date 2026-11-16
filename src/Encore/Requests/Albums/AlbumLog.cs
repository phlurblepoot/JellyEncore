namespace Encore.Requests.Albums;

/// <summary>Album request events 9330-9390 (v0.3 design 13). Never pass the Lidarr key, a URL or a response body here.</summary>
public static partial class AlbumLog
{
    [LoggerMessage(EventId = 9330, EventName = "lidarr.artist_added", Level = LogLevel.Information,
        Message = "Request {RequestId}: POST album also added the artist {Artist} as Lidarr artist {LidarrArtistId}")]
    public static partial void ArtistAdded(this ILogger logger, string requestId, long lidarrArtistId, string artist);

    [LoggerMessage(EventId = 9340, EventName = "lidarr.album_monitored", Level = LogLevel.Information,
        Message = "Request {RequestId}: Lidarr album {LidarrAlbumId} monitored via {Via}, artist monitored {ArtistMonitored}")]
    public static partial void AlbumMonitored(this ILogger logger, string requestId, long lidarrAlbumId, bool artistMonitored, string via);

    [LoggerMessage(EventId = 9350, EventName = "lidarr.album_progress", Level = LogLevel.Debug,
        Message = "Request {RequestId}: {Files} of {Of} tracks in Lidarr")]
    public static partial void AlbumProgress(this ILogger logger, string requestId, int files, int of);

    [LoggerMessage(EventId = 9360, EventName = "lidarr.album_unmonitored", Level = LogLevel.Information,
        Message = "Request {RequestId}: Lidarr album unmonitored ({Reason})")]
    public static partial void AlbumUnmonitored(this ILogger logger, string requestId, string reason);

    [LoggerMessage(EventId = 9380, EventName = "lidarr.album_files_seen", Level = LogLevel.Information,
        Message = "Request {RequestId}: Lidarr album {LidarrAlbumId} has files now, {Files} of {Of} tracks (statistics {StatisticsFiles}, track files {TrackFiles})")]
    public static partial void AlbumFilesSeen(this ILogger logger, string requestId, long lidarrAlbumId, int files, int of, int statisticsFiles, int trackFiles);

    [LoggerMessage(EventId = 9390, EventName = "lidarr.album_poll_failed", Level = LogLevel.Warning,
        Message = "Request {RequestId}: poll of Lidarr album {LidarrAlbumId} failed at {Operation} (status {Status}), {FailuresInARow} in a row; the state is kept")]
    public static partial void AlbumPollFailed(this ILogger logger, string requestId, long lidarrAlbumId, string operation, int status, int failuresInARow);

    [LoggerMessage(EventId = 9370, EventName = "lidarr.artist_other_wanted", Level = LogLevel.Warning,
        Message = "Request {RequestId}: monitoring Lidarr artist {LidarrArtistId} also makes {OtherWantedAlbums} other monitored album(s) wanted")]
    public static partial void ArtistOtherWanted(this ILogger logger, string requestId, long lidarrArtistId, int otherWantedAlbums);
}
