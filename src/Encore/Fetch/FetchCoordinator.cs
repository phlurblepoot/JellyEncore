using System.Collections.Concurrent;
using System.Threading.Channels;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch.Import;

namespace Encore.Fetch;

/// <summary>
/// One fetch job: one song (one <see cref="TrackKey"/>), shared by every pick that asked for it while it was live.
/// The coordinator changes its stage and readiness under its lock; <see cref="Readiness"/> is read without one.
/// </summary>
internal sealed class FetchJob
{
    private volatile FetchReadiness _readiness = new FetchReadiness.Pending(FetchStage.Queued);

    public FetchJob(string jobId, FetchRequest first, DateTimeOffset createdUtc)
    {
        JobId = jobId;
        Candidate = first.Candidate;
        DryRun = first.DryRun;
        Deadline = first.Deadline;
        RunId = first.RunId;
        SessionId = first.SessionId;
        UserId = first.UserId;
        CreatedUtc = createdUtc;
    }

    public string JobId { get; }

    public Candidate Candidate { get; }

    public TrackKey Key => Candidate.Key;

    public bool DryRun { get; }

    public DateTimeOffset Deadline { get; }

    /// <summary>The first request's run, session and user (they go on the <c>fetched_files</c> row).</summary>
    public string RunId { get; }

    public string SessionId { get; }

    public Guid UserId { get; }

    public DateTimeOffset CreatedUtc { get; }

    public FetchReadiness Readiness
    {
        get => _readiness;
        set => _readiness = value;
    }

    public bool IsTerminal => _readiness.IsTerminal;

    // ---- coordinator state (under the coordinator lock)
    public FetchStage Stage { get; set; } = FetchStage.Queued;

    public CancellationTokenSource Soft { get; } = new();

    public string? CancelReason { get; set; }

    public List<PickRef> Holders { get; } = [];

    /// <summary>Holds one place in the hourly cap until it reaches Downloading (or a would-download) or ends.</summary>
    public bool Reserved { get; set; }

    /// <summary>Counted in the hourly window (reached Downloading or a would-download).</summary>
    public bool Counted { get; set; }

    public DateTimeOffset? FinishedUtc { get; set; }

    // ---- worker state (only the job's own task touches these)
    public long? RowId { get; set; }

    public string? Peer { get; set; }

    public ImportResult? Import { get; set; }
}

/// <summary>A pick attached to a job. <see cref="Live"/> turns false when the pick is abandoned or its run ends.</summary>
internal sealed class PickRef(string pickId, string runId, string sessionId, FetchJob job)
{
    public string PickId { get; } = pickId;

    public string RunId { get; } = runId;

    public string SessionId { get; } = sessionId;

    public FetchJob Job { get; } = job;

    public bool Live { get; set; } = true;
}

/// <summary>
/// <see cref="IFetchRequests"/> for dry run and live mode (v0.2 design sections 3.1, 6.1 and 6.4): one job per
/// TrackKey, joined by later picks; a channel the <see cref="FetchWorker"/> reads; a thread-safe readiness view per
/// pick; the hourly cap (30 jobs per rolling hour reach Downloading or a would-download, all users; a job holds a
/// place from its request until it gets there or ends); availability from the worker's periodic checks. Every public
/// member returns at once and never throws; no lock is held while a cancellation token is cancelled.
/// </summary>
public sealed class FetchCoordinator : IFetchRequests
{
    /// <summary>Picks of jobs that ended longer ago than this are forgotten (they then read Failed("unknown")).</summary>
    public static readonly TimeSpan ForgetPicksAfter = TimeSpan.FromHours(1);

    private static readonly FetchReadiness UnknownReadiness = new FetchReadiness.Failed(FetchReasons.Unknown);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, PickRef> _picks = new(StringComparer.Ordinal);
    private readonly Dictionary<TrackKey, FetchJob> _live = [];
    private readonly HashSet<FetchJob> _active = [];
    /// <summary>Songs whose file is in music-radio but not yet seen by Jellyfin (ingest timed out): never fetched again.</summary>
    private readonly HashSet<TrackKey> _importedPending = [];
    private readonly Channel<FetchJob> _queue = Channel.CreateUnbounded<FetchJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<DateTimeOffset> _window = new();
    private readonly IOwnedIndex _owned;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly bool _configured;

    private int _reserved;
    private bool? _slskdReachable;
    private bool? _slskdLoggedIn;
    private bool _slskdKeyRejected;
    private DateTimeOffset? _slskdLastOk;
    private string? _slskdLastError;
    private bool? _inLibrary;
    private string? _libraryName;
    private long? _freeBytes;

    private int _ready;
    private int _failed;
    private int _cancelled;
    private int _wouldDownload;
    private int _sourceLeftBehind;
    private DateTimeOffset? _lastReady;
    private string? _lastError;

    private bool _availabilityKnown;
    private string? _lastReason;

    /// <param name="mode">Dry run or live (mode off uses <see cref="NullFetchRequests"/>).</param>
    /// <param name="owned">The owned index; requests wait until it is loaded (index_not_ready).</param>
    /// <param name="configured">False when the slskd URL or key is missing (not_configured).</param>
    public FetchCoordinator(FetchMode mode, IOwnedIndex owned, ILogger<FetchCoordinator> log, TimeProvider? time = null, bool configured = true)
    {
        Mode = mode;
        _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        _configured = configured;
    }

    public FetchMode Mode { get; }

    internal ChannelReader<FetchJob> Queue => _queue.Reader;

    // ================================================================ IFetchRequests

    public FetchAvailability Availability
    {
        get
        {
            lock (_gate)
            {
                var reason = ReasonLocked(_time.GetUtcNow());
                NoteAvailabilityLocked(reason);
                return reason is null ? FetchAvailability.Yes : new FetchAvailability(false, reason);
            }
        }
    }

    public bool Request(FetchRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PickId) || request.Candidate is null)
        {
            return false;
        }

        try
        {
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                if (_picks.ContainsKey(request.PickId))
                {
                    return true;
                }

                var reason = ReasonLocked(now);
                NoteAvailabilityLocked(reason);

                // The file is already in music-radio, waiting for Jellyfin: a second download would duplicate it.
                if (_importedPending.Contains(request.Candidate.Key))
                {
                    return false;
                }

                // Joining a live job adds no download, so only the hourly cap does not stop it.
                if (_live.TryGetValue(request.Candidate.Key, out var live) && !live.IsTerminal
                    && (reason is null || reason == AvailabilityReasons.HourlyCap))
                {
                    var joined = new PickRef(request.PickId, request.RunId, request.SessionId, live);
                    live.Holders.Add(joined);
                    _picks[request.PickId] = joined;
                    LogRequested(live, request, joined: true);
                    return true;
                }

                if (reason is not null)
                {
                    return false;
                }

                var job = new FetchJob(Guid.NewGuid().ToString("N"), request, now) { Reserved = true };
                _reserved++;
                var pick = new PickRef(request.PickId, request.RunId, request.SessionId, job);
                job.Holders.Add(pick);
                _picks[request.PickId] = pick;
                _live[job.Key] = job;
                _active.Add(job);
                _queue.Writer.TryWrite(job);
                LogRequested(job, request, joined: false);
                return true;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.FetchJobError("-", "request", e.GetType().Name);
            return false;
        }
    }

    public FetchReadiness GetReadiness(string pickId) =>
        pickId is not null && _picks.TryGetValue(pickId, out var pick) ? pick.Job.Readiness : UnknownReadiness;

    public void Abandon(string pickId, string reason)
    {
        if (pickId is null || !_picks.TryGetValue(pickId, out var pick))
        {
            return;
        }

        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (!pick.Live)
            {
                return;
            }

            pick.Live = false;
            if (!pick.Job.Holders.Any(h => h.Live))
            {
                cancel = CancelLocked(pick.Job, CancelReasons.Abandoned);
            }
        }

        CancelOutside(cancel);
    }

    public void EndRun(string runId, string reason, string? callerSessionId = null)
    {
        if (runId is null)
        {
            return;
        }

        var toCancel = new List<CancellationTokenSource>();
        var cancelled = 0;
        string? sessionId = callerSessionId;
        lock (_gate)
        {
            var jobs = new HashSet<FetchJob>();
            foreach (var pick in _picks.Values)
            {
                if (pick.Live && pick.RunId == runId)
                {
                    pick.Live = false;
                    sessionId ??= pick.SessionId;
                    jobs.Add(pick.Job);
                }
            }

            foreach (var job in jobs)
            {
                if (job.IsTerminal || job.Holders.Any(h => h.Live) || job.Stage is not (FetchStage.Queued or FetchStage.Searching))
                {
                    continue;
                }

                cancelled++;
                if (CancelLocked(job, CancelReasons.RunEnd) is { } cts)
                {
                    toCancel.Add(cts);
                }
            }
        }

        _log.RunEnded(runId, sessionId ?? "", reason ?? "", cancelled);
        foreach (var cts in toCancel)
        {
            CancelOutside(cts);
        }
    }

    public FetchStatus Status
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                var reason = ReasonLocked(now);
                int queued = 0, inFlight = 0, ingesting = 0;
                foreach (var job in _active)
                {
                    switch (job.Stage)
                    {
                        case FetchStage.Queued: queued++; break;
                        case FetchStage.Searching or FetchStage.Downloading: inFlight++; break;
                        default: ingesting++; break;
                    }
                }

                return new FetchStatus(
                    Mode, reason is null, reason, queued, inFlight, ingesting, _ready, _failed, _cancelled, _wouldDownload,
                    _window.Count, FetchLimits.FetchesPerHour, _slskdReachable, _slskdLoggedIn, _slskdLastOk, _slskdLastError,
                    _inLibrary, _libraryName, _freeBytes, _sourceLeftBehind, _lastReady, _lastError, _slskdKeyRejected);
            }
        }
    }

    // ================================================================ worker inputs (public for tests; only the worker calls them)

    /// <summary>The result of the last slskd server-state check (every 60 s).</summary>
    public void SetSlskdState(bool reachable, bool loggedIn, string? error, bool keyRejected = false)
    {
        lock (_gate)
        {
            _slskdReachable = reachable;
            _slskdLoggedIn = loggedIn;
            _slskdLastError = error;
            _slskdKeyRejected = keyRejected;
            if (reachable)
            {
                _slskdLastOk = _time.GetUtcNow();
            }
        }

        RefreshAvailability();
    }

    /// <summary>Is the radio dir in a Jellyfin music library (checked at start and every 30 min).</summary>
    public void SetLibrary(bool inLibrary, string? libraryName)
    {
        lock (_gate)
        {
            _inLibrary = inLibrary;
            _libraryName = libraryName;
        }

        RefreshAvailability();
    }

    /// <summary>Free bytes of the radio dir (every 5 min and after a disk_low import); null when it cannot be read.</summary>
    public void SetFreeBytes(long? freeBytes)
    {
        lock (_gate)
        {
            _freeBytes = freeBytes;
        }

        RefreshAvailability();
    }

    /// <summary>Puts the times of downloads requested in the last hour (from the store) into the hourly window.</summary>
    public void SeedWindow(IEnumerable<DateTimeOffset> times)
    {
        ArgumentNullException.ThrowIfNull(times);
        lock (_gate)
        {
            var all = _window.Concat(times).OrderBy(t => t).ToList();
            _window.Clear();
            foreach (var t in all)
            {
                _window.Enqueue(t);
            }

            PruneWindowLocked(_time.GetUtcNow());
        }
    }

    /// <summary>Re-evaluates availability and logs <c>fetch.unavailable</c> or <c>fetch.available</c> when it changed.</summary>
    public FetchAvailability RefreshAvailability() => Availability;

    /// <summary>
    /// Housekeeping (the worker calls it every second): Queued jobs past their deadline fail with <c>deadline</c>,
    /// Searching ones are cancelled with that reason; picks of jobs that ended over an hour ago are forgotten.
    /// </summary>
    public void Sweep()
    {
        var toCancel = new List<CancellationTokenSource>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var job in _active.ToList())
            {
                if (now >= job.Deadline && job.Stage is (FetchStage.Queued or FetchStage.Searching) && job.CancelReason is null
                    && CancelLocked(job, CancelReasons.Deadline) is { } cts)
                {
                    toCancel.Add(cts);
                }
            }

            foreach (var (id, pick) in _picks)
            {
                if (pick.Job.FinishedUtc is { } done && now - done > ForgetPicksAfter)
                {
                    _picks.TryRemove(id, out _);
                }
            }

            NoteAvailabilityLocked(ReasonLocked(now));
        }

        foreach (var cts in toCancel)
        {
            CancelOutside(cts);
        }
    }

    // ================================================================ worker transitions

    /// <summary>The worker got a slot for the job. False when it already ended (cancelled while queued) or is past its deadline.</summary>
    internal bool TryStart(FetchJob job)
    {
        lock (_gate)
        {
            if (job.IsTerminal)
            {
                return false;
            }

            if (_time.GetUtcNow() >= job.Deadline)
            {
                _log.FetchCancelled(job.JobId, CancelReasons.Deadline);
                FinishFailedLocked(job, FetchFailure.Deadline);
                return false;
            }

            SetStageLocked(job, FetchStage.Searching);
            return true;
        }
    }

    internal void MarkStage(FetchJob job, FetchStage stage)
    {
        lock (_gate)
        {
            if (!job.IsTerminal)
            {
                SetStageLocked(job, stage);
            }
        }
    }

    /// <summary>The first (or a later) peer was enqueued: the job counts in the hourly window from now on.</summary>
    internal void MarkDownloading(FetchJob job)
    {
        lock (_gate)
        {
            CountLocked(job);
            if (!job.IsTerminal)
            {
                SetStageLocked(job, FetchStage.Downloading);

                // Cancelled while searching, but the enqueue went through: the download goes on, so later requests
                // for the song join this job instead of starting a second download.
                if (job.CancelReason is not null && !_live.ContainsKey(job.Key))
                {
                    _live[job.Key] = job;
                }
            }
        }
    }

    /// <summary>The song's file is in music-radio and Jellyfin has not reported it yet: no new job for it until it is owned.</summary>
    internal void NoteImportedPending(TrackKey key)
    {
        lock (_gate)
        {
            _importedPending.Add(key);
        }
    }

    internal void ClearImportedPending(TrackKey key)
    {
        lock (_gate)
        {
            _importedPending.Remove(key);
        }
    }

    /// <summary>Dry run: the job found the copy it would download. It counts in the hourly window and reads Failed("dry_run").</summary>
    internal void MarkWouldDownload(FetchJob job)
    {
        lock (_gate)
        {
            CountLocked(job);
            _wouldDownload++;
            FinishFailedLocked(job, FetchReasons.DryRun);
        }
    }

    internal void Complete(FetchJob job, Guid itemId)
    {
        lock (_gate)
        {
            if (job.IsTerminal)
            {
                return;
            }

            var now = _time.GetUtcNow();
            job.Readiness = new FetchReadiness.Ready(itemId);
            _importedPending.Remove(job.Key);
            EndLocked(job, now);
            _ready++;
            _lastReady = now;
            _log.FetchReady(job.JobId, itemId, job.Candidate.Artist, job.Candidate.Title, job.Peer, (long)(now - job.CreatedUtc).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Ends the job as failed and returns the reason used: a <c>cancelled</c> result of a job cancelled for its
    /// deadline reads <c>deadline</c>.
    /// </summary>
    internal string Fail(FetchJob job, string reason)
    {
        lock (_gate)
        {
            if (reason == FetchFailure.Cancelled && job.CancelReason == CancelReasons.Deadline)
            {
                reason = FetchFailure.Deadline;
            }

            if (!job.IsTerminal)
            {
                FinishFailedLocked(job, reason);
                if (reason == FetchReasons.IngestTimeout)
                {
                    _importedPending.Add(job.Key);
                }
            }

            return reason;
        }
    }

    /// <summary>Host shutdown: the job was stopped (it reads Failed("cancelled")).</summary>
    internal void Stopped(FetchJob job)
    {
        lock (_gate)
        {
            if (!job.IsTerminal)
            {
                _log.FetchCancelled(job.JobId, CancelReasons.Shutdown);
                FinishFailedLocked(job, FetchFailure.Cancelled);
            }
        }
    }

    /// <summary>Host shutdown: every Queued job fails as cancelled and every Searching one is cancelled.</summary>
    internal void CancelAll()
    {
        var toCancel = new List<CancellationTokenSource>();
        lock (_gate)
        {
            foreach (var job in _active.ToList())
            {
                if (CancelLocked(job, CancelReasons.Shutdown) is { } cts)
                {
                    toCancel.Add(cts);
                }
            }
        }

        foreach (var cts in toCancel)
        {
            CancelOutside(cts);
        }
    }

    internal void NoteError(string error)
    {
        lock (_gate)
        {
            _lastError = error;
        }
    }

    internal void NoteSourceLeftBehind()
    {
        lock (_gate)
        {
            _sourceLeftBehind++;
        }
    }

    // ================================================================ internals (call under _gate)

    private string? ReasonLocked(DateTimeOffset now)
    {
        if (Mode == FetchMode.Off)
        {
            return AvailabilityReasons.Off;
        }

        if (!_configured)
        {
            return AvailabilityReasons.NotConfigured;
        }

        if (_slskdKeyRejected)
        {
            return AvailabilityReasons.SlskdKeyRejected;
        }

        if (_slskdReachable != true)
        {
            return AvailabilityReasons.SlskdUnreachable;
        }

        if (_slskdLoggedIn != true)
        {
            return AvailabilityReasons.NotLoggedIn;
        }

        if (Mode == FetchMode.Live && _inLibrary == false)
        {
            return AvailabilityReasons.RadioDirNotInLibrary;
        }

        if (Mode == FetchMode.Live && _freeBytes is { } free && free < FetchLimits.FreeSpaceFloorBytes)
        {
            return AvailabilityReasons.DiskLow;
        }

        PruneWindowLocked(now);
        if (_window.Count + _reserved >= FetchLimits.FetchesPerHour)
        {
            return AvailabilityReasons.HourlyCap;
        }

        if (!_owned.IsLoaded)
        {
            return AvailabilityReasons.IndexNotReady;
        }

        return null;
    }

    private void NoteAvailabilityLocked(string? reason)
    {
        if (_availabilityKnown && reason == _lastReason)
        {
            return;
        }

        var wasKnown = _availabilityKnown;
        _availabilityKnown = true;
        _lastReason = reason;
        if (reason is not null)
        {
            _log.FetchUnavailable(reason);
        }
        else if (wasKnown)
        {
            _log.FetchAvailable();
        }
    }

    private void PruneWindowLocked(DateTimeOffset now)
    {
        var since = now - TimeSpan.FromHours(1);
        while (_window.Count > 0 && _window.Peek() <= since)
        {
            _window.Dequeue();
        }
    }

    private void CountLocked(FetchJob job)
    {
        if (job.Counted)
        {
            return;
        }

        job.Counted = true;
        ReleaseReservationLocked(job);
        _window.Enqueue(_time.GetUtcNow());
    }

    private void ReleaseReservationLocked(FetchJob job)
    {
        if (job.Reserved)
        {
            job.Reserved = false;
            _reserved--;
        }
    }

    private void SetStageLocked(FetchJob job, FetchStage stage)
    {
        if (job.Stage == stage)
        {
            return;
        }

        _log.FetchState(job.JobId, Wire(job.Stage), Wire(stage));
        job.Stage = stage;
        job.Readiness = new FetchReadiness.Pending(stage);
    }

    /// <summary>Queued: fails at once. Searching: marks the reason and returns the token to cancel (outside the lock). Later stages go on.</summary>
    private CancellationTokenSource? CancelLocked(FetchJob job, string why)
    {
        if (job.IsTerminal || job.CancelReason is not null)
        {
            return null;
        }

        if (job.Stage == FetchStage.Queued)
        {
            job.CancelReason = why;
            _log.FetchCancelled(job.JobId, why);
            FinishFailedLocked(job, why == CancelReasons.Deadline ? FetchFailure.Deadline : FetchFailure.Cancelled);
            return null;
        }

        if (job.Stage == FetchStage.Searching)
        {
            job.CancelReason = why;
            _log.FetchCancelled(job.JobId, why);
            if (_live.TryGetValue(job.Key, out var live) && ReferenceEquals(live, job))
            {
                _live.Remove(job.Key);   // a new request for the song starts a new job
            }

            return job.Soft;
        }

        return null;
    }

    private void FinishFailedLocked(FetchJob job, string reason)
    {
        job.Readiness = new FetchReadiness.Failed(reason);
        EndLocked(job, _time.GetUtcNow());
        switch (reason)
        {
            case FetchFailure.Cancelled:
                _cancelled++;
                break;
            case FetchReasons.DryRun:
                break;
            default:
                _failed++;
                _log.FetchFailed(job.JobId, job.Candidate.Artist, job.Candidate.Title, reason);
                break;
        }
    }

    private void EndLocked(FetchJob job, DateTimeOffset now)
    {
        job.FinishedUtc = now;
        ReleaseReservationLocked(job);
        _active.Remove(job);
        if (_live.TryGetValue(job.Key, out var live) && ReferenceEquals(live, job))
        {
            _live.Remove(job.Key);
        }
    }

    private void LogRequested(FetchJob job, FetchRequest request, bool joined) =>
        _log.FetchRequested(job.JobId, request.PickId, request.RunId, request.SessionId, job.Candidate.Artist, job.Candidate.Title,
            job.Candidate.Origin.ToWire(), job.DryRun, joined);

    private static void CancelOutside(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            // A callback threw; the job sees the cancellation anyway.
        }
    }

    internal static string Wire(FetchStage stage) => stage switch
    {
        FetchStage.Queued => "queued",
        FetchStage.Searching => "searching",
        FetchStage.Downloading => "downloading",
        FetchStage.Importing => "importing",
        FetchStage.Ingesting => "ingesting",
        _ => stage.ToString().ToLowerInvariant(),
    };
}
