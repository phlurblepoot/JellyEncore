using System.Collections.Concurrent;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Fetch.Import;
using Encore.Fetch.Scoring;
using Encore.Slskd;
using Encore.Store;
using Encore.Tests.EndToEnd;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;

namespace Encore.Tests.Fetch;

/// <summary>One call of <see cref="FakeFetcher.FetchAsync"/>.</summary>
internal sealed class FetchCall(SoulseekJob job, SoulseekFetchHooks hooks, CancellationToken soft, CancellationToken stopping)
{
    public SoulseekJob Job { get; } = job;

    public SoulseekFetchHooks Hooks { get; } = hooks;

    public CancellationToken Soft { get; } = soft;

    public CancellationToken Stopping { get; } = stopping;

    /// <summary>Counted in <see cref="FakeFetcher.Active"/> (false while the verify hook imports).</summary>
    public bool Counted { get; set; }
}

/// <summary>
/// A scripted <see cref="ISoulseekFetcher"/>. It counts calls that are searching or downloading (not while the verify
/// hook imports), so <see cref="MaxActive"/> is the slot gauge.
/// </summary>
internal sealed class FakeFetcher : ISoulseekFetcher
{
    private readonly object _gate = new();
    private int _active;

    /// <summary>What a call does. The default downloads from one peer at once.</summary>
    public Func<FetchCall, Task<SoulseekFetchResult>> Behavior { get; set; } = c => DownloadAsync(c);

    public ConcurrentQueue<FetchCall> Calls { get; } = new();

    public int Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public int MaxActive { get; private set; }

    public async Task<SoulseekFetchResult> FetchAsync(SoulseekJob job, SoulseekFetchHooks? hooks, CancellationToken soft, CancellationToken stopping)
    {
        var call = new FetchCall(job, hooks ?? new SoulseekFetchHooks(), soft, stopping);
        Calls.Enqueue(call);
        Enter(call);
        try
        {
            await Task.Yield();
            return await Behavior(call).ConfigureAwait(false);
        }
        finally
        {
            Exit(call);
        }
    }

    public FetchCall? CallFor(string title) => Calls.FirstOrDefault(c => c.Job.Title == title);

    private void Enter(FetchCall call)
    {
        lock (_gate)
        {
            if (!call.Counted)
            {
                call.Counted = true;
                _active++;
                MaxActive = Math.Max(MaxActive, _active);
            }
        }
    }

    private void Exit(FetchCall call)
    {
        lock (_gate)
        {
            if (call.Counted)
            {
                call.Counted = false;
                _active--;
            }
        }
    }

    /// <summary>Searching: waits for <paramref name="gate"/>; a soft cancel returns Failed(cancelled), shutdown throws.</summary>
    public static async Task<SoulseekFetchResult?> SearchAsync(FetchCall call, Task gate)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Soft, call.Stopping);
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!call.Stopping.IsCancellationRequested)
        {
            return new SoulseekFetchResult.Failed(FetchFailure.Cancelled);
        }
    }

    /// <summary>
    /// Downloads like the real fetcher: for each peer, enqueue (the OnEnqueued hook), optionally wait for
    /// <paramref name="downloading"/> (only shutdown stops it), then the verify hook. Returns the first verified file,
    /// else Failed(all_peers_failed).
    /// </summary>
    public static async Task<SoulseekFetchResult> DownloadAsync(FetchCall call, int peers = 1, Task? downloading = null, FakeFetcher? owner = null)
    {
        for (var peerNo = 1; peerNo <= peers; peerNo++)
        {
            var file = Scored($"peer{peerNo}", call.Job);
            var transfer = Guid.NewGuid();
            var rowId = call.Hooks.OnEnqueued?.Invoke(new EnqueuedDownload(call.Job.JobId, peerNo, file, transfer, "q1"));
            if (downloading is not null)
            {
                await downloading.WaitAsync(call.Stopping).ConfigureAwait(false);
            }

            var downloaded = new DownloadedFile(call.Job.JobId, peerNo, file, transfer, $"/slskd/encore/{call.Job.JobId}/{call.Job.Title}.mp3",
                "q1", rowId, TimeSpan.FromSeconds(3));
            owner?.Exit(call);
            var ok = call.Hooks.VerifyAsync is null || await call.Hooks.VerifyAsync(downloaded, call.Stopping).ConfigureAwait(false);
            if (ok)
            {
                return new SoulseekFetchResult.Downloaded(downloaded);
            }

            owner?.Enter(call);   // a mismatch: the next peer downloads again
        }

        return new SoulseekFetchResult.Failed(FetchFailure.AllPeersFailed);
    }

    public static ScoredFile Scored(string peer, SoulseekJob job) =>
        new(peer, new SlskdFile($"@@music\\{job.Artist}\\{job.Title}.mp3", 9_000_000, 320, false, job.DurationS, 44100, null, "mp3"),
            AudioFormat.Mp3, 320, true, 0, 4.2);
}

/// <summary>A scripted <see cref="IImporter"/>: imports at once unless <see cref="Statuses"/> says otherwise.</summary>
internal sealed class FakeImporter(string radioDir) : IImporter
{
    public ConcurrentQueue<ImportRequest> Requests { get; } = new();

    /// <summary>Results to give, in order; empty means Imported.</summary>
    public ConcurrentQueue<(ImportStatus Status, string? Reason)> Statuses { get; } = new();

    /// <summary>Runs before each import (to look at the row, or to wait).</summary>
    public Func<ImportRequest, Task>? Before { get; set; }

    public int StagedFiles { get; set; }

    public int CleanCalls { get; private set; }

    public bool LeaveSource { get; set; }

    public async Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (Before is { } before)
        {
            await before(request).ConfigureAwait(false);
        }

        if (Statuses.TryDequeue(out var scripted) && scripted.Status != ImportStatus.Imported)
        {
            return new ImportResult(scripted.Status, scripted.Reason, "scripted", null, null, null, null, false, false);
        }

        var path = Path.Combine(radioDir, "soulseek", request.Artist, request.Title + ".mp3");
        var jellyfinPath = $"/music-radio/soulseek/{request.Artist}/{request.Title}.mp3";
        return new ImportResult(ImportStatus.Imported, null, null, path, jellyfinPath, null,
            new AudioFacts("mp3", 320, request.DurationS ?? 200, 44100, null, 9_000_000), false, LeaveSource);
    }

    public int CleanStaging()
    {
        CleanCalls++;
        return StagedFiles;
    }
}

/// <summary>A scripted <see cref="IJellyfinIngest"/>: ready at once with a new item id unless <see cref="Behavior"/> says otherwise.</summary>
internal sealed class FakeIngest : IJellyfinIngest
{
    public static readonly Guid LibraryId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public LibraryCheck Library { get; set; } = new(true, LibraryId, "Music", ["/music-radio"]);

    public ConcurrentQueue<(string JobId, string Path)> Ingested { get; } = new();

    public Func<string, string, CancellationToken, Task<IngestResult>> Behavior { get; set; } =
        (_, _, _) => Task.FromResult(new IngestResult(IngestOutcome.Ready, Guid.NewGuid(), TimeSpan.FromSeconds(5)));

    public Task<LibraryCheck> CheckLibraryAsync(CancellationToken ct = default) => Task.FromResult(Library);

    public Task<IngestResult> IngestAsync(string jobId, string jellyfinPath, CancellationToken ct = default)
    {
        Ingested.Enqueue((jobId, jellyfinPath));
        return Behavior(jobId, jellyfinPath, ct);
    }
}

/// <summary>An <see cref="IOwnedIndex"/> that records what is added.</summary>
internal sealed class FakeOwned : IOwnedIndex
{
    public bool IsLoaded { get; set; } = true;

    public ConcurrentQueue<OwnedEntry> Added { get; } = new();

    public OwnedEntry? Resolve(TrackKey key, int? durationS) => Added.LastOrDefault(e => e.Key == key);

    public TrackKey? KeyOf(Guid itemId) => Added.FirstOrDefault(e => e.ItemId == itemId)?.Key;

    public void Add(OwnedEntry entry) => Added.Enqueue(entry);

    public int Count => Added.Count;

    public DateTimeOffset? RefreshedUtc => null;
}

/// <summary>An <see cref="ISlskdClient"/> for the worker: server state and transfer removal only.</summary>
internal sealed class FakeSlskdState : ISlskdClient
{
    public SlskdServerState State { get; set; } = new(true, true, "Connected, LoggedIn");

    public Exception? Failure { get; set; }

    public ConcurrentQueue<(string User, Guid Id, bool Remove)> CancelledTransfers { get; } = new();

    public Task<SlskdServerState> GetServerStateAsync(CancellationToken ct) =>
        Failure is { } e ? Task.FromException<SlskdServerState>(e) : Task.FromResult(State);

    public Task CancelTransferAsync(string username, Guid transferId, bool remove, CancellationToken ct)
    {
        CancelledTransfers.Enqueue((username, transferId, remove));
        return Task.CompletedTask;
    }

    public Task<SlskdSearch> StartSearchAsync(SlskdSearchRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<SlskdSearch?> GetSearchAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task CancelSearchAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteSearchAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<SlskdEnqueueResult> EnqueueAsync(SlskdEnqueueRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<SlskdTransfer?> GetTransferAsync(string username, Guid transferId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>One <c>fetched_files</c> row as the tests read it.</summary>
internal sealed record FetchedRow(
    long Id, string JobId, string State, string? FailReason, string? Peer, string? Path, string? JellyfinItemId,
    string RequestedUtc, string? FetchedUtc, string? ImportedUtc, string? ReadyUtc, string RunId, string Origin);

/// <summary>
/// A <see cref="FetchWorker"/> with fakes around it, a real fetch store in a temp folder, and the real clock
/// (tick 20 ms). Dispose stops the worker and deletes the folder.
/// </summary>
internal sealed class WorkerHarness : IAsyncDisposable
{
    public WorkerHarness(FetchMode mode = FetchMode.Live, int concurrency = 2)
    {
        Root = Path.Combine(Path.GetTempPath(), "encore-t49-" + Guid.NewGuid().ToString("N"));
        RadioDir = Path.Combine(Root, "radio");
        DownloadsDir = Path.Combine(Root, "downloads");
        Directory.CreateDirectory(RadioDir);
        Directory.CreateDirectory(DownloadsDir);
        DbPath = Path.Combine(Root, "encore.db");
        EncoreStore.Open(DbPath, TimeProvider.System).Dispose();
        Store = FetchStore.Open(DbPath);
        Importer = new FakeImporter(RadioDir);
        Coordinator = new FetchCoordinator(mode, Owned, CoordinatorLog);
        Worker = new FetchWorker(Coordinator, Fetcher, Importer, Ingest, Owned, Jellyfin, Slskd, Store, FreeSpace,
            new FetchWorkerSettings(RadioDir, "/music-radio", DownloadsDir, concurrency) { Tick = TimeSpan.FromMilliseconds(20) },
            WorkerLog);
    }

    public string Root { get; }

    public string RadioDir { get; }

    public string DownloadsDir { get; }

    public string DbPath { get; }

    public FetchStore Store { get; }

    public FakeFetcher Fetcher { get; } = new();

    public FakeImporter Importer { get; }

    public FakeIngest Ingest { get; } = new();

    public FakeOwned Owned { get; } = new();

    public FakeJellyfin Jellyfin { get; } = new();

    public FakeSlskdState Slskd { get; } = new();

    public FakeFreeSpace FreeSpace { get; } = new(100_000_000_000);

    public ListLogger<FetchCoordinator> CoordinatorLog { get; } = new();

    public ListLogger<FetchWorker> WorkerLog { get; } = new();

    public FetchCoordinator Coordinator { get; }

    public FetchWorker Worker { get; }

    private bool _started;

    public async Task StartAsync(bool waitAvailable = true)
    {
        _started = true;
        await Worker.StartAsync(CancellationToken.None);
        await Worker.Reconciled.WaitAsync(TimeSpan.FromSeconds(10));
        if (waitAvailable)
        {
            await Wait.Until(() => Coordinator.Availability.Available, "fetching available");
        }
    }

    public async Task StopAsync()
    {
        if (_started)
        {
            _started = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Worker.StopAsync(cts.Token);
        }
    }

    public static Candidate Candidate(string artist, string title, int? durationS = 200) =>
        new(TrackKey.Create(artist, title)!.Value, artist, title, "Album", durationS, 42, 7, Guid.Parse("a74b1b7f-71a5-4011-9441-d0b5e4122711"),
            CandidateOrigin.DeezerRadio, 1);

    public static FetchRequest RequestFor(string pickId, string artist, string title, string runId = "run1", bool dryRun = false) =>
        new(pickId, runId, "session1", Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"), Candidate(artist, title), dryRun,
            DateTimeOffset.UtcNow + FetchLimits.JobDeadline);

    public bool Request(string pickId, string artist, string title, string runId = "run1") =>
        Coordinator.Request(RequestFor(pickId, artist, title, runId, Coordinator.Mode == FetchMode.DryRun));

    public FetchReadiness Readiness(string pickId) => Coordinator.GetReadiness(pickId);

    public Task Until(string pickId, Func<FetchReadiness, bool> condition, string what) =>
        Wait.Until(() => condition(Readiness(pickId)), what);

    public IReadOnlyList<FetchedRow> Rows()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ConnectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, job_id, state, fail_reason, peer, path, jellyfin_item_id, requested_utc, fetched_utc, imported_utc, ready_utc, run_id, origin
            FROM fetched_files ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        var rows = new List<FetchedRow>();
        while (r.Read())
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            rows.Add(new FetchedRow(r.GetInt64(0), r.GetString(1), r.GetString(2), S(3), S(4), S(5), S(6), r.GetString(7), S(8), S(9), S(10),
                r.GetString(11), r.GetString(12)));
        }

        return rows;
    }

    public string? RowState(string jobId) => Rows().LastOrDefault(r => r.JobId == jobId)?.State;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Worker.Dispose();
        Store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
