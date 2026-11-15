using Microsoft.Extensions.Logging;

namespace Encore.Fetch.Import;

/// <summary>Import log events (v0.2 design section 13, ids 73xx).</summary>
internal static partial class ImportLog
{
    [LoggerMessage(EventId = 7300, EventName = "import.done", Level = LogLevel.Information,
        Message = "Imported job {JobId} to {Path} ({Format}, {Kbps} kbps, {DurationS} s)")]
    public static partial void ImportDone(this ILogger logger, string jobId, string path, string format, int kbps, int durationS);

    [LoggerMessage(EventId = 7310, EventName = "import.failed", Level = LogLevel.Warning,
        Message = "Import of job {JobId} failed: {Reason}: {Error}")]
    public static partial void ImportFailed(this ILogger logger, string jobId, string reason, string error);

    [LoggerMessage(EventId = 7320, EventName = "import.source_left", Level = LogLevel.Warning,
        Message = "slskd copy of job {JobId} left behind at {Path}: {Error}")]
    public static partial void ImportSourceLeft(this ILogger logger, string jobId, string path, string error);

    [LoggerMessage(EventId = 7371, EventName = "import.folder_created", Level = LogLevel.Information,
        Message = "Created the soulseek folder in {RadioDir}")]
    public static partial void ImportFolderCreated(this ILogger logger, string radioDir);

    [LoggerMessage(EventId = 7372, EventName = "import.folder_failed", Level = LogLevel.Warning,
        Message = "Could not create the soulseek folder in {RadioDir}: {Error}")]
    public static partial void ImportFolderFailed(this ILogger logger, string radioDir, string error);

    [LoggerMessage(EventId = 7370, EventName = "import.cleanup", Level = LogLevel.Information,
        Message = "Import cleanup: {StagedDeleted} staged files deleted, {RowsFailed} rows failed, {RowsReingested} rows re-ingested, {OrphanDirsDeleted} orphan folders deleted")]
    public static partial void ImportCleanup(this ILogger logger, int stagedDeleted, int rowsFailed, int rowsReingested, int orphanDirsDeleted);
}
