using System.Diagnostics;
using System.Threading.Channels;
using Encore.Config;
using Encore.Jellyfin;
using Encore.Logging;
using Encore.Picks;
using Encore.Store;
using Encore.Trigger;
using Encore.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace Encore.Orchestration;

/// <summary>
/// The one loop that makes decisions (design section 1). It reads session snapshots from the watcher's channel,
/// keeps a tracker per session, runs the pure trigger, and on a fire picks songs and sends PlayLast (or logs
/// "would queue" in dry run). It also keeps the allow-list resolved, retries failed fires, drops idle trackers,
/// prunes the store, and keeps the heartbeat that <c>/health</c> reads. All state is touched only from this loop.
/// </summary>
public sealed class Orchestrator : BackgroundService
{
    private const string AudioType = "Audio";

    private readonly ChannelReader<SessionsSnapshot> _snapshots;
    private readonly IJellyfinClient _jellyfin;
    private readonly IPicker _picker;
    private readonly EncoreStore _store;
    private readonly EncoreOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<Orchestrator> _log;
    private readonly OrchestratorTiming _timing;
    private readonly Dictionary<string, SessionTrack> _sessions = new(StringComparer.Ordinal);
    private IReadOnlySet<Guid> _allowed = new HashSet<Guid>();
    private DateTimeOffset _nextAllowListAt;
    private DateTimeOffset _nextPruneAt;
    private int _allowListFailures;
    private OrchestratorStatus _status;

    public Orchestrator(
        IJellyfinClient jellyfin,
        IPicker picker,
        EncoreStore store,
        EncoreOptions options,
        ChannelReader<SessionsSnapshot> snapshots,
        TimeProvider time,
        ILogger<Orchestrator> log,
        OrchestratorTiming? timing = null)
    {
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timing = timing ?? new OrchestratorTiming();

        var now = _time.GetUtcNow();
        _nextAllowListAt = now;
        _nextPruneAt = now + _timing.PruneEvery;
        _status = new OrchestratorStatus
        {
            StartedUtc = now,
            LastHeartbeatUtc = now,
            AllowListConfigured = options.AllowedUsers,
        };
    }

    /// <summary>The current status, for <c>/health</c>.</summary>
    public OrchestratorStatus Status => Volatile.Read(ref _status);

    /// <summary>The allow-list ids that resolved (empty until the first successful lookup).</summary>
    public IReadOnlySet<Guid> AllowedUserIds => _allowed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SafelyAsync("housekeeping", () => TickAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
            var tick = Task.Delay(_timing.Tick, stoppingToken);
            var reading = _snapshots.WaitToReadAsync(stoppingToken).AsTask();
            while (true)
            {
                var first = await Task.WhenAny(reading, tick).ConfigureAwait(false);
                if (ReferenceEquals(first, tick))
                {
                    await SafelyAsync("housekeeping", () => TickAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
                    tick = Task.Delay(_timing.Tick, stoppingToken);
                    continue;
                }

                // The channel is completed only when the host shuts down.
                if (!await reading.ConfigureAwait(false))
                {
                    break;
                }

                while (_snapshots.TryRead(out var snapshot))
                {
                    await SafelyAsync("snapshot", () => HandleSnapshotAsync(snapshot, stoppingToken), stoppingToken).ConfigureAwait(false);
                }

                reading = _snapshots.WaitToReadAsync(stoppingToken).AsTask();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Handles one snapshot: evaluates every session in it. Public so tests can drive the loop step by step;
    /// the running service calls it from <see cref="ExecuteAsync"/>.
    /// </summary>
    public async Task HandleSnapshotAsync(SessionsSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Beat();
        foreach (var dto in snapshot.Sessions)
        {
            if (string.IsNullOrEmpty(dto.Id))
            {
                continue;
            }

            // One bad session must not stop the others in the same snapshot from being evaluated.
            await SafelyAsync("session", () => ProcessSessionAsync(dto, ct), ct).ConfigureAwait(false);
        }

        // A snapshot lists every session. One that waited for a delivery verdict and is gone has ended: whatever was
        // queued will not play there, so judge it now instead of keeping it until the tracker is forgotten.
        var present = new HashSet<string>(snapshot.Sessions.Select(s => s.Id ?? string.Empty), StringComparer.Ordinal);
        foreach (var track in _sessions.Where(kv => kv.Value.Pending is not null && !present.Contains(kv.Key)).Select(kv => kv.Value).ToList())
        {
            ResolveDelivery(track, confirmed: false, "session_ended", null, _time.GetUtcNow());
        }

        JudgeLongStops(_time.GetUtcNow());
        RefreshCounts();
    }

    /// <summary>GET /Users and resolves the allow-list. Keeps the previous set when the call fails. Public for tests and startup.</summary>
    public async Task RefreshAllowListAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        try
        {
            var users = await _jellyfin.GetUsersAsync(ct).ConfigureAwait(false);
            var resolution = AllowList.Resolve(_options.AllowedUsers, users);
            _allowed = resolution.UserIds;

            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var user in users.Where(u => _allowed.Contains(u.Id)))
            {
                resolved[user.Name ?? user.Id.ToString("N")] = user.Id.ToString("N");
            }

            var unresolved = resolution.Problems.Select(p => p.Entry).ToList();
            _allowListFailures = 0;
            _nextAllowListAt = now + (unresolved.Count > 0 ? _timing.AllowListProblemRecheck : _timing.AllowListRefresh);

            OperationsLog.AllowListResolved(_log, string.Join(", ", resolved.Select(kv => $"{kv.Key}={kv.Value}")));
            if (unresolved.Count > 0)
            {
                OperationsLog.AllowListUnresolved(_log, string.Join(", ", unresolved));
            }

            Update(s => s with
            {
                AllowListResolved = resolved,
                AllowListUnresolved = unresolved,
                AllowListResolvedUtc = now,
                AllowListError = null,
                JellyfinLastOkUtc = now,
                JellyfinLastError = null,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (JellyfinException ex)
        {
            _allowListFailures++;
            var delay = _timing.AllowListRetryMin * Math.Pow(2, Math.Min(_allowListFailures - 1, 10));
            if (delay > _timing.AllowListRetryMax)
            {
                delay = _timing.AllowListRetryMax;
            }

            _nextAllowListAt = now + delay;
            var unauthorized = ex is JellyfinAuthException;
            if (ex is JellyfinAuthException auth)
            {
                _log.JellyfinAuthFailed(auth.Operation, auth.StatusCode);
            }

            OperationsLog.AllowListResolveFailed(_log, ex.Message, delay.TotalSeconds);
            Update(s => s with
            {
                AllowListError = ex.Message,
                JellyfinLastError = unauthorized ? "unauthorized" : ex.Message,
            });
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        Beat();

        if (now >= _nextAllowListAt)
        {
            await RefreshAllowListAsync(ct).ConfigureAwait(false);
        }

        if (now >= _nextPruneAt)
        {
            _nextPruneAt = now + _timing.PruneEvery;
            TryStore("prune", () => _store.Prune(now), (PruneResult?)null);
        }

        // A failed fire is retried by the trigger after its delay, but only when a snapshot arrives.
        // Re-evaluate the last snapshot here, so the retry happens even when the server is quiet.
        foreach (var id in _sessions.Keys.ToList())
        {
            if (!_sessions.TryGetValue(id, out var track))
            {
                continue;
            }

            if (track.Memory?.Instance is { State: InstanceState.FireFailed, FailedAt: { } failedAt }
                && now - failedAt >= TriggerLimits.FireRetryDelay
                && track.LastSession is { } session
                && track.LastDto is { } dto)
            {
                await SafelyAsync("retry", () => EvaluateAsync(id, track, session, dto, track.Allowed, now, ct), ct).ConfigureAwait(false);
            }
        }

        JudgeLongStops(now);

        // A session unseen for this long is gone, whatever its state. (A FireFailed tracker used to be kept, but its
        // retry happens within seconds; one whose retry is then skipped would stay FireFailed and be re-evaluated
        // every tick forever.)
        foreach (var id in _sessions.Keys.ToList())
        {
            var track = _sessions[id];
            if (now - track.LastSeen > _timing.SessionForgetAfter)
            {
                _sessions.Remove(id);
            }
        }

        RefreshCounts();
    }

    private async Task ProcessSessionAsync(SessionInfoDto dto, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var session = SessionMapping.ToTrigger(dto);
        var allowed = session.UserId is { } user && _allowed.Contains(user);
        if (!_sessions.TryGetValue(dto.Id, out var track))
        {
            track = new SessionTrack();
        }

        if (allowed && !track.RunLoaded)
        {
            track.RunLoaded = true;
            track.Run ??= LoadRun(dto.Id, now);
        }

        track.LastSeen = now;
        track.Allowed = allowed;
        track.LastDto = dto;
        track.LastSession = session;
        if (dto.NowPlayingItem is null)
        {
            JudgeStopped(track, dto, now);
        }
        else
        {
            track.StoppedSince = null;
        }

        await EvaluateAsync(dto.Id, track, session, dto, allowed, now, ct).ConfigureAwait(false);
    }

    private async Task EvaluateAsync(string sessionId, SessionTrack track, TriggerSession session, SessionInfoDto dto, bool allowed, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = TriggerEngine.Evaluate(session, track.Memory, BuildContext(track, session, allowed, now));
        if (outcome.Memory is null)
        {
            // Not a listener's session (no user, or Encore's own device): keep no tracker.
            _sessions.Remove(sessionId);
            return;
        }

        track.Memory = outcome.Memory;
        _sessions[sessionId] = track;

        if (outcome.InstanceStarted)
        {
            OnInstanceStarted(track, session, dto, allowed, now);
        }

        if (outcome.Decision is TriggerDecision.Skip skip)
        {
            if (skip.ShouldLog)
            {
                LogSkip(session, skip.Reason);
            }
        }
        else if (outcome.Decision is TriggerDecision.Fire fire)
        {
            await FireAsync(track, session, dto, fire, now, ct).ConfigureAwait(false);
        }
    }

    private TriggerContext BuildContext(SessionTrack track, TriggerSession session, bool allowed, DateTimeOffset now)
    {
        var counting = allowed && session.NowPlayingItem is not null;
        var fallback = TriggerLimits.GlobalItemsPerHour + 1;
        return new TriggerContext
        {
            AllowedUserIds = _allowed,
            Now = now,
            OwnDeviceId = _options.DeviceId,
            Batch = _options.Batch,
            // If the store cannot answer, treat the key as a duplicate: a skipped radio is safer than a double add.
            IsDuplicate = key => TryStore("duplicate check", () => _store.IsDuplicate(key, now, TriggerLimits.DedupeTtl), true),
            LastSessionFireAt = track.LastFireAt,
            SessionItemsLastHour = counting
                ? TryStore("count session", () => _store.CountAddedSince(session.Id, now - TimeSpan.FromHours(1)), fallback)
                : 0,
            GlobalItemsLastHour = counting
                ? TryStore("count global", () => _store.CountAddedSince(null, now - TimeSpan.FromHours(1)), fallback)
                : 0,
            Run = track.Run is { } run ? new RunInfo(run.LastAddedItemId, run.AddedCount) : null,
        };
    }

    private SessionRun? LoadRun(string sessionId, DateTimeOffset now)
    {
        var stored = TryStore("latest run", () => _store.LatestRun(sessionId, now - _timing.RunResumeWindow), (StoredRun?)null);
        return stored is null
            ? null
            : new SessionRun(stored.RunId, new RadioPlan(_options.Lookahead, _options.Batch))
            {
                LastAddedItemId = stored.LastAddedItemId,
                AddedCount = stored.AddedCount,
            };
    }

    private void OnInstanceStarted(SessionTrack track, TriggerSession session, SessionInfoDto dto, bool allowed, DateTimeOffset now)
    {
        var item = session.NowPlayingItem;
        var instance = track.Memory?.Instance;
        if (item is null || instance is null || track.Memory is null)
        {
            return;
        }

        OperationsLog.TrackChanged(_log, session.Id, item.Id, instance.EntryQueue.Count, track.Memory.PrevEntryQueue?.Count ?? -1, session.RepeatMode, instance.Sequence);

        // Delivery check: the session's next play instance shows whether the songs were really queued.
        if (track.Pending is { } pending)
        {
            var batch = pending.ItemIds;
            var confirmed = batch.Contains(item.Id) || instance.EntryQueue.Any(e => batch.Contains(e.Id));
            ResolveDelivery(track, confirmed, "next_song", item.Id, now);
        }

        if (allowed && session.UserId is { } userId && string.Equals(item.Type, AudioType, StringComparison.Ordinal))
        {
            var artist = Picker.ArtistOf(dto.NowPlayingItem!)?.Id;
            TryStoreAction("record play", () => _store.RecordPlayed(session.Id, userId, item.Id, artist, now));
            TryStoreAction("upsert session", () => _store.UpsertSession(session.Id, userId, session.Client, dto.DeviceName, now));
        }
    }

    /// <summary>
    /// The session shows no playing item while a batch waits for its verdict. In Jellyfin 12.4 that happens only after
    /// the client's PlaybackStopped, and the server stores the stop's queue just before it clears the item, so the
    /// queue in this snapshot is the client's fresh report. A non-empty report is judged at once (jellyfin-web lists
    /// the whole queue; Finamp lists what comes next). An empty or missing report is ambiguous: a real stop, or Finamp
    /// moving onto the last queued song. That waits <see cref="OrchestratorTiming.StoppedVerdictGrace"/>; a new song
    /// within it is judged by the next-song check instead.
    /// </summary>
    private void JudgeStopped(SessionTrack track, SessionInfoDto dto, DateTimeOffset now)
    {
        if (track.Pending is not { } pending)
        {
            return;
        }

        if (dto.NowPlayingQueue is { Count: > 0 } queue)
        {
            var confirmed = queue.Any(q => pending.ItemIds.Contains(q.Id));
            ResolveDelivery(track, confirmed, "stopped", null, now);
        }
        else
        {
            track.StoppedSince ??= now;
        }
    }

    /// <summary>
    /// A session stopped with an empty or missing queue report, and no song started within the grace: it was a real
    /// stop, not a track change, so the batch did not arrive. Runs on every tick and snapshot.
    /// </summary>
    private void JudgeLongStops(DateTimeOffset now)
    {
        foreach (var track in _sessions.Values.ToList())
        {
            if (track.Pending is not null && track.StoppedSince is { } stopped && now - stopped >= _timing.StoppedVerdictGrace)
            {
                ResolveDelivery(track, confirmed: false, "stopped", null, now);
            }
        }
    }

    /// <summary>Records the delivery verdict of the pending batch and logs <c>queue.confirmed</c> or <c>queue.unconfirmed</c>.</summary>
    private void ResolveDelivery(SessionTrack track, bool confirmed, string judgedAt, Guid? nextItemId, DateTimeOffset now)
    {
        if (track.Pending is not { } pending)
        {
            return;
        }

        track.Pending = null;
        track.StoppedSince = null;
        var ids = string.Join(',', pending.ItemIds.Select(id => id.ToString("N")));
        TryStoreAction("delivery", () => _store.MarkDelivery(pending.TriggerId, confirmed, now));
        if (confirmed)
        {
            OperationsLog.QueueConfirmed(_log, pending.RunId, ids);
        }
        else
        {
            OperationsLog.QueueUnconfirmed(_log, pending.RunId, ids, judgedAt, nextItemId);
            Update(s => s with { Unconfirmed = s.Unconfirmed + 1 });
        }
    }

    private void LogSkip(TriggerSession session, SkipReason reason)
    {
        if (reason is SkipReason.NotLast or SkipReason.NotAllowed)
        {
            OperationsLog.TriggerSkippedDebug(_log, session.Id, session.UserName, session.Client, reason.ToWire());
        }
        else
        {
            OperationsLog.TriggerSkipped(_log, session.Id, session.UserName, session.Client, reason.ToWire());
        }
    }

    private async Task FireAsync(SessionTrack track, TriggerSession session, SessionInfoDto dto, TriggerDecision.Fire fire, DateTimeOffset now, CancellationToken ct)
    {
        // The trigger fires only for a playing item of an allow-listed user.
        var item = dto.NowPlayingItem!;
        var userId = session.UserId!.Value;
        var clock = Stopwatch.GetTimestamp();

        var run = fire.Kind == FireKind.TopUp && track.Run is { } current
            ? current
            : new SessionRun(Guid.NewGuid().ToString("N"), new RadioPlan(_options.Lookahead, _options.Batch));
        track.Run = run;

        OperationsLog.TriggerFired(_log, fire.Kind.ToWire(), session.Id, fire.Model.ToWire(), item.Id, item.Name, run.RunId, fire.DedupeKey);

        try
        {
            var queueIds = dto.NowPlayingQueue?.Select(q => q.Id).ToList() ?? [];
            var plays = _store.RecentPlays(_options.RecentWindow, session.Id, userId, now);
            var exclusions = RecentExclusions.Build(queueIds, item.Id, plays, _options.RecentWindow, session.Id, userId, now);
            var history = _store.SessionHistory(session.Id, _timing.HistoryLimit);

            PlanHandout handout;
            using (var pickTimeout = new CancellationTokenSource(_timing.PickTimeout, _time))
            using (var pickCts = CancellationTokenSource.CreateLinkedTokenSource(ct, pickTimeout.Token))
            {
                try
                {
                    handout = await run.Plan.NextBatchAsync(_picker, userId, item, history, exclusions, pickCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Counts as a failed fire (retried once), and keeps the loop's heartbeat inside the health window.
                    throw new TimeoutException($"picking songs took longer than {_timing.PickTimeout.TotalSeconds:0} s");
                }
            }

            if (handout.IsEmpty)
            {
                RecordOutcome(track, session, run, fire, "no_picks", now, clock, [], null, null);
                return;
            }

            if (_options.DryRun)
            {
                OperationsLog.QueueWouldAdd(_log, fire.Kind.ToWire(), session.Id, string.Join("; ", handout.Batch));
                RecordOutcome(track, session, run, fire, EncoreStore.DryRunOutcome, now, clock, handout.Batch, null, null);
                return;
            }

            var ids = handout.Batch.Select(i => i.Id).ToList();
            string outcome;
            string? error = null;
            int? status = 204;
            try
            {
                await _jellyfin.PlayLastAsync(session.Id, ids, ct).ConfigureAwait(false);
                outcome = EncoreStore.SentOutcome;
                Update(s => s with { JellyfinLastOkUtc = now, JellyfinLastError = null });
                OperationsLog.QueueAdded(_log, fire.Kind.ToWire(), session.Id, string.Join("; ", handout.Batch), 204);
            }
            catch (JellyfinException ex) when (ex.OutcomeUnknown && !ct.IsCancellationRequested)
            {
                // No answer, but the request may have reached Jellyfin and the songs may be queued already. A retry
                // could queue them twice, so this counts as sent (dedupe, cooldown, run) and the delivery check,
                // from the queue the client reports at its next track change or stop, decides whether they arrived.
                outcome = EncoreStore.SentNoReplyOutcome;
                status = null;
                error = Truncate(ex.Message);
                var reason = error;
                Update(s => s with { JellyfinLastError = reason });
                OperationsLog.QueueAddNoReply(_log, session.Id, error, string.Join("; ", handout.Batch));
            }

            var triggerId = RecordOutcome(track, session, run, fire, outcome, now, clock, handout.Batch, status, error);
            if (triggerId is { } id)
            {
                track.Pending = new PendingDelivery(id, run.RunId, ids);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(track, session, run, fire, now, clock, ex);
        }
    }

    /// <summary>Writes the trigger row, updates the run and the instance state, and the counters. Returns the trigger id.</summary>
    private long? RecordOutcome(
        SessionTrack track,
        TriggerSession session,
        SessionRun run,
        TriggerDecision.Fire fire,
        string outcome,
        DateTimeOffset now,
        long clock,
        IReadOnlyList<PickedItem> items,
        int? httpStatus,
        string? error)
    {
        var record = new TriggerRecord(
            now, session.Id, session.UserId!.Value, session.Client, fire.Model.ToWire(), fire.Kind.ToWire(),
            session.NowPlayingItem!.Id, session.PlaylistItemId, fire.DedupeKey, outcome, run.RunId,
            ElapsedMs(clock), httpStatus, error);
        var triggerId = TryStore("record trigger", () => (long?)_store.RecordFire(record, items), (long?)null);

        // A send without an answer counts as sent: the songs may be in the queue.
        var sent = outcome is EncoreStore.SentOutcome or EncoreStore.SentNoReplyOutcome;
        if (items.Count > 0 && (sent || outcome == EncoreStore.DryRunOutcome))
        {
            run.LastAddedItemId = items[^1].Id;
            run.AddedCount += items.Count;
        }

        if (sent || outcome == EncoreStore.DryRunOutcome)
        {
            track.LastFireAt = now;
        }

        track.Memory = TriggerEngine.MarkFired(track.Memory!);
        Update(s => s with
        {
            Fired = s.Fired + 1,
            Sent = s.Sent + (sent ? 1 : 0),
            DryRun = s.DryRun + (outcome == EncoreStore.DryRunOutcome ? 1 : 0),
            NoPicks = s.NoPicks + (outcome == "no_picks" ? 1 : 0),
            ItemsSent = s.ItemsSent + (sent ? items.Count : 0),
            LastSentUtc = sent ? now : s.LastSentUtc,
            LastFireUtc = now,
            LastOutcome = outcome,
        });
        return triggerId;
    }

    private void RecordFailure(SessionTrack track, TriggerSession session, SessionRun run, TriggerDecision.Fire fire, DateTimeOffset now, long clock, Exception ex)
    {
        var status = ex is JellyfinException jellyfin ? jellyfin.StatusCode : null;
        var error = Truncate(ex.Message);
        if (ex is JellyfinException)
        {
            Update(s => s with { JellyfinLastError = ex is JellyfinAuthException ? "unauthorized" : error });
        }

        if (ex is JellyfinAuthException auth)
        {
            _log.JellyfinAuthFailed(auth.Operation, auth.StatusCode);
        }

        if (ex is SqliteException or StoreException)
        {
            OperationsLog.StoreError(_log, "fire", error);
            Update(s => s with { StoreOk = false });
        }

        OperationsLog.QueueAddFailed(_log, session.Id, error, status, string.Empty);

        var record = new TriggerRecord(
            now, session.Id, session.UserId!.Value, session.Client, fire.Model.ToWire(), fire.Kind.ToWire(),
            session.NowPlayingItem!.Id, session.PlaylistItemId, fire.DedupeKey, "failed", run.RunId,
            ElapsedMs(clock), status, error);
        TryStoreAction("record failure", () => _store.RecordFire(record, []));

        // The retry delay counts from when the fire actually failed, not from the snapshot that started it.
        var (memory, gaveUp) = TriggerEngine.MarkFailed(track.Memory!, _time.GetUtcNow());
        track.Memory = memory;
        if (gaveUp)
        {
            OperationsLog.TriggerGaveUp(_log, session.Id, memory.Instance?.Attempts ?? 0, error);
        }

        Update(s => s with
        {
            Fired = s.Fired + 1,
            Failed = s.Failed + 1,
            LastFireUtc = now,
            LastOutcome = "failed",
        });
    }

    /// <summary>Runs one housekeeping or event step. A failure is logged and the loop goes on; only shutdown stops it.</summary>
    private async Task SafelyAsync(string component, Func<Task> step, CancellationToken ct)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OperationsLog.WorkerCrashed(_log, component, Truncate(ex.Message));
        }
    }

    private T TryStore<T>(string operation, Func<T> action, T fallback)
    {
        try
        {
            var value = action();
            if (!Status.StoreOk)
            {
                Update(s => s with { StoreOk = true });
            }

            return value;
        }
        catch (Exception ex)
        {
            OperationsLog.StoreError(_log, operation, Truncate(ex.Message));
            Update(s => s with { StoreOk = false });
            return fallback;
        }
    }

    private void TryStoreAction(string operation, Action action) => TryStore(operation, () => { action(); return true; }, false);

    private void Beat() => Update(s => s with { LastHeartbeatUtc = _time.GetUtcNow() });

    private void RefreshCounts()
    {
        var tracked = _sessions.Count;
        var playing = _sessions.Values.Count(t => t.Allowed && t.LastDto?.NowPlayingItem is not null);
        Update(s => s with { SessionsTracked = tracked, AllowListedPlaying = playing });
    }

    private void Update(Func<OrchestratorStatus, OrchestratorStatus> change) => Volatile.Write(ref _status, change(_status));

    private static int ElapsedMs(long startTimestamp) => (int)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
