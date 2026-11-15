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
        Priority = first.Priority;
        Preference = first.Preference;
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

    /// <summary>Request once any request pick joined; a queued job then moves to the request queue.</summary>
    public FetchPriority Priority { get; set; }

    /// <summary>Changed by a joining request only while Queued; the worker reads it once the job is Searching.</summary>
    public FetchPreference Preference { get; set; }

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
/// TrackKey, joined by later picks; a two-level queue (request jobs first, v0.3 design section 6.2) the
/// <see cref="FetchWorker"/> takes from; a thread-safe readiness view per
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
    /// <summary>The two-level queue (v0.3 design section 6.2): request jobs are always taken before radio jobs. Both FIFO.</summary>
    private readonly List<FetchJob> _requestQueue = [];
    private readonly List<FetchJob> _radioQueue = [];
    /// <summary>An auto-reset wake-up for the worker's dispatcher: new work, a freed slot, or the sweep with jobs queued.</summary>
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
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
    private bool? _scanned;
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

    /// <summary>Waits until <see cref="Signal"/> was called since the last wait returned (the worker's dispatcher only).</summary>
    internal async Task WaitForWorkAsync(CancellationToken ct) => await _wake.Reader.ReadAsync(ct).ConfigureAwait(false);

    /// <summary>Wakes the dispatcher: there may be a job it can start now.</summary>
    internal void Signal() => _wake.Writer.TryWrite(0);

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

                // A request job is never refused at the hourly cap: it waits in Queued (v0.3 design section 6.2).
                var isRequest = request.Priority == FetchPriority.Request;
                if (isRequest && reason == AvailabilityReasons.HourlyCap)
                {
                    reason = ReasonLocked(now, ignoreCap: true);
                }

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
                    if (isRequest)
                    {
                        UpgradeLocked(live, request.Preference);
                    }

                    LogRequested(live, request, joined: true);
                    return true;
                }

                if (reason is not null)
                {
                    return false;
                }

                // A radio job holds its place in the hourly cap from now; a request job takes one when it leaves the queue.
                var job = new FetchJob(Guid.NewGuid().ToString("N"), request, now) { Reserved = !isRequest };
                if (!isRequest)
                {
                    _reserved++;
                }

                var pick = new PickRef(request.PickId, request.RunId, request.SessionId, job);
                job.Holders.Add(pick);
                _picks[request.PickId] = pick;
                _live[job.Key] = job;
                _active.Add(job);
                (isRequest ? _requestQueue : _radioQueue).Add(job);
                if (isRequest)
                {
                    MarkCapWaitLocked(now);
                }

                LogRequested(job, request, joined: false);
                Signal();
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

    public void Forget(string pickId)
    {
        if (pickId is null || !_picks.TryGetValue(pickId, out var pick))
        {
            return;
        }

        if (!pick.Job.IsTerminal)
        {
            Abandon(pickId, "forgotten");
        }

        _picks.TryRemove(pickId, out _);
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
                    _inLibrary, _libraryName, _freeBytes, _sourceLeftBehind, _lastReady, _lastError, _slskdKeyRejected, _scanned);
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

    /// <summary>Has Jellyfin scanned the radio dir yet (from the library check and every ingest)? Fetching stays available when not.</summary>
    public void SetScanned(bool scanned)
    {
        lock (_gate)
        {
            _scanned = scanned;
        }
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

            // Request picks wait for Forget (the request worker reads their terminal state first); the backstop catches leaks.
            foreach (var (id, pick) in _picks)
            {
                var keep = IsRequestPick(id) ? FetchLimits.RequestPicksBackstop : ForgetPicksAfter;
                if (pick.Job.FinishedUtc is { } done && now - done > keep)
                {
                    _picks.TryRemove(id, out _);
                }
            }

            NoteAvailabilityLocked(ReasonLocked(now));
            MarkCapWaitLocked(now);
            if (_requestQueue.Count > 0 || _radioQueue.Count > 0)
            {
                Signal();   // a request job may have been waiting for the hourly window to free
            }
        }

        foreach (var cts in toCancel)
        {
            CancelOutside(cts);
        }
    }

    /// <summary>Song requests use pick ids <c>req:{id}</c> (v0.3 design section 6.1).</summary>
    public static bool IsRequestPick(string pickId) => pickId.StartsWith("req:", StringComparison.Ordinal);

    // ================================================================ worker transitions

    /// <summary>
    /// The dispatcher holds a free request slot and/or a free general slot: takes the next job that can start and moves it
    /// to Searching. Request jobs come first (in either slot, the request slot preferred); one without a place in the hourly
    /// cap stays queued. Radio jobs need a general slot. Jobs that ended while queued are dropped; one past its deadline
    /// fails. Null when nothing can start now.
    /// </summary>
    internal FetchJob? TryTake(bool requestSlot, bool generalSlot, out bool usedRequestSlot)
    {
        usedRequestSlot = false;
        if (!requestSlot && !generalSlot)
        {
            return null;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _requestQueue.RemoveAll(j => j.IsTerminal);
            _radioQueue.RemoveAll(j => j.IsTerminal);
            PruneWindowLocked(now);

            for (var i = 0; i < _requestQueue.Count;)
            {
                var job = _requestQueue[i];
                if (!job.Reserved)
                {
                    if (_window.Count + _reserved >= FetchLimits.FetchesPerHour)
                    {
                        i++;   // waits for the hourly window; a reserved (joined radio) job behind it may still start
                        continue;
                    }

                    job.Reserved = true;
                    _reserved++;
                }

                _requestQueue.RemoveAt(i);
                if (StartLocked(job, now))
                {
                    usedRequestSlot = requestSlot;
                    MarkCapWaitLocked(now);
                    return job;
                }
            }

            MarkCapWaitLocked(now);
            while (generalSlot && _radioQueue.Count > 0)
            {
                var job = _radioQueue[0];
                _radioQueue.RemoveAt(0);
                if (StartLocked(job, now))
                {
                    return job;
                }
            }

            return null;
        }
    }

    /// <summary>Moves the job to Searching. False when it already ended (cancelled while queued) or is past its deadline.</summary>
    private bool StartLocked(FetchJob job, DateTimeOffset now)
    {
        if (job.IsTerminal)
        {
            return false;
        }

        if (now >= job.Deadline)
        {
            _log.FetchCancelled(job.JobId, CancelReasons.Deadline);
            FinishFailedLocked(job, FetchFailure.Deadline);
            return false;
        }

        SetStageLocked(job, FetchStage.Searching);
        return true;
    }

    /// <summary>The job's download preference, read by the worker once the job is Searching (it no longer changes then).</summary>
    internal FetchPreference PreferenceOf(FetchJob job)
    {
        lock (_gate)
        {
            return job.Preference;
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

    /// <param name="ignoreCap">True for a request job, which waits at the hourly cap instead of being refused.</param>
    private string? ReasonLocked(DateTimeOffset now, bool ignoreCap = false)
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

        // Queued request jobs without a place yet count too, so requests get the free capacity first.
        PruneWindowLocked(now);
        if (!ignoreCap && _window.Count + _reserved + WaitingRequestsLocked() >= FetchLimits.FetchesPerHour)
        {
            return AvailabilityReasons.HourlyCap;
        }

        if (!_owned.IsLoaded)
        {
            return AvailabilityReasons.IndexNotReady;
        }

        return null;
    }

    /// <summary>Queued request jobs that hold no place in the hourly cap yet.</summary>
    private int WaitingRequestsLocked()
    {
        var n = 0;
        foreach (var job in _requestQueue)
        {
            if (!job.IsTerminal && !job.Reserved)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Marks queued request jobs without a place as waiting for the hourly cap (or clears the mark when there is room).</summary>
    private void MarkCapWaitLocked(DateTimeOffset now)
    {
        if (_requestQueue.Count == 0)
        {
            return;
        }

        PruneWindowLocked(now);
        var full = _window.Count + _reserved >= FetchLimits.FetchesPerHour;
        foreach (var job in _requestQueue)
        {
            if (job.IsTerminal || job.Reserved || job.Stage != FetchStage.Queued)
            {
                continue;
            }

            var waiting = full ? FetchWaiting.HourlyCap : null;
            if (job.Readiness is FetchReadiness.Pending p && p.Waiting != waiting)
            {
                job.Readiness = new FetchReadiness.Pending(FetchStage.Queued) { Waiting = waiting };
            }
        }
    }

    /// <summary>
    /// A request pick joined the job: it becomes a request job (moved to the request queue if still queued). Its download
    /// preference changes only while it is queued; a job already searching keeps what it started with.
    /// </summary>
    private void UpgradeLocked(FetchJob job, FetchPreference preference)
    {
        if (job.Stage == FetchStage.Queued && job.Preference != preference && preference != FetchPreference.Fastest)
        {
            job.Preference = preference;
        }

        if (job.Priority == FetchPriority.Request)
        {
            return;
        }

        job.Priority = FetchPriority.Request;
        if (job.Stage == FetchStage.Queued && _radioQueue.Remove(job))
        {
            _requestQueue.Add(job);
            Signal();
        }
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
