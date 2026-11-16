using Encore.Fetch;
using Encore.Slskd;

namespace Encore.Tests.Fetch;

/// <summary>
/// A clock for one sequential flow: every timer (Task.Delay) moves the clock forward by its due time at once and fires
/// right away. Waits take no real time and elapsed times are exact.
/// </summary>
internal sealed class AutoClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class Timer(AutoClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool _fired;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan || _fired)
            {
                return true;
            }

            _fired = true;
            clock.Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>One scripted search: its responses, when slskd calls it complete, and how StartSearch answers.</summary>
internal sealed record SearchScript(IReadOnlyList<SlskdSearchResponse> Responses, TimeSpan CompleteAfter, Exception? StartError = null,
    int BusyTimes = 0, Exception? ResponsesError = null);

/// <summary>A transfer's state at a time after the enqueue.</summary>
internal sealed record TransferStep(string State, long Bytes, double Speed, DateTimeOffset? NextAttemptAt = null);

/// <summary>How one peer behaves on enqueue and while downloading. <see cref="SaveAs"/> null = the remote name's last segment; WriteFile false = nothing on disk.</summary>
internal sealed record TransferScript(Func<TimeSpan, TransferStep> At, Exception? EnqueueError = null, bool Refuse = false,
    string? SaveAs = null, bool WriteFile = true);

/// <summary>In-memory slskd with scripted searches (by query text) and transfers (by peer), recording every call.</summary>
internal sealed class FakeSlskd(TimeProvider clock, string downloadsDir) : ISlskdClient, IRetentionSlskd
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (SearchScript Script, DateTimeOffset At, bool Cancelled)> _searches = [];
    private readonly Dictionary<Guid, (string User, SlskdEnqueueRequest Request, TransferScript Script, DateTimeOffset At)> _transfers = [];

    public Dictionary<string, SearchScript> Searches { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TransferScript> Peers { get; } = new(StringComparer.Ordinal);
    public List<string> Started { get; } = [];
    public List<Guid> StartedIds { get; } = [];
    public List<Guid> CancelledSearches { get; } = [];
    public List<Guid> DeletedSearches { get; } = [];
    public List<SlskdEnqueueRequest> Enqueued { get; } = [];
    public List<(string User, Guid Id, bool Remove)> CancelledTransfers { get; } = [];
    public int GetSearchCalls { get; private set; }
    public int GetTransferCalls { get; private set; }

    /// <summary>Runs on each call by operation name ("get_search", "get_transfer", ...), before it answers.</summary>
    public Action<string>? OnCall { get; set; }

    private int _busyLeft = -1;

    /// <summary>Set to make slskd unreachable: the state check and new searches throw this.</summary>
    public Exception? Down { get; set; }

    public Task<SlskdServerState> GetServerStateAsync(CancellationToken ct) =>
        Down is { } down ? Task.FromException<SlskdServerState>(down) : Task.FromResult(new SlskdServerState(true, true, "Connected, LoggedIn"));

    public Task<SlskdSearch> StartSearchAsync(SlskdSearchRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Down is { } down)
        {
            throw down;
        }

        OnCall?.Invoke("start_search");
        var script = Searches.GetValueOrDefault(request.SearchText) ?? new SearchScript([], TimeSpan.FromSeconds(1));
        lock (_gate)
        {
            Started.Add(request.SearchText);
            if (script.StartError is { } error)
            {
                throw error;
            }

            if (_busyLeft < 0)
            {
                _busyLeft = script.BusyTimes;
            }

            if (_busyLeft > 0)
            {
                _busyLeft--;
                throw new SlskdBusyException("search.start");
            }

            _busyLeft = -1;
            StartedIds.Add(request.Id);
            _searches[request.Id] = (script, clock.GetUtcNow(), false);
        }

        return Task.FromResult(new SlskdSearch(request.Id, "InProgress", false, 0, 0));
    }

    public Task<SlskdSearch?> GetSearchAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GetSearchCalls++;
        OnCall?.Invoke("get_search");
        lock (_gate)
        {
            if (!_searches.TryGetValue(id, out var s))
            {
                return Task.FromResult<SlskdSearch?>(null);
            }

            var complete = s.Cancelled || clock.GetUtcNow() - s.At >= s.Script.CompleteAfter;
            return Task.FromResult<SlskdSearch?>(new SlskdSearch(id, complete ? "Completed, TimedOut" : "InProgress", complete,
                s.Script.Responses.Count, s.Script.Responses.Sum(r => r.Files.Count)));
        }
    }

    public Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OnCall?.Invoke("get_responses");
        lock (_gate)
        {
            var s = _searches[id];
            if (s.Script.ResponsesError is { } error)
            {
                throw error;
            }

            return Task.FromResult(s.Script.Responses);
        }
    }

    public Task CancelSearchAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            CancelledSearches.Add(id);
            if (_searches.TryGetValue(id, out var s))
            {
                _searches[id] = s with { Cancelled = true };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteSearchAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            DeletedSearches.Add(id);
            _searches.Remove(id);
        }

        return Task.CompletedTask;
    }

    public Task<SlskdEnqueueResult> EnqueueAsync(SlskdEnqueueRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OnCall?.Invoke("enqueue");
        var script = Peers[request.Username];
        lock (_gate)
        {
            Enqueued.Add(request);
            if (script.EnqueueError is { } error)
            {
                throw error;
            }

            if (script.Refuse)
            {
                return Task.FromResult(new SlskdEnqueueResult(null, ["File not shared."]));
            }

            var id = Guid.NewGuid();
            _transfers[id] = (request.Username, request, script, clock.GetUtcNow());
            return Task.FromResult(new SlskdEnqueueResult(id, []));
        }
    }

    public Task<SlskdTransfer?> GetTransferAsync(string username, Guid transferId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        GetTransferCalls++;
        OnCall?.Invoke("get_transfer");
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId, out var t))
            {
                return Task.FromResult<SlskdTransfer?>(null);
            }

            var step = t.Script.At(clock.GetUtcNow() - t.At);
            var transfer = new SlskdTransfer(transferId, username, t.Request.Filename, t.Request.Size, step.State, step.Bytes, step.Speed,
                null, 1, step.NextAttemptAt);
            if (transfer.IsSucceeded && t.Script.WriteFile)
            {
                var dir = Path.Combine(downloadsDir, t.Request.Destination.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, t.Script.SaveAs ?? SoulseekFetcher.LocalName(t.Request.Filename));
                if (!File.Exists(file))
                {
                    File.WriteAllBytes(file, [1, 2, 3]);
                }
            }

            return Task.FromResult<SlskdTransfer?>(transfer);
        }
    }

    public Task CancelTransferAsync(string username, Guid transferId, bool remove, CancellationToken ct)
    {
        lock (_gate)
        {
            CancelledTransfers.Add((username, transferId, remove));
            if (remove)
            {
                _transfers.Remove(transferId);
            }
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ retention calls (IRetentionSlskd)

    /// <summary>Upload events, newest first; GetEventsAsync pages over them.</summary>
    public List<SlskdUploadEvent> UploadEvents { get; } = [];

    /// <summary>Records of other event types that sit among the uploads (counted in the total, never returned as uploads).</summary>
    public int OtherEvents { get; set; }

    public List<SlskdUpload> Uploads { get; } = [];

    public List<SlskdShare> Shares { get; } = [];

    public SlskdShareState ShareState { get; set; } = new(false, false, true, false);

    public List<(int Offset, int Limit)> EventReads { get; } = [];

    public int RescanCalls { get; private set; }

    public Task<SlskdEventsPage> GetEventsAsync(int offset, int limit, CancellationToken ct)
    {
        if (Down is { } down)
        {
            return Task.FromException<SlskdEventsPage>(down);
        }

        lock (_gate)
        {
            EventReads.Add((offset, limit));
            var page = UploadEvents.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SlskdEventsPage(page, page.Count, page.Count == 0 ? null : page.Min(e => e.CompletedUtc), UploadEvents.Count + OtherEvents));
        }
    }

    public Task<IReadOnlyList<SlskdUpload>> GetUploadsAsync(CancellationToken ct) =>
        Down is { } down ? Task.FromException<IReadOnlyList<SlskdUpload>>(down) : Task.FromResult<IReadOnlyList<SlskdUpload>>(Uploads.ToList());

    public Task<IReadOnlyList<SlskdShare>> GetSharesAsync(CancellationToken ct) =>
        Down is { } down ? Task.FromException<IReadOnlyList<SlskdShare>>(down) : Task.FromResult<IReadOnlyList<SlskdShare>>(Shares.ToList());

    public Task<SlskdShareState> GetShareStateAsync(CancellationToken ct) =>
        Down is { } down ? Task.FromException<SlskdShareState>(down) : Task.FromResult(ShareState);

    public Task RescanSharesAsync(CancellationToken ct)
    {
        if (Down is { } down)
        {
            return Task.FromException(down);
        }

        RescanCalls++;
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ ready-made transfer behaviours

    public static TransferScript Succeeds(TimeSpan after, long size = 32_000_000, string? saveAs = null, bool writeFile = true) =>
        new(e => e >= after
            ? new TransferStep("Completed, Succeeded", size, 1_000_000)
            : new TransferStep("InProgress", Math.Max(1, (long)(size * (e / after))), 1_000_000), SaveAs: saveAs, WriteFile: writeFile);

    public static TransferScript QueuedRemotely() => new(_ => new TransferStep("Queued, Remotely", 0, 0));

    public static TransferScript IdleAt(long bytes) => new(_ => new TransferStep("InProgress", bytes, 0));

    public static TransferScript Slow(double bytesPerSecond) =>
        new(e => new TransferStep("InProgress", (long)(bytesPerSecond * e.TotalSeconds) + 1, bytesPerSecond));

    public static TransferScript Ends(string state, DateTimeOffset? nextAttemptAt = null) =>
        new(e => e >= TimeSpan.FromSeconds(4) ? new TransferStep(state, 1000, 0, nextAttemptAt) : new TransferStep("InProgress", 1000, 500));
}
