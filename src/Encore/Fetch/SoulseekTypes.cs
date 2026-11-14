using Encore.Fetch.Scoring;

namespace Encore.Fetch;

/// <summary>
/// One song for <see cref="ISoulseekFetcher"/>. <see cref="TrackKey"/> is the owned-index key text (design section 5.1),
/// used for <c>fetch_attempts</c> and the negative cache. <see cref="Deadline"/> is 15 min after the first request.
/// </summary>
public sealed record SoulseekJob(
    string JobId, string TrackKey, string Artist, string Title, string? Album, int? DurationS, bool DryRun, DateTimeOffset Deadline);

/// <summary>A download request slskd accepted (peer number 1 to 3).</summary>
public sealed record EnqueuedDownload(string JobId, int PeerNo, ScoredFile File, Guid TransferId, string Query);

/// <summary>A finished download: <see cref="LocalPath"/> is Encore's path to slskd's copy. The transfer entry is already removed.</summary>
public sealed record DownloadedFile(
    string JobId, int PeerNo, ScoredFile File, Guid TransferId, string LocalPath, string Query, long? FetchedFileId, TimeSpan Elapsed);

/// <summary>
/// Callbacks the fetch worker gives each job. <see cref="OnEnqueued"/> runs after every accepted enqueue and may return the
/// <c>fetched_files</c> row id, which the fetcher puts on that peer's <c>fetch_attempts</c> rows. <see cref="VerifyAsync"/>
/// runs on each downloaded file: true keeps it, false (a mismatch) moves on to the next peer; an exception ends the job.
/// </summary>
public sealed record SoulseekFetchHooks(
    Func<EnqueuedDownload, long?>? OnEnqueued = null,
    Func<DownloadedFile, CancellationToken, Task<bool>>? VerifyAsync = null);

public abstract record SoulseekFetchResult
{
    private SoulseekFetchResult()
    {
    }

    public sealed record Downloaded(DownloadedFile File) : SoulseekFetchResult;

    /// <summary>Dry run: the file that would have been downloaded, and how many backup peers there were.</summary>
    public sealed record WouldDownload(ScoredFile Best, int Backups, string Query) : SoulseekFetchResult;

    /// <summary>Reason is a wire name from design section 6.3.</summary>
    public sealed record Failed(string Reason) : SoulseekFetchResult;
}

/// <summary>Wire names of job failure reasons (design section 6.3) that the fetcher can give.</summary>
public static class FetchFailure
{
    public const string NoResults = "no_results";
    public const string NoAcceptable = "no_acceptable";
    public const string Budget = "budget";
    public const string SlskdUnavailable = "slskd_unavailable";
    public const string AllPeersFailed = "all_peers_failed";
    public const string Deadline = "deadline";
    public const string Cancelled = "cancelled";
}

/// <summary>Why one peer was given up (<c>fetch_attempts.reason</c>, design section 6.3).</summary>
public static class PeerFailure
{
    public const string Queued = "queued";
    public const string Idle = "idle";
    public const string TooSlow = "too_slow";
    public const string Errored = "errored";
    public const string Rejected = "rejected";
    public const string Offline = "offline";
    public const string MissingFile = "missing_file";
    public const string Mismatch = "mismatch";
}

/// <summary>Searches Soulseek through slskd for one song and downloads the best copy (design section 6.2).</summary>
public interface ISoulseekFetcher
{
    /// <summary>
    /// <paramref name="soft"/> (run end, abandon, deadline) is honoured until the first enqueue, then ignored.
    /// <paramref name="stopping"/> (host shutdown) is honoured everywhere: a running download is cancelled and removed,
    /// then <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<SoulseekFetchResult> FetchAsync(SoulseekJob job, SoulseekFetchHooks? hooks, CancellationToken soft, CancellationToken stopping);
}
