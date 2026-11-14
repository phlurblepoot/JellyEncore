using Encore.Config;
using Encore.Discovery;

namespace Encore.Fetch;

/// <summary>
/// One fetch request for an unowned pick (v0.2 design section 3.1). <see cref="Deadline"/> is normally
/// now + <see cref="FetchLimits.JobDeadline"/>; a pick that joins a live job keeps that job's deadline.
/// </summary>
public sealed record FetchRequest(
    string PickId, string RunId, string SessionId, Guid UserId,
    Candidate Candidate, bool DryRun, DateTimeOffset Deadline);

/// <summary>Where a job is. Searching and Downloading hold a slot; Importing and Ingesting do not.</summary>
public enum FetchStage { Queued, Searching, Downloading, Importing, Ingesting }

/// <summary>What a pick reads: still pending (with the job's stage), ready with its Jellyfin item, or failed.</summary>
public abstract record FetchReadiness
{
    private FetchReadiness()
    {
    }

    public sealed record Pending(FetchStage Stage) : FetchReadiness;

    public sealed record Ready(Guid ItemId) : FetchReadiness;

    /// <summary><see cref="Reason"/> is a wire name from <see cref="FetchReasons"/> (design section 6.3).</summary>
    public sealed record Failed(string Reason) : FetchReadiness;

    public bool IsTerminal => this is Ready or Failed;
}

/// <summary>Whether new requests are accepted; <see cref="Reason"/> is one of <see cref="AvailabilityReasons"/> when not.</summary>
public sealed record FetchAvailability(bool Available, string? Reason)
{
    public static readonly FetchAvailability Yes = new(true, null);
}

/// <summary>Job failure reasons (design section 6.3), beyond the ones the fetcher gives (<see cref="FetchFailure"/>).</summary>
public static class FetchReasons
{
    public const string ImportFailed = "import_failed";
    public const string IngestTimeout = "ingest_timeout";
    public const string Restart = "restart";
    public const string FileMissing = "file_missing";
    public const string Internal = "internal";
    public const string DryRun = "dry_run";
    public const string Off = "off";
    public const string Unknown = "unknown";
}

/// <summary>Why fetching is unavailable (design section 6.4), in the order they are checked.</summary>
public static class AvailabilityReasons
{
    public const string Off = "off";
    public const string NotConfigured = "not_configured";
    public const string SlskdUnreachable = "slskd_unreachable";
    public const string NotLoggedIn = "not_logged_in";
    public const string RadioDirNotInLibrary = "radio_dir_not_in_library";
    public const string DiskLow = "disk_low";
    public const string HourlyCap = "hourly_cap";
    public const string IndexNotReady = "index_not_ready";
}

/// <summary>Why queued or searching jobs were cancelled (<c>fetch.cancelled</c>).</summary>
public static class CancelReasons
{
    public const string RunEnd = "run_end";
    public const string Abandoned = "abandoned";
    public const string Deadline = "deadline";
    public const string Shutdown = "shutdown";
}

/// <summary>
/// The fetch side for /health (design section 12). Counters are since start; <see cref="Queued"/>, <see cref="InFlight"/>
/// (Searching plus Downloading) and <see cref="Ingesting"/> (Importing plus Ingesting) are gauges.
/// </summary>
public sealed record FetchStatus(
    FetchMode Mode,
    bool Available,
    string? UnavailableReason,
    int Queued,
    int InFlight,
    int Ingesting,
    int Ready,
    int Failed,
    int Cancelled,
    int WouldDownload,
    int LastHour,
    int HourlyCap,
    bool? SlskdReachable,
    bool? SlskdLoggedIn,
    DateTimeOffset? SlskdLastOkUtc,
    string? SlskdLastError,
    bool? RadioDirInLibrary,
    string? RadioLibrary,
    long? FreeBytes,
    int SourceLeftBehind,
    DateTimeOffset? LastReadyUtc,
    string? LastError)
{
    /// <summary>The status of mode off: nothing configured, all counters zero.</summary>
    public static readonly FetchStatus Off = new(
        FetchMode.Off, false, AvailabilityReasons.Off, 0, 0, 0, 0, 0, 0, 0, 0, FetchLimits.FetchesPerHour,
        null, null, null, null, null, null, null, 0, null, null);
}

/// <summary>
/// Fetch requests and pick readiness (design section 3.1), the only meeting point of the orchestrator and the fetch side.
/// Thread safe. Every member returns at once (no I/O, no lock held across an await) and none throws.
/// </summary>
public interface IFetchRequests
{
    FetchAvailability Availability { get; }

    /// <summary>Enqueues a job, or joins the live job for the same TrackKey. False when unavailable (the pick is then not planned).</summary>
    bool Request(FetchRequest request);

    /// <summary>Unknown pick ids (for example after a restart) read as Failed("unknown").</summary>
    FetchReadiness GetReadiness(string pickId);

    /// <summary>The pick was skipped or dropped: a Queued or Searching job with no other live pick is cancelled; later stages finish.</summary>
    void Abandon(string pickId, string reason);

    /// <summary>The run ended (design section 10): every Queued or Searching job held only by this run's picks is cancelled.</summary>
    void EndRun(string runId, string reason);

    FetchStatus Status { get; }
}

/// <summary>Mode off: nothing is ever fetched.</summary>
public sealed class NullFetchRequests : IFetchRequests
{
    public static readonly NullFetchRequests Instance = new();

    private static readonly FetchReadiness OffReadiness = new FetchReadiness.Failed(FetchReasons.Off);

    public FetchAvailability Availability { get; } = new(false, AvailabilityReasons.Off);

    public bool Request(FetchRequest request) => false;

    public FetchReadiness GetReadiness(string pickId) => OffReadiness;

    public void Abandon(string pickId, string reason)
    {
    }

    public void EndRun(string runId, string reason)
    {
    }

    public FetchStatus Status => FetchStatus.Off;
}
