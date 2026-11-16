namespace Encore.Retention;

/// <summary>Retention log events (v0.4 design section 12). This part holds the delete path, 8400-8430.</summary>
internal static partial class RetentionLog
{
    [LoggerMessage(EventId = 8400, EventName = "delete.done", Level = LogLevel.Information,
        Message = "Row {RowId} deleted ({Reason}): {Path} moved to trash {TrashPath}")]
    public static partial void DeleteDone(this ILogger logger, long rowId, string path, string reason, string trashPath);

    [LoggerMessage(EventId = 8410, EventName = "delete.failed", Level = LogLevel.Warning,
        Message = "Row {RowId} not deleted: {Path}: {Error}")]
    public static partial void DeleteFailed(this ILogger logger, long rowId, string path, string error);

    [LoggerMessage(EventId = 8420, EventName = "delete.skipped", Level = LogLevel.Information,
        Message = "Row {RowId} delete skipped, tried again next pass: {Why}")]
    public static partial void DeleteSkipped(this ILogger logger, long rowId, string why);

    [LoggerMessage(EventId = 8430, EventName = "trash.purged", Level = LogLevel.Information,
        Message = "Trash purged: {Folders} folders, {Files} files")]
    public static partial void TrashPurged(this ILogger logger, int folders, int files);
}
