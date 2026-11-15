using System.Globalization;
using Encore.Fetch.Scoring;
using Encore.Slskd;
using Encore.Store;

namespace Encore.Fetch;

/// <summary>
/// Searches Soulseek through slskd for one song and downloads the fastest acceptable copy (v0.2 design section 6.2):
/// at most <see cref="FetchLimits.QueriesPerSong"/> searches through the shared <see cref="SearchBudget"/>, the
/// <see cref="IScorer"/> ranking, up to <see cref="FetchLimits.PeersPerSong"/> peers with the stall rules, and the
/// job deadline. Every search, peer try and dry run writes one <c>fetch_attempts</c> row; a song with no acceptable copy
/// or whose peers all failed goes on the negative cache for 24 h. One instance serves all jobs.
/// </summary>
public sealed class SoulseekFetcher : ISoulseekFetcher
{
    public const string Source = "soulseek";
    private static readonly string[] AudioExtensions = [".flac", ".mp3"];

    private readonly ISlskdClient _slskd;
    private readonly SearchBudget _budget;
    private readonly IScorer _scorer;
    private readonly FetchStore? _store;
    private readonly string _downloadsDir;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <param name="downloadsDir">Encore's view of slskd's downloads folder (ENCORE_SLSKD_DOWNLOADS_DIR).</param>
    /// <param name="store">Where attempts and the negative cache go; null writes nothing (tests of the flow only).</param>
    public SoulseekFetcher(
        ISlskdClient slskd, SearchBudget budget, IScorer scorer, FetchStore? store, string downloadsDir,
        ILogger<SoulseekFetcher> log, TimeProvider? time = null)
    {
        _slskd = slskd;
        _budget = budget;
        _scorer = scorer;
        _store = store;
        _downloadsDir = downloadsDir;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public async Task<SoulseekFetchResult> FetchAsync(SoulseekJob job, SoulseekFetchHooks? hooks, CancellationToken soft, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(job);
        hooks ??= new SoulseekFetchHooks();
        using var beforeEnqueue = CancellationTokenSource.CreateLinkedTokenSource(soft, stopping);
        var reference = new ScoreReference(job.Artist, job.Title, job.Album, job.DurationS, job.Preference);
        var queries = SoulseekQueries.For(job.Artist, job.Title);
        var triedPeers = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new Dictionary<RejectReason, int>();
        var anyFiles = false;
        var anyAccepted = false;
        var peerNo = 0;
        var enqueued = false;
        var queriesRun = 0;

        for (var qi = 0; qi < queries.Count && qi < FetchLimits.QueriesPerSong; qi++)
        {
            if (peerNo >= FetchLimits.PeersPerSong)
            {
                break;
            }

            if (qi > 0 && anyAccepted && peerNo == 0)
            {
                break;   // q2 only runs when q1 found nothing acceptable or all of q1's peers failed
            }

            var query = queries[qi];
            var ct = enqueued ? stopping : beforeEnqueue.Token;
            SearchOutcome outcome;
            try
            {
                outcome = await SearchAsync(job, qi + 1, query, reference, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested && soft.IsCancellationRequested && !enqueued)
            {
                Attempt(job, "cancelled", query: query, reason: FetchFailure.Cancelled);
                return new SoulseekFetchResult.Failed(FetchFailure.Cancelled);
            }

            queriesRun++;
            if (outcome.Failure is { } failure)
            {
                Attempt(job, "failed", query: query, reason: failure);
                return enqueued ? AllPeersFailed(job) : new SoulseekFetchResult.Failed(failure);
            }

            var result = outcome.Result!;
            anyFiles |= result.FilesSeen > 0;
            foreach (var (reason, count) in result.Rejected)
            {
                rejected[reason] = rejected.GetValueOrDefault(reason) + count;
            }

            if (result.Ranked.Count == 0)
            {
                Attempt(job, result.FilesSeen == 0 ? "no_results" : "rejected", query: query,
                    reason: result.FilesSeen == 0 ? null : FetchFailure.NoAcceptable);
                continue;
            }

            anyAccepted = true;
            var best = result.Ranked[0];
            Attempt(job, "ok", query: query, peer: best.Username, filename: best.File.Filename, reason: "search", eta: best.EtaSeconds);

            if (job.DryRun)
            {
                var backups = result.Ranked.Count - 1;
                _log.WouldDownload(job.JobId, job.Artist, job.Title, best.Username, best.File.Filename, FormatName(best.Format),
                    best.Kbps, best.File.Size, Math.Round(best.EtaSeconds, 1), backups);
                Attempt(job, "dry_run", query: query, peer: best.Username, filename: best.File.Filename, eta: best.EtaSeconds);
                return new SoulseekFetchResult.WouldDownload(best, backups, query);
            }

            foreach (var file in result.Ranked)
            {
                if (peerNo >= FetchLimits.PeersPerSong)
                {
                    break;
                }

                if (!triedPeers.Add(file.Username))
                {
                    continue;
                }

                if (_time.GetUtcNow() >= job.Deadline)
                {
                    Attempt(job, "failed", query: query, peer: file.Username, filename: file.File.Filename, reason: FetchFailure.Deadline);
                    return new SoulseekFetchResult.Failed(FetchFailure.Deadline);
                }

                if (!enqueued && beforeEnqueue.IsCancellationRequested)
                {
                    stopping.ThrowIfCancellationRequested();
                    Attempt(job, "cancelled", query: query, reason: FetchFailure.Cancelled);
                    return new SoulseekFetchResult.Failed(FetchFailure.Cancelled);
                }

                peerNo++;
                var peer = await DownloadAsync(job, peerNo, file, outcome.SearchId, query, hooks, stopping).ConfigureAwait(false);
                enqueued |= peer.Enqueued;
                if (peer.File is { } downloaded)
                {
                    return new SoulseekFetchResult.Downloaded(downloaded);
                }
            }
        }

        if (peerNo > 0)
        {
            return AllPeersFailed(job);
        }

        var reasonNone = anyFiles ? FetchFailure.NoAcceptable : FetchFailure.NoResults;
        _log.ScoreNone(job.JobId, job.Artist, job.Title, queriesRun, Describe(rejected));
        Negative(job, reasonNone);
        return new SoulseekFetchResult.Failed(reasonNone);
    }

    private SoulseekFetchResult AllPeersFailed(SoulseekJob job)
    {
        Negative(job, FetchFailure.AllPeersFailed);
        return new SoulseekFetchResult.Failed(FetchFailure.AllPeersFailed);
    }

    // ---------------------------------------------------------------- search

    private sealed record SearchOutcome(Guid SearchId, ScoreResult? Result, string? Failure);

    private async Task<SearchOutcome> SearchAsync(SoulseekJob job, int queryNo, string query, ScoreReference reference, CancellationToken ct)
    {
        var askedAt = _time.GetUtcNow();
        if (!await _budget.TryAcquireAsync(job.Deadline, ct).ConfigureAwait(false))
        {
            return new SearchOutcome(Guid.Empty, null, FetchFailure.Budget);
        }

        var waited = _time.GetUtcNow() - askedAt;
        if (waited > TimeSpan.Zero)
        {
            _log.BudgetWait(job.JobId, Math.Round(waited.TotalSeconds, 1), _budget.SearchesInWindow);
        }

        var id = Guid.NewGuid();
        var started = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _slskd.StartSearchAsync(new SlskdSearchRequest(id, query), ct).ConfigureAwait(false);
                    started = true;
                    break;
                }
                catch (SlskdBusyException) when (attempt < FetchLimits.SearchBusyRetries)
                {
                    await Task.Delay(FetchLimits.SearchBusyWait, _time, ct).ConfigureAwait(false);
                }
                catch (SlskdException ex)
                {
                    LogSlskdFailure(ex);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await CleanupSearchAsync(id, cancelFirst: true).ConfigureAwait(false);   // the POST may have reached slskd
            throw;
        }

        if (!started)
        {
            return new SearchOutcome(id, null, FetchFailure.SlskdUnavailable);
        }

        _log.SearchStarted(job.JobId, queryNo, query, id);
        var startedAt = _time.GetUtcNow();
        var finished = false;
        try
        {
            var endedBy = "max_wait";
            DateTimeOffset? lastScore = null;
            while (true)
            {
                await Task.Delay(FetchLimits.SearchPoll, _time, ct).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                var elapsed = now - startedAt;
                if (elapsed >= FetchLimits.SearchMaxWait)
                {
                    await _slskd.CancelSearchAsync(id, ct).ConfigureAwait(false);
                    break;
                }

                var state = await _slskd.GetSearchAsync(id, ct).ConfigureAwait(false);
                if (state is null || state.IsComplete)
                {
                    endedBy = "complete";
                    break;
                }

                if (elapsed >= FetchLimits.EarlyStopAfter && state.ResponseCount > 0
                    && (lastScore is null || now - lastScore.Value >= FetchLimits.SearchScoreEvery))
                {
                    lastScore = now;
                    var partial = _scorer.Score(At(reference, job), await _slskd.GetSearchResponsesAsync(id, ct).ConfigureAwait(false));
                    if (partial.Ranked.Count > 0 && partial.Ranked[0].HasFreeUploadSlot)
                    {
                        await _slskd.CancelSearchAsync(id, ct).ConfigureAwait(false);
                        endedBy = "early";
                        break;
                    }
                }
            }

            var responses = await _slskd.GetSearchResponsesAsync(id, ct).ConfigureAwait(false);
            var result = _scorer.Score(At(reference, job), responses);
            finished = true;
            _log.SearchDone(job.JobId, id, responses.Count, result.FilesSeen,
                (long)(_time.GetUtcNow() - startedAt).TotalMilliseconds, endedBy);
            _log.ScoreResult(job.JobId, queryNo, result.Ranked.Count, Describe(result.Rejected), DescribeBest(result));
            return new SearchOutcome(id, result, null);
        }
        catch (SlskdException ex)
        {
            LogSlskdFailure(ex);
            return new SearchOutcome(id, null, FetchFailure.SlskdUnavailable);
        }
        finally
        {
            await CleanupSearchAsync(id, cancelFirst: !finished).ConfigureAwait(false);
        }
    }

    private async Task CleanupSearchAsync(Guid id, bool cancelFirst)
    {
        using var cleanup = new CancellationTokenSource(FetchLimits.CleanupTimeout);
        try
        {
            if (cancelFirst)
            {
                await _slskd.CancelSearchAsync(id, cleanup.Token).ConfigureAwait(false);
            }

            await _slskd.DeleteSearchAsync(id, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SlskdException or OperationCanceledException)
        {
            if (ex is SlskdException s)
            {
                LogSlskdFailure(s);
            }
        }
    }

    // ---------------------------------------------------------------- download

    private sealed record PeerOutcome(bool Enqueued, DownloadedFile? File);

    private async Task<PeerOutcome> DownloadAsync(
        SoulseekJob job, int peerNo, ScoredFile file, Guid searchId, string query, SoulseekFetchHooks hooks, CancellationToken stopping)
    {
        var peer = file.Username;
        var remote = file.File.Filename;
        var enqueueAt = _time.GetUtcNow();
        SlskdEnqueueResult enq;
        try
        {
            enq = await _slskd.EnqueueAsync(
                new SlskdEnqueueRequest(Guid.NewGuid(), searchId, peer, remote, file.File.Size, $"encore/{job.JobId}"), stopping).ConfigureAwait(false);
        }
        catch (SlskdPeerOfflineException)
        {
            return GiveUp(job, null, peer, remote, query, file, PeerFailure.Offline, "offline", 0, enqueueAt, enqueued: false);
        }
        catch (SlskdException ex)
        {
            LogSlskdFailure(ex);
            return GiveUp(job, null, peer, remote, query, file, PeerFailure.Errored, "enqueue_failed", 0, enqueueAt, enqueued: false);
        }

        if (enq.TransferId is not { } transferId || enq.Failures.Count > 0)
        {
            if (enq.TransferId is { } stray)
            {
                await RemoveTransferAsync(peer, stray).ConfigureAwait(false);
            }

            return GiveUp(job, null, peer, remote, query, file, PeerFailure.Offline, "refused", 0, enqueueAt, enqueued: false);
        }

        var fileId = hooks.OnEnqueued?.Invoke(new EnqueuedDownload(job.JobId, peerNo, file, transferId, query));
        _log.Enqueued(job.JobId, peerNo, peer, remote, file.File.Size, transferId);

        var lastBytes = 0L;
        var lastState = "Requested";
        var lastProgressAt = enqueueAt;
        DateTimeOffset? inProgressSince = null;
        SlskdTransfer done;
        try
        {
            while (true)
            {
                await Task.Delay(FetchLimits.TransferPoll, _time, stopping).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                SlskdTransfer? t;
                try
                {
                    t = await _slskd.GetTransferAsync(peer, transferId, stopping).ConfigureAwait(false);
                }
                catch (SlskdException ex) when (ex is not SlskdAuthException)
                {
                    LogSlskdFailure(ex);
                    t = null;
                    if (StallReason(now, enqueueAt, lastProgressAt, inProgressSince, lastBytes, lastState, null, job.Deadline) is { } why0)
                    {
                        await RemoveTransferAsync(peer, transferId).ConfigureAwait(false);
                        return GiveUp(job, fileId, peer, remote, query, file, why0, lastState, lastBytes, enqueueAt, enqueued: true, stalled: true);
                    }

                    continue;
                }

                if (t is null)
                {
                    return GiveUp(job, fileId, peer, remote, query, file, PeerFailure.Errored, "gone", lastBytes, enqueueAt, enqueued: true);
                }

                lastState = t.State;
                if (t.IsSucceeded)
                {
                    done = t;
                    break;
                }

                if (t.IsTerminal || (t.State.Contains("Errored", StringComparison.OrdinalIgnoreCase) && t.NextAttemptAt is not null))
                {
                    await RemoveTransferAsync(peer, transferId).ConfigureAwait(false);
                    var why = t.State.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ? PeerFailure.Rejected : PeerFailure.Errored;
                    return GiveUp(job, fileId, peer, remote, query, file, why, t.State, t.BytesTransferred, enqueueAt, enqueued: true);
                }

                if (inProgressSince is null && t.State.StartsWith("InProgress", StringComparison.OrdinalIgnoreCase))
                {
                    inProgressSince = now;
                    lastProgressAt = now;   // the idle clock starts when the transfer starts
                }

                if (t.BytesTransferred != lastBytes)
                {
                    lastBytes = t.BytesTransferred;
                    lastProgressAt = now;
                }

                if (StallReason(now, enqueueAt, lastProgressAt, inProgressSince, lastBytes, t.State, t, job.Deadline) is { } stall)
                {
                    await RemoveTransferAsync(peer, transferId).ConfigureAwait(false);
                    return GiveUp(job, fileId, peer, remote, query, file, stall, t.State, t.BytesTransferred, enqueueAt, enqueued: true, stalled: true);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await RemoveTransferAsync(peer, transferId).ConfigureAwait(false);
            Attempt(job, "cancelled", fileId, query, peer, remote, "shutdown", file.EtaSeconds, enqueueAt);
            throw;
        }

        var elapsed = _time.GetUtcNow() - enqueueAt;
        var local = FindLocalFile(job.JobId, remote);
        await RemoveTransferAsync(peer, transferId).ConfigureAwait(false);
        if (local is null)
        {
            return GiveUp(job, fileId, peer, remote, query, file, PeerFailure.MissingFile, done.State, done.BytesTransferred, enqueueAt, enqueued: true);
        }

        _log.Downloaded(job.JobId, peer, done.Size, (long)elapsed.TotalMilliseconds, done.AverageSpeed);
        var downloaded = new DownloadedFile(job.JobId, peerNo, file, transferId, local, query, fileId, elapsed);
        if (hooks.VerifyAsync is { } verify && !await verify(downloaded, stopping).ConfigureAwait(false))
        {
            _log.TransferStalled(job.JobId, peer, PeerFailure.Mismatch, done.State, done.BytesTransferred);
            Attempt(job, "rejected", fileId, query, peer, remote, PeerFailure.Mismatch, file.EtaSeconds, enqueueAt);
            return new PeerOutcome(true, null);
        }

        Attempt(job, "ok", fileId, query, peer, remote, null, file.EtaSeconds, enqueueAt);
        return new PeerOutcome(true, downloaded);
    }

    /// <summary>The stall rules of design section 6.2, in order: queued, idle, too_slow.</summary>
    private static string? StallReason(
        DateTimeOffset now, DateTimeOffset enqueueAt, DateTimeOffset lastProgressAt, DateTimeOffset? inProgressSince,
        long bytes, string state, SlskdTransfer? t, DateTimeOffset deadline)
    {
        if (inProgressSince is null)
        {
            return bytes == 0 && now - enqueueAt >= FetchLimits.StallQueued ? PeerFailure.Queued : null;
        }

        if (now - lastProgressAt >= FetchLimits.StallIdle)
        {
            return PeerFailure.Idle;
        }

        if (t is not null && t.AverageSpeed > 0 && now - inProgressSince.Value >= FetchLimits.TooSlowCheckAfter
            && state.StartsWith("InProgress", StringComparison.OrdinalIgnoreCase))
        {
            var remainingS = Math.Max(0, t.Size - t.BytesTransferred) / t.AverageSpeed;
            if (remainingS > (deadline - now).TotalSeconds)
            {
                return PeerFailure.TooSlow;
            }
        }

        return null;
    }

    private PeerOutcome GiveUp(
        SoulseekJob job, long? fileId, string peer, string remote, string query, ScoredFile file, string why, string state, long bytes,
        DateTimeOffset since, bool enqueued, bool stalled = false)
    {
        _log.TransferStalled(job.JobId, peer, why, state, bytes);
        Attempt(job, stalled ? "stalled" : "failed", fileId, query, peer, remote, why, file.EtaSeconds, since);
        return new PeerOutcome(enqueued, null);
    }

    private async Task RemoveTransferAsync(string peer, Guid transferId)
    {
        using var cleanup = new CancellationTokenSource(FetchLimits.CleanupTimeout);
        try
        {
            await _slskd.CancelTransferAsync(peer, transferId, remove: true, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SlskdException or OperationCanceledException)
        {
            if (ex is SlskdException s)
            {
                LogSlskdFailure(s);
            }
        }
    }

    /// <summary>
    /// slskd's copy of a finished download: <c>{downloads}/encore/{jobId}/{last segment of the remote name}</c>; else the one
    /// <c>{name}_*{ext}</c> (slskd's rename when the name was taken); else the only audio file in that folder; else null.
    /// </summary>
    public string? FindLocalFile(string jobId, string remoteFilename)
    {
        var dir = Path.Combine(_downloadsDir, "encore", jobId);
        var name = LocalName(remoteFilename);
        try
        {
            var exact = Path.Combine(dir, name);
            if (File.Exists(exact))
            {
                return exact;
            }

            if (!Directory.Exists(dir))
            {
                return null;
            }

            var ext = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            var renamed = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var n = Path.GetFileName(f);
                    return n.StartsWith(stem + "_", StringComparison.Ordinal) && n.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            if (renamed.Count == 1)
            {
                return renamed[0];
            }

            var audio = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
            return audio.Count == 1 ? audio[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last segment of a Soulseek path (peers use <c>\</c>; <c>/</c> is accepted too).</summary>
    public static string LocalName(string remoteFilename)
    {
        var cut = remoteFilename.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? remoteFilename[(cut + 1)..] : remoteFilename;
    }

    // ---------------------------------------------------------------- store and log helpers

    private void Attempt(
        SoulseekJob job, string outcome, long? fileId = null, string? query = null, string? peer = null, string? filename = null,
        string? reason = null, double? eta = null, DateTimeOffset? since = null)
    {
        if (_store is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        int? durationMs = since is { } s ? (int)Math.Min(int.MaxValue, (now - s).TotalMilliseconds) : null;
        try
        {
            _store.AddAttempt(new FetchAttempt(job.JobId, job.TrackKey, Source, outcome, now, fileId, query, peer, filename, reason, eta, durationMs));
        }
        catch (StoreException ex)
        {
            _log.RequestFailed("store.attempt", null, ex.Message);
        }
    }

    /// <summary>FlacFirst needs the time left to the deadline when it scores; Fastest is unchanged.</summary>
    private ScoreReference At(ScoreReference reference, SoulseekJob job) =>
        reference.Preference == FetchPreference.Fastest
            ? reference
            : reference with { SecondsLeft = (job.Deadline - _time.GetUtcNow()).TotalSeconds };

    private void Negative(SoulseekJob job, string reason)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            _store.UpsertNegative(job.TrackKey, _time.GetUtcNow() + FetchLimits.NegativeCache, reason);
        }
        catch (StoreException ex)
        {
            _log.RequestFailed("store.negative", null, ex.Message);
        }
    }

    private void LogSlskdFailure(SlskdException ex)
    {
        if (ex is SlskdAuthException)
        {
            _log.AuthFailed(ex.Operation, ex.StatusCode);
        }
        else
        {
            _log.RequestFailed(ex.Operation, ex.StatusCode, ex.Message);
        }
    }

    private static string FormatName(AudioFormat format) => format == AudioFormat.Flac ? "flac" : "mp3";

    private static string Describe(IReadOnlyDictionary<RejectReason, int> rejected) =>
        string.Join(", ", rejected.Where(p => p.Value > 0).OrderBy(p => p.Key)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key.ToString().ToLowerInvariant()}={p.Value}")));

    private static string DescribeBest(ScoreResult result)
    {
        if (result.Ranked.Count == 0)
        {
            return "none";
        }

        var b = result.Ranked[0];
        return string.Create(CultureInfo.InvariantCulture,
            $"{b.Username} {b.File.Filename} {FormatName(b.Format)} {b.Kbps?.ToString(CultureInfo.InvariantCulture) ?? "?"} kbps eta {Math.Round(b.EtaSeconds, 1)} s");
    }
}
