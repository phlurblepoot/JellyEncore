using Microsoft.Extensions.Logging;

namespace Encore.Fetch;

/// <summary>Jellyfin ingest log events (v0.2 design section 13, ids 7330-7360, plus 7380).</summary>
internal static partial class IngestLog
{
    [LoggerMessage(EventId = 7330, EventName = "ingest.reported", Level = LogLevel.Debug,
        Message = "Reported job {JobId} to Jellyfin at {JellyfinPath}")]
    public static partial void IngestReported(this ILogger logger, string jobId, string jellyfinPath);

    [LoggerMessage(EventId = 7340, EventName = "ingest.ready", Level = LogLevel.Information,
        Message = "Job {JobId} is in Jellyfin as {ItemId} after {WaitedMs} ms")]
    public static partial void IngestReady(this ILogger logger, string jobId, Guid itemId, long waitedMs);

    [LoggerMessage(EventId = 7350, EventName = "ingest.timeout", Level = LogLevel.Warning,
        Message = "Job {JobId} did not show up in Jellyfin at {JellyfinPath} after {WaitedMs} ms")]
    public static partial void IngestTimeout(this ILogger logger, string jobId, string jellyfinPath, long waitedMs);

    [LoggerMessage(EventId = 7360, EventName = "ingest.library_check",
        Message = "Radio dir in a Jellyfin music library: {RadioDirInLibrary} (library {Library}, locations {Locations})")]
    public static partial void IngestLibraryCheck(this ILogger logger, LogLevel level, bool radioDirInLibrary, string? library, string locations);

    [LoggerMessage(EventId = 7380, EventName = "ingest.poll_failed", Level = LogLevel.Warning,
        Message = "Jellyfin poll for new songs failed, will try again: {Error}")]
    public static partial void IngestPollFailed(this ILogger logger, string error);
}
