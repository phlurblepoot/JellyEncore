namespace Encore.Retention;

/// <summary>Retention log events (v0.4 design section 12). This part holds the promoter, 8500-8590.</summary>
internal static partial class RetentionLog
{
    [LoggerMessage(EventId = 8500, EventName = "promote.step", Level = LogLevel.Information,
        Message = "Row {RowId} promotion at step {Step}, target {TargetPath}")]
    public static partial void PromoteStep(this ILogger logger, long rowId, string step, string? targetPath);

    [LoggerMessage(EventId = 8510, EventName = "promote.lidarr_wait", Level = LogLevel.Information,
        Message = "Row {RowId} waited {WaitedSec} s for Lidarr commands [{Commands}], placed anyway {PlacedAnyway}")]
    public static partial void PromoteLidarrWait(this ILogger logger, long rowId, string commands, long waitedSec, bool placedAnyway);

    [LoggerMessage(EventId = 8520, EventName = "promote.done", Level = LogLevel.Information,
        Message = "Row {RowId} promoted: item {OldItemId} is now {NewItemId} at {TargetPath} ({TotalMs} ms)")]
    public static partial void PromoteDone(this ILogger logger, long rowId, string oldItemId, string? newItemId, string? targetPath, long totalMs);

    [LoggerMessage(EventId = 8530, EventName = "promote.failed", Level = LogLevel.Warning,
        Message = "Row {RowId} promotion failed at step {Step} (attempt {Attempts}): {Error}")]
    public static partial void PromoteFailed(this ILogger logger, long rowId, string step, int attempts, string error);

    [LoggerMessage(EventId = 8540, EventName = "promote.stalled", Level = LogLevel.Warning,
        Message = "Row {RowId} promotion stalled at step {Step}; it is no longer retried automatically")]
    public static partial void PromoteStalled(this ILogger logger, long rowId, string step);

    [LoggerMessage(EventId = 8550, EventName = "promote.userdata", Level = LogLevel.Information,
        Message = "Row {RowId} user data for {Users} users: favorites carried {FavoritesCarried}, repaired {FavoritesRepaired}, play counts carried {PlayCountsCarried}")]
    public static partial void PromoteUserData(this ILogger logger, long rowId, int users, int favoritesCarried, int favoritesRepaired, int playCountsCarried);

    [LoggerMessage(EventId = 8555, EventName = "promote.userdata_skipped", Level = LogLevel.Warning,
        Message = "Row {RowId} user data of user {UserId} not carried: {Error}")]
    public static partial void PromoteUserDataSkipped(this ILogger logger, long rowId, string userId, string error);

    [LoggerMessage(EventId = 8560, EventName = "playlist.readded", Level = LogLevel.Information,
        Message = "Row {RowId} re-added to playlist {PlaylistId} at position {Position} as user {EditorUserId}")]
    public static partial void PlaylistReadded(this ILogger logger, long rowId, string playlistId, int position, string? editorUserId);

    [LoggerMessage(EventId = 8570, EventName = "playlist.readd_failed", Level = LogLevel.Warning,
        Message = "Row {RowId} could not be re-added to playlist {PlaylistId}: status {Status}, {Error}")]
    public static partial void PlaylistReaddFailed(this ILogger logger, long rowId, string playlistId, int? status, string error);

    [LoggerMessage(EventId = 8580, EventName = "promote.duplicate_in_library", Level = LogLevel.Warning,
        Message = "Row {RowId} not promoted: the library already has {TargetPath}; the song stays in music-radio, kept")]
    public static partial void PromoteDuplicate(this ILogger logger, long rowId, string targetPath);

    [LoggerMessage(EventId = 8595, EventName = "promote.restored", Level = LogLevel.Warning,
        Message = "Row {RowId} promotion stalled at step {Step}: the song was put back at {RadioPath} and kept; it is not promoted")]
    public static partial void PromoteRestored(this ILogger logger, long rowId, string step, string radioPath);

    [LoggerMessage(EventId = 8596, EventName = "promote.restore_failed", Level = LogLevel.Error,
        Message = "Row {RowId} stalled promotion: the song could not be put back in music-radio ({Error}); the copy is still in the staging folder")]
    public static partial void PromoteRestoreFailed(this ILogger logger, long rowId, string error);

    [LoggerMessage(EventId = 8590, EventName = "promote.paused", Level = LogLevel.Warning,
        Message = "Promotions paused: {Reason} ({Detail})")]
    public static partial void PromotePaused(this ILogger logger, string reason, string? detail);
}
