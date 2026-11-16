using System.Security.Cryptography;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Fetch.Import;
using Encore.Jellyfin;
using Encore.Store;

namespace Encore.Retention;

/// <summary>How one promotion call ended.</summary>
public enum PromotionOutcome
{
    /// <summary>The song is in the library and every step is done.</summary>
    Done,

    /// <summary>The library already has a file with the target name: no promotion, the row stays in music-radio as kept (8580).</summary>
    Duplicate,

    /// <summary>A guard holds promotions back (Lidarr defaults or tags, disk low, library not writable; 8590). No attempt is counted.</summary>
    Paused,

    /// <summary>The song is being uploaded on Soulseek right now: tried again next pass. No attempt is counted.</summary>
    Deferred,

    /// <summary>A step failed (8530); the attempt is counted and the step is kept for the next pass.</summary>
    Failed,

    /// <summary>Five attempts failed (8540): the promotion is no longer tried automatically.</summary>
    Stalled,

    /// <summary>The row cannot be promoted (gone, not ready, no Jellyfin item or path).</summary>
    NotEligible,
}

/// <summary>The result of one promotion call. <see cref="Step"/> is where it stopped.</summary>
public sealed record PromotionResult(long RowId, PromotionOutcome Outcome, string? Step = null, string? Reason = null);

/// <summary>The guards a pass checks before its promotions (v04 §7.2 copied, §7.5).</summary>
public sealed record PromotionGuard(bool Ok, string? Reason, string? Detail, LidarrGuardResult Lidarr, long? LibraryFreeBytes);

/// <summary>Poll intervals and waits of the promoter; the defaults are the fixed v0.4 limits (<see cref="RetentionLimits"/>).</summary>
public sealed record PromoterTimings
{
    public TimeSpan OldGonePoll { get; init; } = RetentionLimits.OldGonePoll;

    public TimeSpan OldGoneWait { get; init; } = RetentionLimits.OldGoneWait;

    public TimeSpan LidarrPoll { get; init; } = RetentionLimits.OldGonePoll;

    public TimeSpan LidarrWait { get; init; } = RetentionLimits.LidarrCommandWait;

    public TimeSpan IngestPoll { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan IngestWait { get; init; } = RetentionLimits.IngestWait;
}

/// <summary>
/// Test seams. <see cref="Checkpoint"/> is called at named points (<c>step:{name}</c> after a step was persisted, <c>staged_written</c>
/// after the copy was written and before it is checked, <c>playlist_added</c> after an Add and before it is marked). An
/// <see cref="OperationCanceledException"/> thrown there escapes like a process kill; anything else counts as a step failure.
/// </summary>
public sealed record PromoterHooks(Action<string>? Checkpoint = null);

/// <summary>
/// Promotes kept songs from music-radio into the main library, live only (v04 §7). One song at a time, through the persisted steps
/// <c>snapshot</c>, <c>copied</c>, <c>source_removed</c>, <c>old_gone</c>, <c>placed</c>, <c>ingested</c>, <c>verified</c>, <c>playlists</c>,
/// <c>done</c> (<c>promotions.step</c> holds the step to run next, so a restart resumes there). The order is Jellyfin's carry-over
/// order (§7.3): the old item must be gone before the new file is scanned, so user data parked under the old item is re-attached on the
/// new item's first refresh. The radio copy is removed only after a byte-identical staged copy (size and SHA-256) exists (invariant 6);
/// in the library only new files are created (invariant 5). A failed step counts an attempt (8530); after 5 the row is stalled (8540).
/// </summary>
public sealed class Promoter : IDisposable
{
    private const int RecentLimit = 50;
    private const string ItemFields = "Path,DateCreated";

    private readonly FetchStore _fetch;
    private readonly RetentionStore _store;
    private readonly RequestStore? _requests;
    private readonly IJellyfinClient _jellyfin;
    private readonly IRetentionJellyfin _retention;
    private readonly IOwnedIndex _owned;
    private readonly LidarrGuard _lidarr;
    private readonly IFreeSpace _freeSpace;
    private readonly ISharesDirty? _shares;
    private readonly IUploadCheck? _uploads;
    private readonly PromotionPaths _paths;
    private readonly EncoreOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<Promoter> _log;
    private readonly PromoterTimings _timings;
    private readonly PromoterHooks _hooks;
    private readonly SemaphoreSlim _one = new(1, 1);

    public Promoter(
        FetchStore fetch,
        RetentionStore store,
        RequestStore? requests,
        IJellyfinClient jellyfin,
        IRetentionJellyfin retention,
        IOwnedIndex owned,
        LidarrGuard lidarr,
        IFreeSpace freeSpace,
        ISharesDirty? shares,
        IUploadCheck? uploads,
        EncoreOptions options,
        TimeProvider time,
        ILogger<Promoter> log,
        PromoterTimings? timings = null,
        PromoterHooks? hooks = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _requests = requests;
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _owned = owned ?? throw new ArgumentNullException(nameof(owned));
        _lidarr = lidarr ?? throw new ArgumentNullException(nameof(lidarr));
        _freeSpace = freeSpace ?? throw new ArgumentNullException(nameof(freeSpace));
        _shares = shares;
        _uploads = uploads;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timings = timings ?? new PromoterTimings();
        _hooks = hooks ?? new PromoterHooks();
        _paths = PromotionPaths.From(options);
    }

    public PromotionPaths Paths => _paths;

    /// <summary>The newest guard check, or null before the first (for /health).</summary>
    public PromotionGuard? LastGuard { get; private set; }

    /// <summary>
    /// Checks every guard once (Lidarr defaults and tags, library writable, free space) and logs 8590 when paused. The worker calls it
    /// once per pass before any promotion.
    /// </summary>
    public async Task<PromotionGuard> CheckGuardsAsync(CancellationToken ct = default)
    {
        var lidarr = await _lidarr.CheckAsync(ct).ConfigureAwait(false);
        var free = _freeSpace.FreeBytes(_paths.LibraryDir);
        PromotionGuard guard;
        if (!lidarr.Ok)
        {
            guard = new PromotionGuard(false, lidarr.Reason, lidarr.Detail, lidarr, free);
        }
        else if (LibraryProblem() is { } problem)
        {
            guard = new PromotionGuard(false, PromotionPause.LibraryNotWritable, problem, lidarr, free);
        }
        else if (free is long f && f < RetentionLimits.FreeSpaceFloorBytes)
        {
            guard = new PromotionGuard(false, PromotionPause.DiskLow, $"{f} bytes free", lidarr, free);
        }
        else
        {
            guard = new PromotionGuard(true, null, null, lidarr, free);
        }

        if (!guard.Ok)
        {
            _log.PromotePaused(guard.Reason!, guard.Detail);
        }

        LastGuard = guard;
        return guard;
    }

    /// <summary>
    /// One pass's promotions: the guards once, then up to <paramref name="max"/> rows one after another (promotions already in progress
    /// first). Stalled rows, duplicates and rows that are not eligible do not use up the budget. A pause stops the batch.
    /// </summary>
    public async Task<IReadOnlyList<PromotionResult>> RunPassAsync(IReadOnlyList<long> rowIds, SignalsSnapshot signals, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        ArgumentNullException.ThrowIfNull(signals);
        var results = new List<PromotionResult>();
        if (max <= 0 || rowIds.Count == 0 || _options.RetentionMode != RetentionMode.Live)
        {
            // Invariant 2: only live moves files (the worker checks too; this keeps the promoter safe on its own, like the deleter).
            return results;
        }

        var guard = await CheckGuardsAsync(ct).ConfigureAwait(false);
        if (!guard.Ok)
        {
            return rowIds.Select(id => new PromotionResult(id, PromotionOutcome.Paused, null, guard.Reason)).ToList();
        }

        var activeRows = _store.ActivePromotions();
        var active = activeRows.Select(p => p.FetchedFileId).ToHashSet();
        var pastSnapshot = activeRows.Where(p => p.Step != PromotionStep.Snapshot).Select(p => p.FetchedFileId).ToHashSet();
        var ordered = rowIds.Distinct().OrderBy(id => active.Contains(id) ? 0 : 1).ToList();
        var used = 0;
        foreach (var id in ordered)
        {
            if (used >= max)
            {
                break;
            }

            if (!signals.PlaylistsComplete && !pastSnapshot.Contains(id))
            {
                // The snapshot records the song's playlist places from these signals; with a failed playlist read it would miss some and
                // the song would drop out of them. Promotions past the snapshot need no signals and go on.
                results.Add(new PromotionResult(id, PromotionOutcome.Deferred, null, "signals_incomplete"));
                continue;
            }

            var memberships = _fetch.GetTracked(id) is { JellyfinItemId: { } item } && Guid.TryParse(item, out var itemId)
                ? signals.MembershipsOf(itemId)
                : [];
            var result = await PromoteAsync(id, memberships, ct).ConfigureAwait(false);
            results.Add(result);
            if (result.Outcome == PromotionOutcome.Paused)
            {
                break;
            }

            if (result.Outcome is PromotionOutcome.Done or PromotionOutcome.Failed or PromotionOutcome.Deferred)
            {
                used++;
            }
        }

        return results;
    }

    /// <summary>
    /// Starts or resumes the promotion of one row. <paramref name="memberships"/> are the counted playlists that hold the old item
    /// (<see cref="SignalsSnapshot.MembershipsOf"/>; SmartLists and ignored playlists are never in it); they are recorded only at step
    /// <c>snapshot</c>. Only one promotion runs at a time.
    /// </summary>
    public async Task<PromotionResult> PromoteAsync(long rowId, IReadOnlyList<PlaylistMembership> memberships, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        if (_options.RetentionMode != RetentionMode.Live)
        {
            return new PromotionResult(rowId, PromotionOutcome.NotEligible, null, "not_live");
        }

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunAsync(rowId, memberships, ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose() => _one.Dispose();

    // ---- the step machine ----

    private abstract record StepResult;

    private sealed record Next(string Step, PromotionUpdate? Fields = null) : StepResult;

    private sealed record Finished : StepResult;

    private sealed record Pause(string Reason, string? Detail) : StepResult;

    private sealed record Defer(string Reason) : StepResult;

    private sealed record Fail(string Error) : StepResult;

    /// <summary>What one run knows besides the stored row.</summary>
    private sealed class Run(TrackedFile row, IReadOnlyList<PlaylistMembership> memberships)
    {
        public TrackedFile Row { get; } = row;

        public IReadOnlyList<PlaylistMembership> Memberships { get; } = memberships;

        /// <summary>The previous step ran in this call (so a Media/Updated it sent need not be repeated).</summary>
        public string? CameFrom { get; set; }
    }

    private async Task<PromotionResult> RunAsync(long rowId, IReadOnlyList<PlaylistMembership> memberships, CancellationToken ct)
    {
        var row = _fetch.GetTracked(rowId);
        var promo = _store.GetPromotion(rowId);
        if (row is null)
        {
            return new PromotionResult(rowId, PromotionOutcome.NotEligible, promo?.Step, "row_missing");
        }

        if (promo is { Step: not PromotionStep.Done } && row.State == FetchState.Promoted)
        {
            // A restart between the row's mark and the step's mark: the done work is idempotent, finish it.
            return await FinishAsync(new Run(row, memberships), promo, ct).ConfigureAwait(false);
        }

        if (promo is { Step: PromotionStep.Done })
        {
            return new PromotionResult(rowId, PromotionOutcome.Done, PromotionStep.Done);
        }

        if (row.State != FetchState.Ready)
        {
            return new PromotionResult(rowId, PromotionOutcome.NotEligible, promo?.Step, "not_ready");
        }

        if (promo is null)
        {
            var start = Start(row);
            if (start is not null)
            {
                return start;
            }

            promo = _store.GetPromotion(rowId)!;
            _log.PromoteStep(rowId, promo.Step, promo.TargetPath);
        }

        if (promo.Stalled)
        {
            return new PromotionResult(rowId, PromotionOutcome.Stalled, promo.Step, promo.LastError);
        }

        var run = new Run(row, memberships);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            StepResult result;
            try
            {
                result = await StepAsync(run, promo, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                result = new Fail($"{e.GetType().Name}: {e.Message}");
            }

            switch (result)
            {
                case Finished:
                    return await FinishAsync(run, promo, ct).ConfigureAwait(false);
                case Next next:
                    _store.UpdatePromotion(rowId, next.Step, _time.GetUtcNow(), next.Fields);
                    run.CameFrom = promo.Step;
                    promo = _store.GetPromotion(rowId)!;
                    _log.PromoteStep(rowId, promo.Step, promo.TargetPath);
                    _hooks.Checkpoint?.Invoke("step:" + promo.Step);
                    break;
                case Pause pause:
                    _log.PromotePaused(pause.Reason, pause.Detail);
                    return new PromotionResult(rowId, PromotionOutcome.Paused, promo.Step, pause.Reason);
                case Defer defer:
                    return new PromotionResult(rowId, PromotionOutcome.Deferred, promo.Step, defer.Reason);
                case Fail fail:
                    return Failed(promo, fail.Error);
            }
        }
    }

    /// <summary>Plans the target and writes the promotions row; a non-null result ends the call (duplicate or not eligible).</summary>
    private PromotionResult? Start(TrackedFile row)
    {
        if (string.IsNullOrEmpty(row.Path) || !Guid.TryParse(row.JellyfinItemId, out var oldItem))
        {
            return new PromotionResult(row.Id, PromotionOutcome.NotEligible, null, "no_item_or_path");
        }

        if (!File.Exists(row.Path))
        {
            return new PromotionResult(row.Id, PromotionOutcome.NotEligible, null, "radio_file_missing");
        }

        var target = _paths.Plan(row.Path, _fetch.DiscNoOf(row.Id), row.Title);
        if (target is null)
        {
            return new PromotionResult(row.Id, PromotionOutcome.NotEligible, null, "path_not_in_radio_layout");
        }

        if (target.Collision is { } existing)
        {
            _log.PromoteDuplicate(row.Id, existing);
            return new PromotionResult(row.Id, PromotionOutcome.Duplicate, null, "duplicate_in_library");
        }

        _store.StartPromotion(new NewPromotion(row.Id, oldItem.ToString("N"), _time.GetUtcNow(), target.Path, target.JellyfinPath));
        return null;
    }

    private PromotionResult Failed(PromotionRow promo, string error)
    {
        var attempts = promo.Attempts + 1;
        var stalled = attempts >= RetentionLimits.MaxPromotionAttempts;
        _store.UpdatePromotion(promo.FetchedFileId, promo.Step, _time.GetUtcNow(), new PromotionUpdate(Attempts: attempts, LastError: Cut(error), Stalled: stalled ? true : null));
        _log.PromoteFailed(promo.FetchedFileId, promo.Step, attempts, error);
        if (stalled)
        {
            _log.PromoteStalled(promo.FetchedFileId, promo.Step);
            return new PromotionResult(promo.FetchedFileId, PromotionOutcome.Stalled, promo.Step, error);
        }

        return new PromotionResult(promo.FetchedFileId, PromotionOutcome.Failed, promo.Step, error);
    }

    private Task<StepResult> StepAsync(Run run, PromotionRow promo, CancellationToken ct) => promo.Step switch
    {
        PromotionStep.Snapshot => SnapshotAsync(run, promo, ct),
        PromotionStep.Copied => CopyAsync(run, promo, ct),
        PromotionStep.SourceRemoved => RemoveSourceAsync(run, promo, ct),
        PromotionStep.OldGone => WaitOldGoneAsync(run, promo, ct),
        PromotionStep.Placed => PlaceAsync(run, promo, ct),
        PromotionStep.Ingested => WaitIngestAsync(run, promo, ct),
        PromotionStep.Verified => VerifyAsync(run, promo, ct),
        PromotionStep.Playlists => ReaddAsync(run, promo, ct),
        _ => Task.FromResult<StepResult>(new Fail($"unknown step {promo.Step}")),
    };

    // ---- snapshot: playlist entries and per-user data of the old item ----

    private async Task<StepResult> SnapshotAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        _store.AddEntries(promo.FetchedFileId, run.Memberships
            .Select(m => new NewPromotionEntry(m.PlaylistId.ToString("N"), m.CandidateUsers.Select(u => u.ToString("N")).ToList(), m.Index))
            .ToList());

        var old = Guid.Parse(promo.OldItemId);
        var users = await _jellyfin.GetUsersAsync(ct).ConfigureAwait(false);
        foreach (var user in users)
        {
            if (await _retention.GetUserItemDataAsync(old, user.Id, ct).ConfigureAwait(false) is { } data && (data.IsFavorite || data.PlayCount > 0))
            {
                _store.SaveUserData(promo.FetchedFileId, new PromotionUserData(user.Id.ToString("N"), data.IsFavorite, data.PlayCount));
            }
        }

        return new Next(PromotionStep.Copied);
    }

    // ---- copied: a verified copy in the library's hidden staging folder ----

    private async Task<StepResult> CopyAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        var source = run.Row.Path!;
        if (!File.Exists(source))
        {
            return new Fail("radio_file_missing");
        }

        var size = new FileInfo(source).Length;
        if (_freeSpace.FreeBytes(_paths.LibraryDir) is long free && free - size < RetentionLimits.FreeSpaceFloorBytes)
        {
            return new Pause(PromotionPause.DiskLow, $"{free} bytes free, file {size} bytes");
        }

        var staged = promo.StagedPath ?? _paths.StagedPath(promo.FetchedFileId, Extension(source));
        CreateFolder(Path.GetDirectoryName(staged)!);
        string sourceHash;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        await using (var output = NewFile(staged))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            sourceHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        _hooks.Checkpoint?.Invoke("staged_written");
        var stagedSize = new FileInfo(staged).Length;
        var stagedHash = await HashAsync(staged, ct).ConfigureAwait(false);
        if (stagedSize != size || stagedHash != sourceHash)
        {
            TryDelete(staged);
            return new Fail($"copy_mismatch: size {stagedSize} of {size}, hash {(stagedHash == sourceHash ? "same" : "differs")}");
        }

        return new Next(PromotionStep.SourceRemoved, new PromotionUpdate(StagedPath: staged, Sha256: sourceHash));
    }

    // ---- source_removed: the radio file goes, the old item leaves the owned index ----

    private async Task<StepResult> RemoveSourceAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        var source = run.Row.Path!;
        if (File.Exists(source))
        {
            // Invariant 6: the radio copy goes only while a verified staged copy exists. Anything else redoes the copy.
            if (promo.StagedPath is not { } staged || promo.Sha256 is not { } sha || !File.Exists(staged)
                || await HashAsync(staged, ct).ConfigureAwait(false) != sha)
            {
                return new Next(PromotionStep.Copied);
            }

            if (!IsUnderSoulseek(source))
            {
                return new Fail("radio_file_outside_soulseek_folder");
            }

            if (_uploads is not null && (await _uploads.CheckActiveAsync(promo.FetchedFileId, source, ct).ConfigureAwait(false)).Active)
            {
                return new Defer("uploading");
            }

            File.Delete(source);
            RemoveEmptyRadioFolders(source);
        }

        _owned.Remove(Guid.Parse(promo.OldItemId));
        await _jellyfin.ReportMediaUpdatedAsync([OldJellyfinFolder(run.Row)], ct).ConfigureAwait(false);
        return new Next(PromotionStep.OldGone);
    }

    // ---- old_gone: wait until Jellyfin dropped the old item ----

    private async Task<StepResult> WaitOldGoneAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        if (run.CameFrom != PromotionStep.SourceRemoved)
        {
            // Resumed (a restart or the next pass after a timeout): ask Jellyfin again.
            await _jellyfin.ReportMediaUpdatedAsync([OldJellyfinFolder(run.Row)], ct).ConfigureAwait(false);
        }

        var old = Guid.Parse(promo.OldItemId);
        var started = _time.GetUtcNow();
        string? lastError = null;
        while (true)
        {
            try
            {
                if (!await _retention.ItemExistsAsync(old, ct).ConfigureAwait(false))
                {
                    return new Next(PromotionStep.Placed);
                }
            }
            catch (JellyfinException e)
            {
                lastError = e.Message;
            }

            if (_time.GetUtcNow() - started >= _timings.OldGoneWait)
            {
                return new Fail("old_item_still_in_jellyfin" + (lastError is null ? "" : $" ({lastError})"));
            }

            await Task.Delay(_timings.OldGonePoll, _time, ct).ConfigureAwait(false);
        }
    }

    // ---- placed: the staged copy becomes the library file ----

    private async Task<StepResult> PlaceAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        if (promo.TargetPath is not { } target || promo.TargetJellyfinPath is not { } targetJellyfin || promo.Sha256 is not { } sha)
        {
            return new Fail("promotion_row_incomplete");
        }

        var staged = promo.StagedPath;
        var stagedThere = staged is not null && File.Exists(staged);
        if (File.Exists(target))
        {
            if (await HashAsync(target, ct).ConfigureAwait(false) != sha)
            {
                return new Fail("target_exists"); // never overwrite (invariant 5); the staged copy stays safe
            }

            if (stagedThere)
            {
                TryDelete(staged!); // our own staged copy of a file already placed (a move that copied, then stopped)
            }

            await _jellyfin.ReportMediaUpdatedAsync([Folder(targetJellyfin)], ct).ConfigureAwait(false);
            return new Next(PromotionStep.Ingested);
        }

        if (!stagedThere)
        {
            return new Fail("staged_copy_missing");
        }

        await WaitForLidarrAsync(promo.FetchedFileId, ct).ConfigureAwait(false);
        var albumFolder = Path.GetDirectoryName(target)!;
        CreateFolder(Path.GetDirectoryName(albumFolder)!);
        CreateFolder(albumFolder);
        File.Move(staged!, target, overwrite: false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, RetentionLimits.NewFileMode);
        }

        await _jellyfin.ReportMediaUpdatedAsync([Folder(targetJellyfin)], ct).ConfigureAwait(false);
        return new Next(PromotionStep.Ingested);
    }

    /// <summary>Waits while Lidarr runs an import, rescan, rename or retag (at most 10 min), then places anyway (8510). A courtesy, not a safety rule.</summary>
    private async Task WaitForLidarrAsync(long rowId, CancellationToken ct)
    {
        if (!_lidarr.Configured)
        {
            return;
        }

        var started = _time.GetUtcNow();
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var running = await _lidarr.RunningImportsAsync(ct).ConfigureAwait(false);
            var waited = _time.GetUtcNow() - started;
            if (running is not { Count: > 0 })
            {
                if (seen.Count > 0)
                {
                    _log.PromoteLidarrWait(rowId, string.Join(", ", seen), (long)waited.TotalSeconds, false);
                }

                return;
            }

            seen.UnionWith(running);
            if (waited >= _timings.LidarrWait)
            {
                _log.PromoteLidarrWait(rowId, string.Join(", ", seen), (long)waited.TotalSeconds, true);
                return;
            }

            await Task.Delay(_timings.LidarrPoll, _time, ct).ConfigureAwait(false);
        }
    }

    // ---- ingested: Jellyfin lists the new file ----

    private async Task<StepResult> WaitIngestAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        var targetJellyfin = promo.TargetJellyfinPath!;
        if (run.CameFrom != PromotionStep.Placed)
        {
            await _jellyfin.ReportMediaUpdatedAsync([Folder(targetJellyfin)], ct).ConfigureAwait(false);
        }

        var library = JellyfinIngest.Evaluate(await _jellyfin.GetVirtualFoldersAsync(ct).ConfigureAwait(false), _options.LibraryJellyfinDir);
        if (library.LibraryItemId is not { } parent)
        {
            return new Fail("library_dir_not_in_a_jellyfin_music_library");
        }

        var started = _time.GetUtcNow();
        string? lastError = null;
        while (true)
        {
            try
            {
                var recent = await _jellyfin.GetRecentAudioAsync(parent, RecentLimit, ct).ConfigureAwait(false);
                if (recent.FirstOrDefault(i => i.Id != Guid.Empty && string.Equals(i.Path, targetJellyfin, StringComparison.Ordinal)) is { } item)
                {
                    return new Next(PromotionStep.Verified, new PromotionUpdate(NewItemId: item.Id.ToString("N")));
                }
            }
            catch (JellyfinException e)
            {
                lastError = e.Message;
            }

            if (_time.GetUtcNow() - started >= _timings.IngestWait)
            {
                return new Fail("ingest_timeout" + (lastError is null ? "" : $" ({lastError})"));
            }

            await Task.Delay(_timings.IngestPoll, _time, ct).ConfigureAwait(false);
        }
    }

    // ---- verified: favorites carried over, or repaired ----

    private async Task<StepResult> VerifyAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        var newItem = Guid.Parse(promo.NewItemId!);
        var saved = _store.UserData(promo.FetchedFileId);
        int carried = 0, repaired = 0, counts = 0;
        foreach (var user in saved)
        {
            var userId = Guid.Parse(user.UserId);
            UserItemData? now;
            try
            {
                now = await _retention.GetUserItemDataAsync(newItem, userId, ct).ConfigureAwait(false);
                if (user.IsFavorite && now is not { IsFavorite: true })
                {
                    await _retention.MarkFavoriteAsync(newItem, userId, ct).ConfigureAwait(false);
                    repaired++;
                }
                else if (user.IsFavorite)
                {
                    carried++;
                }
            }
            catch (JellyfinException e) when (e.StatusCode is 400 or 404)
            {
                // The user was deleted, or cannot see the library the song moved to: nothing to carry for them. Failing here would stall a
                // promotion whose file is already in the library, before its playlist places are restored.
                _log.PromoteUserDataSkipped(promo.FetchedFileId, user.UserId, e.Message);
                continue;
            }

            // A play count that did not carry over is only logged (the count itself is Jellyfin's).
            if (user.PlayCount > 0 && now is { } d && d.PlayCount >= user.PlayCount)
            {
                counts++;
            }
        }

        _log.PromoteUserData(promo.FetchedFileId, saved.Count, carried, repaired, counts);
        return new Next(PromotionStep.Playlists);
    }

    // ---- playlists: re-add at the recorded positions, then purge the dead entries ----

    private async Task<StepResult> ReaddAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        var rowId = promo.FetchedFileId;
        var old = Guid.Parse(promo.OldItemId);
        var newItem = Guid.Parse(promo.NewItemId!);
        foreach (var group in _store.Entries(rowId).GroupBy(e => e.PlaylistId, StringComparer.Ordinal))
        {
            var playlist = Guid.Parse(group.Key);
            var candidates = group.First().CandidateUserIds.Select(Guid.Parse).ToList();
            var editor = group.Select(e => e.EditorUserId).FirstOrDefault(e => e is not null) is { } stored ? Guid.Parse(stored) : (Guid?)null;
            var pending = group.Where(e => e.ReaddedUtc is null).OrderByDescending(e => e.Position).ThenBy(e => e.Id).ToList();
            var gone = false;
            if (pending.Count > 0 && candidates.Count > 0)
            {
                // A crash between an Add and its mark: the new item already sits there more often than the marks say.
                IReadOnlyList<Guid> visible;
                try
                {
                    visible = await _retention.GetPlaylistEntriesAsync(playlist, editor ?? candidates[0], ct).ConfigureAwait(false);
                }
                catch (JellyfinException e) when (e.StatusCode == 404)
                {
                    visible = [];
                    gone = true;
                }

                var extra = visible.Count(v => v == newItem) - group.Count(e => e.ReaddedUtc is not null);
                foreach (var entry in pending.Take(Math.Max(0, extra)).ToList())
                {
                    _store.MarkReadded(entry.Id, _time.GetUtcNow());
                    _log.PlaylistReadded(rowId, group.Key, entry.Position, entry.EditorUserId ?? editor?.ToString("N"));
                    pending.Remove(entry);
                }
            }

            foreach (var entry in gone ? new List<PromotionEntryRow>() : pending)
            {
                PlaylistWriteResult result;
                if (editor is { } known)
                {
                    result = await _retention.AddToPlaylistAsync(playlist, newItem, known, entry.Position, ct).ConfigureAwait(false);
                }
                else
                {
                    result = PlaylistWriteResult.Forbidden;
                    foreach (var candidate in candidates)
                    {
                        result = await _retention.AddToPlaylistAsync(playlist, newItem, candidate, entry.Position, ct).ConfigureAwait(false);
                        if (result != PlaylistWriteResult.Forbidden)
                        {
                            editor = candidate;
                            break;
                        }
                    }
                }

                if (result == PlaylistWriteResult.Forbidden)
                {
                    _log.PlaylistReaddFailed(rowId, group.Key, 403, editor is null ? "every candidate user was refused" : "the editor was refused");
                    return new Fail($"playlist_readd_forbidden {group.Key}");
                }

                if (result == PlaylistWriteResult.NotFound)
                {
                    gone = true;
                    break;
                }

                _store.SetEditor(entry.Id, editor!.Value.ToString("N"));
                _hooks.Checkpoint?.Invoke("playlist_added");
                _store.MarkReadded(entry.Id, _time.GetUtcNow());
                _log.PlaylistReadded(rowId, group.Key, entry.Position, editor.Value.ToString("N"));
            }

            if (gone)
            {
                // The playlist was deleted: nothing to re-add, nothing to purge.
                _log.PlaylistReaddFailed(rowId, group.Key, 404, "the playlist is gone");
                foreach (var entry in _store.Entries(rowId).Where(e => e.PlaylistId == group.Key && e.ReaddedUtc is null))
                {
                    _store.MarkReadded(entry.Id, _time.GetUtcNow());
                }

                continue;
            }

            // One DELETE rebuilds the stored list from the live entries, which purges the dead old entries (harmless when repeated).
            await _retention.RemovePlaylistEntriesAsync(playlist, [old], ct).ConfigureAwait(false);
        }

        return new Finished();
    }

    // ---- done: the row, the requests, the owned index and the shares ----

    private async Task<PromotionResult> FinishAsync(Run run, PromotionRow promo, CancellationToken ct)
    {
        var rowId = promo.FetchedFileId;
        var now = _time.GetUtcNow();
        try
        {
            _fetch.MarkPromoted(rowId, promo.TargetPath!, promo.TargetJellyfinPath, promo.NewItemId, now);
            _requests?.ReplaceItemId(promo.OldItemId, promo.NewItemId!);
            await AddOwnedAsync(run.Row, promo, ct).ConfigureAwait(false);
            _shares?.MarkDirty("promote");
            _store.UpdatePromotion(rowId, PromotionStep.Done, now, new PromotionUpdate(ClearLastError: true));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return Failed(promo, $"{e.GetType().Name}: {e.Message}");
        }

        _log.PromoteStep(rowId, PromotionStep.Done, promo.TargetPath);
        _log.PromoteDone(rowId, promo.OldItemId, promo.NewItemId, promo.TargetPath, (long)(now - promo.StartedUtc).TotalMilliseconds);
        return new PromotionResult(rowId, PromotionOutcome.Done, PromotionStep.Done);
    }

    private async Task AddOwnedAsync(TrackedFile row, PromotionRow promo, CancellationToken ct)
    {
        var newItem = Guid.Parse(promo.NewItemId!);
        OwnedEntry? entry = null;
        try
        {
            var items = await _jellyfin.GetItemsByIdsAsync([newItem], ItemFields, ct).ConfigureAwait(false);
            if (items.FirstOrDefault(i => i.Id == newItem) is { } item)
            {
                entry = OwnedIndex.CreateEntry(item, _options.RadioJellyfinDir);
            }
        }
        catch (JellyfinException)
        {
            // Fall back to the row below.
        }

        if (entry is null && TrackKey.TryParse(row.TrackKey, out var key))
        {
            entry = new OwnedEntry(newItem, key, row.Title, null, row.Artist, null, promo.TargetJellyfinPath, _time.GetUtcNow(), false);
        }

        if (entry is not null)
        {
            _owned.Add(entry);
        }
    }

    // ---- files and folders ----

    private string? LibraryProblem()
    {
        try
        {
            if (!Directory.Exists(_paths.LibraryDir))
            {
                return $"{_paths.LibraryDir} does not exist";
            }

            CreateFolder(_paths.IncomingDir);
            var probe = Path.Combine(_paths.IncomingDir, ".probe-" + Guid.NewGuid().ToString("N") + RetentionLimits.StagedSuffix);
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>Creates a folder that does not exist yet with mode 0755 (its parents must exist, or are created the same way).</summary>
    private static void CreateFolder(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is not null && !Directory.Exists(parent))
        {
            CreateFolder(parent);
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, RetentionLimits.NewFolderMode);
            File.SetUnixFileMode(path, RetentionLimits.NewFolderMode); // the umask may have narrowed it
        }
    }

    private static FileStream NewFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 81920,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = RetentionLimits.NewFileMode;
        }

        return new FileStream(path, options);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private bool IsUnderSoulseek(string path)
    {
        var root = Path.GetFullPath(Path.Combine(_options.RadioDir, SafeNames.SoulseekFolder));
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>Removes the album and artist folders the source left empty, never <c>soulseek/</c> itself.</summary>
    private void RemoveEmptyRadioFolders(string removedFile)
    {
        var root = Path.GetFullPath(Path.Combine(_options.RadioDir, SafeNames.SoulseekFolder)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = Path.GetDirectoryName(Path.GetFullPath(removedFile));
        while (dir is not null && dir.Length > root.Length && dir.StartsWith(root, StringComparison.Ordinal) && Directory.Exists(dir))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    return;
                }

                Directory.Delete(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return; // a folder that cannot go stays; it does no harm
            }

            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>The old file's folder as Jellyfin sees it (its stored Jellyfin path, else the radio path mapped onto the radio Jellyfin dir).</summary>
    private string OldJellyfinFolder(TrackedFile row)
    {
        var path = row.JellyfinPath;
        if (string.IsNullOrEmpty(path))
        {
            var relative = Path.GetRelativePath(_options.RadioDir, row.Path!).Replace('\\', '/');
            path = _options.RadioJellyfinDir.TrimEnd('/') + "/" + relative;
        }

        return Folder(path);
    }

    private static string Folder(string jellyfinPath)
    {
        var cut = jellyfinPath.LastIndexOf('/');
        return cut > 0 ? jellyfinPath[..cut] : jellyfinPath;
    }

    private static string Extension(string path) => Path.GetExtension(path).TrimStart('.') is { Length: > 0 } ext ? ext : "bin";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next attempt, which overwrites it.
        }
    }

    private static string Cut(string text) => text.Length <= 500 ? text : text[..500];
}
