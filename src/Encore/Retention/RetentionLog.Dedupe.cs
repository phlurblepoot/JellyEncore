namespace Encore.Retention;

/// <summary>Retention log events (v0.4 design section 12). This part holds the library-copy path (T141), 8600-8630.</summary>
internal static partial class RetentionLog
{
    [LoggerMessage(EventId = 8600, EventName = "dedupe.started", Level = LogLevel.Information,
        Message = "Row {RowId} not promoted: the library already has the song as item {LibraryItemId} at {LibraryPath}; moving favorites, playlist places and requests there")]
    public static partial void DedupeStarted(this ILogger logger, long rowId, string libraryItemId, string? libraryPath);

    [LoggerMessage(EventId = 8610, EventName = "dedupe.done", Level = LogLevel.Information,
        Message = "Row {RowId} radio copy {RadioPath} moved to trash {TrashPath}: item {OldItemId} replaced by library item {LibraryItemId}")]
    public static partial void DedupeDone(this ILogger logger, long rowId, string? radioPath, string? trashPath, string oldItemId, string libraryItemId);

    [LoggerMessage(EventId = 8620, EventName = "dedupe.kept", Level = LogLevel.Warning,
        Message = "Row {RowId} radio copy kept: moving it to library item {LibraryItemId} failed at step {Step} (attempt {Attempts}): {Error}")]
    public static partial void DedupeKept(this ILogger logger, long rowId, string libraryItemId, string step, int attempts, string error);

    [LoggerMessage(EventId = 8630, EventName = "dedupe.restore_failed", Level = LogLevel.Error,
        Message = "Row {RowId} radio copy could not be put back from the trash {TrashPath}: {Error}")]
    public static partial void DedupeRestoreFailed(this ILogger logger, long rowId, string trashPath, string error);
}
