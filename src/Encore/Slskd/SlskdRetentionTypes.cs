namespace Encore.Slskd;

/// <summary>
/// A finished upload from the events API. <see cref="LocalFilename"/> is slskd's own path of the file;
/// <see cref="CompletedUtc"/> is the record's timestamp.
/// </summary>
public sealed record SlskdUploadEvent(Guid TransferId, string Username, string LocalFilename, DateTimeOffset CompletedUtc);

/// <summary>
/// One events page. <see cref="RecordCount"/> counts every record returned (any type), <see cref="OldestTimestamp"/> is the
/// oldest record's timestamp (null for an empty page) so a reader can stop at its watermark, and <see cref="Total"/> is
/// the <c>X-Total-Count</c> header (null when absent). Uploads are in the order slskd sent them (newest first).
/// </summary>
public sealed record SlskdEventsPage(
    IReadOnlyList<SlskdUploadEvent> Uploads, int RecordCount, DateTimeOffset? OldestTimestamp, int? Total);

/// <summary>One upload transfer from /transfers/uploads. <see cref="Filename"/> is the remote name (peers see it with backslashes).</summary>
public sealed record SlskdUpload(Guid Id, string Username, string Filename, string State)
{
    /// <summary>Not finished: the state (comma-separated flags) does not contain "Completed".</summary>
    public bool IsActive => !State.Contains("Completed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One share of a host from GET /shares.</summary>
public sealed record SlskdShare(
    string Host, string Id, string Alias, bool IsExcluded, string LocalPath, string RemotePath, int Directories, int Files);

/// <summary>The <c>shares</c> part of /api/v0/application.</summary>
public sealed record SlskdShareState(bool Scanning, bool ScanPending, bool Ready, bool Faulted);
