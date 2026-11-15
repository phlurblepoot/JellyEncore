using System.Collections.Concurrent;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch.Import;
using Encore.Jellyfin;
using Encore.Logging;
using Encore.Slskd;
using Encore.Store;
using Microsoft.Data.Sqlite;

namespace Encore.Fetch;

/// <summary>The folders and limits the fetch worker works with.</summary>
/// <param name="RadioDir">ENCORE_RADIO_DIR, Encore's view of music-radio.</param>
/// <param name="RadioJellyfinDir">ENCORE_RADIO_JELLYFIN_DIR, the same folder as Jellyfin sees it.</param>
/// <param name="SlskdDownloadsDir">ENCORE_SLSKD_DOWNLOADS_DIR, Encore's view of slskd's downloads.</param>
/// <param name="Concurrency">ENCORE_FETCH_CONCURRENCY: jobs in Searching plus Downloading, all users.</param>
public sealed record FetchWorkerSettings(string RadioDir, string RadioJellyfinDir, string SlskdDownloadsDir, int Concurrency)
{
    /// <summary>How often the worker sweeps deadlines and looks at its periodic checks.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>slskd job folders older than this with no active row are deleted at restart.</summary>
    public TimeSpan OrphanAge { get; init; } = TimeSpan.FromHours(1);

    public static FetchWorkerSettings From(EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.RadioDir, options.RadioJellyfinDir, options.SlskdDownloadsDir, options.FetchConcurrency);
    }
}

/// <summary>
/// Runs the fetch jobs the <see cref="FetchCoordinator"/> queues (v0.2 design sections 6, 8 and 10): at most
/// <see cref="FetchWorkerSettings.Concurrency"/> jobs in Searching plus Downloading; the pipeline Soulseek, import
/// (inside the fetcher's verify hook, so a mismatch moves on to the next peer), Jellyfin ingest, ready. It owns the
/// <c>fetched_files</c> rows: inserted at the first enqueue, then importing, imported, ready or failed. At start it
/// settles what a restart left behind (section 8.3), then runs the owned-index refresh, the slskd, library and free
/// space checks, and the deadline sweep. A job that throws fails as <c>internal</c>; the worker goes on.
/// </summary>
public sealed class FetchWorker : BackgroundService
{
    private const string ItemFields = "Path,DateCreated";

    private readonly FetchCoordinator _coordinator;
    private readonly ISoulseekFetcher _fetcher;
    private readonly IImporter _importer;
    private readonly IJellyfinIngest _ingest;
    private readonly IOwnedIndex _owned;
    private readonly IJellyfinClient _jellyfin;
    private readonly ISlskdClient _slskd;
    private readonly FetchStore _store;
    private readonly IFreeSpace _freeSpace;
    private readonly FetchWorkerSettings _settings;
    private readonly ILogger<FetchWorker> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private string? _lastSlskdState;

    public FetchWorker(
        FetchCoordinator coordinator, ISoulseekFetcher fetcher, IImporter importer, IJellyfinIngest ingest, IOwnedIndex owned,
        IJellyfinClient jellyfin, ISlskdClient slskd, FetchStore store, IFreeSpace freeSpace, FetchWorkerSettings settings,
        ILogger<FetchWorker> log, TimeProvider? time = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _slskd = slskd ?? throw new ArgumentNullException(nameof(slskd));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _freeSpace = freeSpace ?? throw new ArgumentNullException(nameof(freeSpace));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.Concurrency, 1);
        _slots = new SemaphoreSlim(settings.Concurrency, settings.Concurrency);
    }

    /// <summary>Set once restart reconciliation finished (tests wait on it).</summary>
    public Task Reconciled => _reconciled.Task;

    private readonly TaskCompletionSource _reconciled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool Live => _coordinator.Mode == FetchMode.Live;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            try
            {
                Reconcile(stoppingToken);
                _coordinator.SeedWindow(_store.FetchTimesSince(_time.GetUtcNow() - TimeSpan.FromHours(1)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.WorkerCrashed("fetch", $"restart reconciliation: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _reconciled.TrySetResult();
            }

            var owned = _owned is OwnedIndex index ? index.RunAsync(stoppingToken) : Task.CompletedTask;
            await Task.WhenAll(owned, ChecksAsync(stoppingToken), DispatchAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _coordinator.CancelAll();
            try
            {
                await Task.WhenAll(_running.Keys.ToArray()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Each job logs its own end.
            }
        }
    }

    // ================================================================ dispatch

    private async Task DispatchAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var job in _coordinator.Queue.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                if (job.IsTerminal)
                {
                    continue;
                }

                await _slots.WaitAsync(stopping).ConfigureAwait(false);
                if (!_coordinator.TryStart(job))
                {
                    _slots.Release();
                    continue;
                }

                Track(RunJobAsync(job, new Slot(_slots), stopping));
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private void Track(Task task)
    {
        _running[task] = 0;
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>One slot of the Searching plus Downloading limit, released while the job imports or ingests.</summary>
    private sealed class Slot(SemaphoreSlim slots)
    {
        private bool _held = true;

        public void Release()
        {
            if (_held)
            {
                _held = false;
                slots.Release();
            }
        }

        public async Task AcquireAsync(CancellationToken ct)
        {
            if (!_held)
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                _held = true;
            }
        }
    }

    private sealed class ImportFailedException(string reason, string? error) : Exception($"{reason}: {error}")
    {
        public string Reason { get; } = reason;
    }

    private async Task RunJobAsync(FetchJob job, Slot slot, CancellationToken stopping)
    {
        await Task.Yield();
        var stage = "search";
        try
        {
            var c = job.Candidate;
            var soulseek = new SoulseekJob(job.JobId, job.Key.Value, c.Artist, c.Title, c.Album, c.DurationS, job.DryRun, job.Deadline);
            var hooks = new SoulseekFetchHooks(
                OnEnqueued: e => OnEnqueued(job, e),
                VerifyAsync: (file, ct) => VerifyAsync(job, slot, file, ct));
            var result = await _fetcher.FetchAsync(soulseek, hooks, job.Soft.Token, stopping).ConfigureAwait(false);
            slot.Release();

            switch (result)
            {
                case SoulseekFetchResult.WouldDownload:
                    _coordinator.MarkWouldDownload(job);
                    return;
                case SoulseekFetchResult.Failed failed:
                    FailJob(job, failed.Reason, failRow: true);
                    return;
                case SoulseekFetchResult.Downloaded when job.Import?.JellyfinPath is { } jellyfinPath:
                    stage = "ingest";
                    await IngestAsync(job, jellyfinPath, stopping).ConfigureAwait(false);
                    return;
                default:
                    throw new InvalidOperationException("the fetcher returned a file that was never imported");
            }
        }
        catch (ImportFailedException e)
        {
            _coordinator.NoteError($"import: {e.Reason}");
            FailJob(job, FetchReasons.ImportFailed, failRow: true);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutdown: rows stay as they are; the next start settles them (restart reconciliation).
            _coordinator.Stopped(job);
        }
        catch (Exception e)
        {
            var error = $"{e.GetType().Name}: {e.Message}";
            _log.FetchJobError(job.JobId, stage, error);
            _coordinator.NoteError(error);
            FailJob(job, FetchReasons.Internal, failRow: stage != "ingest");
        }
        finally
        {
            slot.Release();
        }
    }

    private long? OnEnqueued(FetchJob job, EnqueuedDownload e)
    {
        _coordinator.MarkDownloading(job);
        job.Peer = e.File.Username;
        var now = _time.GetUtcNow();
        var c = job.Candidate;
        TryStore(job, job.RowId is null ? "insert" : "update", () =>
        {
            if (job.RowId is { } id)
            {
                _store.UpdateFetched(id, FetchState.Downloading,
                    new FetchedUpdate(Peer: e.File.Username, RemotePath: e.File.File.Filename, TransferId: e.TransferId.ToString()));
            }
            else
            {
                job.RowId = _store.InsertFetched(new NewFetchedFile(
                    job.JobId, job.Key.Value, c.Artist, c.Title, c.Origin.ToWire(), SoulseekFetcher.Source,
                    job.RunId, job.SessionId, job.UserId, now,
                    Album: c.Album, DeezerTrackId: c.DeezerTrackId, MbArtistId: c.ArtistMbid?.ToString("D"),
                    DurationSeconds: c.DurationS, Peer: e.File.Username, RemotePath: e.File.File.Filename,
                    TransferId: e.TransferId.ToString()));
            }
        });
        return job.RowId;
    }

    /// <summary>The fetcher's verify hook: import the file. Imported keeps it; a mismatch tries the next peer; a failure ends the job.</summary>
    private async Task<bool> VerifyAsync(FetchJob job, Slot slot, DownloadedFile file, CancellationToken ct)
    {
        slot.Release();
        _coordinator.MarkStage(job, FetchStage.Importing);
        UpdateRow(job, FetchState.Importing, new FetchedUpdate(FetchedUtc: _time.GetUtcNow(), SizeBytes: file.File.File.Size));

        var c = job.Candidate;
        var result = await _importer.ImportAsync(
            new ImportRequest(job.JobId, file.LocalPath, c.Artist, c.Title, c.Album, c.DurationS, c.DeezerTrackId, c.ArtistMbid), ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case ImportStatus.Imported:
                job.Import = result;
                if (result.SourceLeftBehind)
                {
                    _coordinator.NoteSourceLeftBehind();
                }

                var a = result.Audio;
                UpdateRow(job, FetchState.Imported, new FetchedUpdate(
                    Format: a?.Format, BitrateKbps: a?.Kbps, SampleRate: a?.SampleRate, BitDepth: a?.BitDepth, SizeBytes: a?.SizeBytes,
                    Path: result.Path, JellyfinPath: result.JellyfinPath, ImportedUtc: _time.GetUtcNow()));
                return true;

            case ImportStatus.Mismatch:
                await slot.AcquireAsync(ct).ConfigureAwait(false);
                _coordinator.MarkStage(job, FetchStage.Downloading);
                return false;

            default:
                if (result.Reason == ImportReason.DiskLow)
                {
                    _coordinator.SetFreeBytes(_freeSpace.FreeBytes(_settings.RadioDir));
                }

                throw new ImportFailedException(result.Reason ?? ImportReason.Io, result.Error);
        }
    }

    private async Task IngestAsync(FetchJob job, string jellyfinPath, CancellationToken stopping)
    {
        _coordinator.MarkStage(job, FetchStage.Ingesting);
        IngestResult result;
        try
        {
            result = await _ingest.IngestAsync(job.JobId, jellyfinPath, stopping).ConfigureAwait(false);
        }
        catch (JellyfinException e)
        {
            // Media/Updated or the library check failed. The file is in music-radio and the row stays imported:
            // the 30 min owned-index refresh finds the song later.
            _log.FetchIngestFailed(job.JobId, jellyfinPath, e.Message);
            _coordinator.NoteError($"ingest: {e.Message}");
            FailJob(job, FetchReasons.IngestTimeout, failRow: false);
            return;
        }

        switch (result.Outcome)
        {
            case IngestOutcome.Ready when result.ItemId is { } itemId:
                await AddOwnedAsync(itemId, jellyfinPath, job.Candidate, stopping).ConfigureAwait(false);
                UpdateRow(job, FetchState.Ready, new FetchedUpdate(JellyfinItemId: itemId.ToString("N"), ReadyUtc: _time.GetUtcNow()));
                _coordinator.Complete(job, itemId);
                break;
            case IngestOutcome.NotInLibrary:
                _coordinator.SetLibrary(false, null);
                FailJob(job, FetchReasons.IngestTimeout, failRow: false);
                break;
            default:
                FailJob(job, FetchReasons.IngestTimeout, failRow: false);
                break;
        }
    }

    /// <summary>Adds the ingested song to the owned index from its Jellyfin item; without it, from the candidate (or not at all).</summary>
    private async Task AddOwnedAsync(Guid itemId, string jellyfinPath, Candidate? candidate, CancellationToken ct)
    {
        OwnedEntry? entry = null;
        try
        {
            var items = await _jellyfin.GetItemsByIdsAsync([itemId], ItemFields, ct).ConfigureAwait(false);
            if (items.FirstOrDefault(i => i.Id == itemId) is { } item)
            {
                entry = OwnedIndex.CreateEntry(item, _settings.RadioJellyfinDir);
            }
        }
        catch (JellyfinException)
        {
            // Fall back to the candidate below.
        }

        if (entry is null && candidate is not null)
        {
            entry = new OwnedEntry(itemId, candidate.Key, candidate.Title, null, candidate.Artist, candidate.DurationS,
                jellyfinPath, _time.GetUtcNow(), true);
        }

        if (entry is not null)
        {
            _owned.Add(entry);
        }
    }

    private void FailJob(FetchJob job, string reason, bool failRow)
    {
        var used = _coordinator.Fail(job, reason);
        if (failRow)
        {
            UpdateRow(job, FetchState.Failed, new FetchedUpdate(FailReason: used));
        }
    }

    private void UpdateRow(FetchJob job, string state, FetchedUpdate fields)
    {
        if (job.RowId is { } id)
        {
            TryStore(job, state, () => _store.UpdateFetched(id, state, fields));
        }
    }

    private void TryStore(FetchJob job, string operation, Action write)
    {
        try
        {
            write();
        }
        catch (Exception e) when (e is StoreException or SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            _log.FetchStoreFailed(job.JobId, operation, e.GetType().Name);
        }
    }

    // ================================================================ periodic checks

    private async Task ChecksAsync(CancellationToken stopping)
    {
        var nextSlskd = DateTimeOffset.MinValue;
        var nextLibrary = DateTimeOffset.MinValue;
        var nextFree = DateTimeOffset.MinValue;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                // One bad check must not end this loop: it also runs the deadline sweep for the life of the process.
                try
                {
                    var now = _time.GetUtcNow();
                    if (now >= nextSlskd)
                    {
                        nextSlskd = now + FetchLimits.SlskdStateEvery;
                        await CheckSlskdAsync(stopping).ConfigureAwait(false);
                    }

                    if (Live && now >= nextLibrary)
                    {
                        nextLibrary = now + FetchLimits.LibraryCheckEvery;
                        await CheckLibraryAsync(stopping).ConfigureAwait(false);
                    }

                    if (Live && now >= nextFree)
                    {
                        nextFree = now + FetchLimits.FreeSpaceEvery;
                        _coordinator.SetFreeBytes(_freeSpace.FreeBytes(_settings.RadioDir));
                    }

                    _coordinator.Sweep();
                }
                catch (Exception e) when (e is not OperationCanceledException || !stopping.IsCancellationRequested)
                {
                    _log.WorkerCrashed("fetch", $"periodic checks: {e.GetType().Name}");
                    _coordinator.NoteError($"periodic checks: {e.GetType().Name}");
                }

                await Task.Delay(_settings.Tick, _time, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task CheckSlskdAsync(CancellationToken stopping)
    {
        bool reachable, loggedIn, keyRejected = false;
        string state;
        string? error = null;
        try
        {
            var s = await _slskd.GetServerStateAsync(stopping).ConfigureAwait(false);
            reachable = true;
            loggedIn = s.IsLoggedIn;
            state = s.State;
        }
        catch (Exception e) when (e is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            // Any failure means slskd cannot be used now; only slskd's own messages (which never hold the key or URL) are kept.
            loggedIn = false;
            keyRejected = e is SlskdAuthException;
            reachable = keyRejected;   // slskd answered, it just refused this key
            state = keyRejected ? "key_rejected" : "unreachable";
            error = e is SlskdException ? e.Message : e.GetType().Name;
        }

        _coordinator.SetSlskdState(reachable, loggedIn, error, keyRejected);
        var summary = $"{reachable}|{loggedIn}|{state}";
        if (summary != _lastSlskdState)
        {
            _lastSlskdState = summary;
            _log.SlskdState(reachable, loggedIn, state);
        }
    }

    private async Task CheckLibraryAsync(CancellationToken stopping)
    {
        try
        {
            var check = await _ingest.CheckLibraryAsync(stopping).ConfigureAwait(false);
            _coordinator.SetLibrary(check.InLibrary, check.LibraryName);
        }
        catch (JellyfinException e)
        {
            // Keep the last answer; the next check (or the next ingest) tries again.
            _coordinator.NoteError($"library check: {e.Message}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            _coordinator.NoteError($"library check: {e.GetType().Name}");
        }
    }

    // ================================================================ restart reconciliation (design 8.3)

    private void Reconcile(CancellationToken stopping)
    {
        var staged = _importer.CleanStaging();
        var failed = 0;
        var reingested = 0;
        var jobsDir = Path.Combine(_settings.SlskdDownloadsDir, "encore");
        var now = _time.GetUtcNow();

        foreach (var row in _store.ActiveForRestart())
        {
            if (row.State is FetchState.Downloading or FetchState.Importing)
            {
                if (row.Peer is { } peer && Guid.TryParse(row.TransferId, out var transferId))
                {
                    RemoveTransfer(peer, transferId);
                }

                TryDeleteDir(Path.Combine(jobsDir, row.JobId));
                _store.UpdateFetched(row.Id, FetchState.Failed, new FetchedUpdate(FailReason: FetchReasons.Restart));
                failed++;
            }
            else if (row.State == FetchState.Imported)
            {
                var jellyfinPath = row.JellyfinPath ?? JellyfinPathOf(row.Path);
                if (row.Path is { } path && File.Exists(path) && jellyfinPath is not null)
                {
                    reingested++;
                    if (TrackKey.TryParse(row.TrackKey, out var pendingKey))
                    {
                        _coordinator.NoteImportedPending(pendingKey);
                    }

                    Track(ReingestAsync(row, jellyfinPath, stopping));
                }
                else
                {
                    _store.UpdateFetched(row.Id, FetchState.Failed, new FetchedUpdate(FailReason: FetchReasons.FileMissing));
                    failed++;
                }
            }
        }

        var orphans = 0;
        if (Directory.Exists(jobsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(jobsDir))
            {
                try
                {
                    if (now - new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero) > _settings.OrphanAge && TryDeleteDir(dir))
                    {
                        orphans++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        _log.ImportCleanup(staged, failed, reingested, orphans);
    }

    /// <summary>An imported row from before the restart: report it again and wait, with no pick. Found: ready and owned.</summary>
    private async Task ReingestAsync(ActiveFetch row, string jellyfinPath, CancellationToken stopping)
    {
        await Task.Yield();
        try
        {
            var result = await _ingest.IngestAsync(row.JobId, jellyfinPath, stopping).ConfigureAwait(false);
            if (result.Outcome == IngestOutcome.Ready && result.ItemId is { } itemId)
            {
                await AddOwnedAsync(itemId, jellyfinPath, null, stopping).ConfigureAwait(false);
                _store.UpdateFetched(row.Id, FetchState.Ready, new FetchedUpdate(JellyfinItemId: itemId.ToString("N"), ReadyUtc: _time.GetUtcNow()));
                if (TrackKey.TryParse(row.TrackKey, out var readyKey))
                {
                    _coordinator.ClearImportedPending(readyKey);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            // The row stays imported; the owned-index refresh finds the song later.
            _log.FetchIngestFailed(row.JobId, jellyfinPath, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The Jellyfin path of a file under the radio dir: the Jellyfin dir plus the relative path, always with '/'.</summary>
    private string? JellyfinPathOf(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(_settings.RadioDir, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return _settings.RadioJellyfinDir.TrimEnd('/') + "/" + relative.Replace('\\', '/');
    }

    private void RemoveTransfer(string peer, Guid transferId)
    {
        try
        {
            using var cts = new CancellationTokenSource(FetchLimits.CleanupTimeout);
            _slskd.CancelTransferAsync(peer, transferId, remove: true, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is SlskdException or HttpRequestException or OperationCanceledException)
        {
            // Best effort.
        }
    }

    private static bool TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }
}
