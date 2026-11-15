using System.Diagnostics;
using System.Threading.Channels;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
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
    private readonly IFetchRequests _fetch;
    private readonly ICandidateSource? _candidates;
    private readonly IOwnedIndex? _owned;
    private readonly IntervalGate _indexNotReady = new(TimeSpan.FromMinutes(1));
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
        OrchestratorTiming? timing = null,
        IFetchRequests? fetch = null,
        ICandidateSource? candidates = null,
        IOwnedIndex? owned = null)
    {
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timing = timing ?? new OrchestratorTiming();
        _fetch = fetch ?? NullFetchRequests.Instance;
        _candidates = candidates;
        _owned = owned;

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
        finally
        {
            foreach (var track in _sessions.Values)
            {
                EndRun(track, "shutdown");
            }
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
            await SafelyAsync("session", () => ProcessSessionAsync(dto, snapshot, ct), ct).ConfigureAwait(false);
        }

        // A snapshot lists every session. One that waited for a delivery verdict and is gone has ended: whatever was
        // queued will not play there, so judge it now instead of keeping it until the tracker is forgotten. Nothing
        // is re-sent to a session that is gone.
        var present = new HashSet<string>(snapshot.Sessions.Select(s => s.Id ?? string.Empty), StringComparer.Ordinal);
        foreach (var track in _sessions.Where(kv => kv.Value.Pending.Count > 0 && !present.Contains(kv.Key)).Select(kv => kv.Value).ToList())
        {
            DropPending(track, "session_ended", _time.GetUtcNow());
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
                EndRun(track, "session_gone");
                _sessions.Remove(id);
            }
        }

        RefreshCounts();
    }

    private async Task ProcessSessionAsync(SessionInfoDto dto, SessionsSnapshot snapshot, CancellationToken ct)
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
        track.LastSnapshotAt = snapshot.ReceivedAt;
        track.LastSnapshotFromPoll = snapshot.Source == SnapshotSource.Poll;
        if (dto.NowPlayingItem is null)
        {
            JudgeStopped(track, session, now);
        }
        else
        {
            // A song that starts after the grace does not take a re-send armed at the stop: settle the stop first.
            JudgeLongStop(track, now);
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
            // A re-send the previous instance never got out (cooldown, caps, repeat) is dropped with it.
            track.Resend = null;
            OnInstanceStarted(track, session, dto, allowed, now);
            ApplyVerdicts(track, session, outcome.AllVerdicts, now);
        }

        if (outcome.Decision is TriggerDecision.Skip skip)
        {
            if (skip.ShouldLog)
            {
                LogSkip(session, skip.Reason);
            }

            if (skip.Reason == SkipReason.RunCap)
            {
                EndRun(track, "run_cap");
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
            // Lazy: the counts are queried only when the engine reaches the rate-limit guard (not for not_last snapshots).
            SessionItemsLastHourSource = counting
                ? () => TryStore("count session", () => _store.CountAddedSince(session.Id, now - TimeSpan.FromHours(1)), fallback)
                : null,
            GlobalItemsLastHourSource = counting
                ? () => TryStore("count global", () => _store.CountAddedSince(null, now - TimeSpan.FromHours(1)), fallback)
                : null,
            Run = track.Run is { } run ? new RunInfo(run.LastAddedItemId, run.AddedCount, run.AddedIds) : null,
            PendingSends = [.. track.Pending.Select(p => p.ToTrigger())],
            SnapshotAt = track.LastSnapshotAt == default ? now : track.LastSnapshotAt,
            SnapshotFromPoll = track.LastSnapshotFromPoll,
            PollSeconds = _options.PollSeconds,
        };
    }

    private SessionRun? LoadRun(string sessionId, DateTimeOffset now)
    {
        var stored = TryStore("latest run", () => _store.LatestRun(sessionId, now - _timing.RunResumeWindow), (StoredRun?)null);
        if (stored is null)
        {
            return null;
        }

        var run = new SessionRun(stored.RunId, NewPlan())
        {
            LastAddedItemId = stored.LastAddedItemId,
            AddedCount = stored.AddedCount,
        };
        run.AddedIds.UnionWith(stored.AddedIds ?? []);
        return run;
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

        if (allowed && session.UserId is { } userId && string.Equals(item.Type, AudioType, StringComparison.Ordinal))
        {
            var artist = Picker.ArtistOf(dto.NowPlayingItem!)?.Id;
            TryStoreAction("record play", () => _store.RecordPlayed(session.Id, userId, item.Id, artist, now));
            TryStoreAction("upsert session", () => _store.UpsertSession(session.Id, userId, session.Client, dto.DeviceName, now));
        }
    }

    /// <summary>
    /// Applies the trigger's delivery verdicts from a new play instance (design section 3, step 7b): confirmed sends
    /// are marked and dropped, deferred ones wait for the next change, missing ones are marked and logged (once), and a
    /// missing send the trigger set up for a re-send is kept in <see cref="SessionTrack.Resend"/> for the fire.
    /// </summary>
    private void ApplyVerdicts(SessionTrack track, TriggerSession session, IReadOnlyList<SendVerdict> verdicts, DateTimeOffset now)
    {
        foreach (var verdict in verdicts)
        {
            var index = track.Pending.FindIndex(p => p.TriggerId == verdict.TriggerId);
            if (index < 0)
            {
                continue;
            }

            var pending = track.Pending[index];
            switch (verdict.Verdict)
            {
                case DeliveryVerdict.Deferred:
                    var age = (long)(track.LastSnapshotAt - pending.DoneAt).TotalMilliseconds;
                    OperationsLog.QueueVerdictDeferred(_log, session.Id, pending.TriggerId, age, track.LastSnapshotFromPoll ? "poll" : "socket");
                    continue;
                case DeliveryVerdict.Confirmed:
                    track.Pending.RemoveAt(index);
                    RecordVerdict(pending, confirmed: true, "next_song", null, string.Empty, now);
                    continue;
                default:
                    track.Pending.RemoveAt(index);
                    var plan = verdict.Resend ?? ResendNames.SkippedUserSongs;
                    if (pending.MissingLoggedAt is null)
                    {
                        RecordVerdict(pending, confirmed: false, "next_song", session.NowPlayingItem?.Id, plan, now);
                    }

                    if (plan == ResendNames.Pending)
                    {
                        track.Resend = track.Resend is { } earlier
                            ? earlier with { Items = [.. earlier.Items, .. pending.Items.Where(i => earlier.Items.All(e => e.Id != i.Id))] }
                            : pending;
                    }

                    continue;
            }
        }

        if (track.Pending.Count == 0)
        {
            track.StoppedSince = null;
        }
    }

    /// <summary>
    /// The session shows no playing item while a send waits for its verdict. In Jellyfin 12.4 that happens only after
    /// the client's PlaybackStopped, and the server stores the stop's queue just before it clears the item, so the
    /// queue in this snapshot is the client's fresh report. A non-empty report is judged at once (jellyfin-web lists
    /// the whole queue; Finamp lists what comes next), unless it may predate the songs (delivery lag). An empty or
    /// missing report is ambiguous: a real stop, or Finamp moving onto the last queued song. That waits
    /// <see cref="OrchestratorTiming.StoppedVerdictGrace"/>; a new song within it is judged by the trigger instead.
    /// Nothing is ever re-sent here: PlayLast to a stopped client starts music or loads a new queue (T28). A send found
    /// missing is re-sent only if a new song starts within the grace.
    /// </summary>
    private void JudgeStopped(SessionTrack track, TriggerSession session, DateTimeOffset now)
    {
        if (track.Pending.Count == 0)
        {
            return;
        }

        track.StoppedSince ??= now;
        if (session.NowPlayingQueue.Count == 0)
        {
            return;
        }

        foreach (var pending in track.Pending.ToList())
        {
            if (pending.MissingLoggedAt is not null)
            {
                continue;
            }

            var verdict = DeliveryJudge.Judge(
                pending.ItemIds, null, session.NowPlayingQueue, pending.DoneAt, track.LastSnapshotAt, track.LastSnapshotFromPoll, _options.PollSeconds);
            var index = track.Pending.IndexOf(pending);
            switch (verdict)
            {
                case DeliveryVerdict.Confirmed:
                    track.Pending.RemoveAt(index);
                    RecordVerdict(pending, confirmed: true, "stopped", null, string.Empty, now);
                    break;
                case DeliveryVerdict.Missing when pending.IsResend:
                    track.Pending.RemoveAt(index);
                    RecordVerdict(pending, confirmed: false, "stopped", null, ResendNames.AlreadyResent, now);
                    break;
                case DeliveryVerdict.Missing:
                    track.Pending[index] = pending with { MissingLoggedAt = now };
                    RecordVerdict(pending, confirmed: false, "stopped", null, ResendNames.Armed, now);
                    break;
                default:
                    var age = (long)(track.LastSnapshotAt - pending.DoneAt).TotalMilliseconds;
                    OperationsLog.QueueVerdictDeferred(_log, session.Id, pending.TriggerId, age, track.LastSnapshotFromPoll ? "poll" : "socket");
                    break;
            }
        }

        if (track.Pending.Count == 0)
        {
            track.StoppedSince = null;
        }
    }

    /// <summary>Runs <see cref="JudgeLongStop"/> on every session. Runs on every tick and snapshot.</summary>
    private void JudgeLongStops(DateTimeOffset now)
    {
        foreach (var track in _sessions.Values.ToList())
        {
            JudgeLongStop(track, now);
        }
    }

    /// <summary>
    /// A session stopped while sends waited, and no song started within the grace: it was a real stop, not a track
    /// change. Sends not judged yet are unconfirmed; sends a stop already judged missing lose their armed re-send.
    /// </summary>
    private void JudgeLongStop(SessionTrack track, DateTimeOffset now)
    {
        if (track.Pending.Count > 0 && track.StoppedSince is { } stopped && now - stopped >= _timing.StoppedVerdictGrace)
        {
            DropPending(track, "stopped", now);
        }
    }

    /// <summary>Drops every pending send without a re-send (a stop past the grace, or the session ended).</summary>
    private void DropPending(SessionTrack track, string judgedAt, DateTimeOffset now)
    {
        foreach (var pending in track.Pending)
        {
            if (pending.MissingLoggedAt is null)
            {
                RecordVerdict(pending, confirmed: false, judgedAt, null, pending.IsResend ? ResendNames.AlreadyResent : ResendNames.SkippedNotPlaying, now);
            }
        }

        track.Pending.Clear();
        track.Resend = null;
        track.StoppedSince = null;
    }

    /// <summary>Records one send's delivery verdict and logs <c>queue.confirmed</c> or <c>queue.unconfirmed</c>.</summary>
    private void RecordVerdict(PendingDelivery pending, bool confirmed, string judgedAt, Guid? nextItemId, string resend, DateTimeOffset now)
    {
        var ids = string.Join(',', pending.ItemIds.Select(id => id.ToString("N")));
        TryStoreAction("delivery", () => _store.MarkDelivery(pending.TriggerId, confirmed, now));
        if (confirmed)
        {
            OperationsLog.QueueConfirmed(_log, pending.RunId, ids);
        }
        else if (judgedAt == "session_ended")
        {
            OperationsLog.QueueUnverified(_log, pending.RunId, ids);
        }
        else
        {
            OperationsLog.QueueUnconfirmed(_log, pending.RunId, ids, judgedAt, nextItemId, pending.IsResend, resend);
            Update(s => s with { Unconfirmed = s.Unconfirmed + 1 });
        }
    }

    private void LogSkip(TriggerSession session, SkipReason reason)
    {
        if (reason is SkipReason.NotLast or SkipReason.NotAllowed or SkipReason.AlreadyQueued or SkipReason.QueueFirst)
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
        if (fire.Kind == FireKind.Resend)
        {
            await ResendAsync(track, session, dto, fire, now, ct).ConfigureAwait(false);
            return;
        }

        var item = dto.NowPlayingItem!;
        var userId = session.UserId!.Value;
        var clock = Stopwatch.GetTimestamp();

        // A retry (attempt 2) keeps the run, so the plan's put-back songs are sent again.
        var run = (fire.Kind == FireKind.TopUp || fire.Attempt > 1) && track.Run is { } current
            ? current
            : new SessionRun(Guid.NewGuid().ToString("N"), NewPlan());
        if (!ReferenceEquals(track.Run, run))
        {
            // A new start ends the old run: its fetches still queued or searching are cancelled (v0.2 design section 10).
            EndRun(track, "replaced");
        }

        track.Run = run;

        OperationsLog.TriggerFired(_log, fire.Kind.ToWire(), fire.Rule.ToWire(), session.Id, fire.Model.ToWire(), item.Id, item.Name, run.RunId, fire.DedupeKey);

        try
        {
            var queueIds = dto.NowPlayingQueue?.Select(q => q.Id).ToList() ?? [];
            // A store error here must not fail the fire: no history just means weaker exclusions and variety.
            var plays = TryStore("recent plays", () => _store.RecentPlays(_options.RecentWindow, session.Id, userId, now), (IReadOnlyList<RecentPlay>)[]);
            var exclusions = RecentExclusions.Build(queueIds, item.Id, plays, _options.RecentWindow, session.Id, userId, now);
            var history = TryStore("session history", () => _store.SessionHistory(session.Id, _timing.HistoryLimit), (IReadOnlyList<HistorySong>)[]);

            PlanHandout handout;
            using (var pickTimeout = new CancellationTokenSource(_timing.PickTimeout, _time))
            using (var pickCts = CancellationTokenSource.CreateLinkedTokenSource(ct, pickTimeout.Token))
            {
                var context = new PlanContext(_picker, userId, item, history, exclusions, run.RunId, session.Id, now);
                var shadow = ShadowAsync(run, context, pickCts.Token);
                try
                {
                    handout = await run.Plan.NextBatchAsync(context, pickCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Counts as a failed fire (retried once), and keeps the loop's heartbeat inside the health window.
                    throw new TimeoutException($"picking songs took longer than {_timing.PickTimeout.TotalSeconds:0} s");
                }
                finally
                {
                    await shadow.ConfigureAwait(false);
                }
            }

            if (handout.PassedOver > 0 || handout.Skipped > 0)
            {
                Update(s => s with { PassedOver = s.PassedOver + handout.PassedOver, Skipped = s.Skipped + handout.Skipped });
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
            catch (JellyfinException ex) when (!ex.OutcomeUnknown)
            {
                // A clear failure: the songs were not queued. Put them back so the retry sends the same ones.
                run.Plan.PutBack(handout.Batch);
                throw;
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
                track.Pending.Add(new PendingDelivery(id, run.RunId, handout.Batch, _time.GetUtcNow(), IsResend: false));
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

    /// <summary>
    /// Sends the songs of a missing send once more (design section 3, fire handling step 7): no picker, no plan. The
    /// answer is handled like any send: 204 is sent, no answer is sent_unconfirmed (never retried), any other failure
    /// is retried 15 s later with the same songs. A sent re-send is a pending send of its own, marked as a re-send.
    /// </summary>
    private async Task ResendAsync(SessionTrack track, TriggerSession session, SessionInfoDto dto, TriggerDecision.Fire fire, DateTimeOffset now, CancellationToken ct)
    {
        var item = dto.NowPlayingItem!;
        var clock = Stopwatch.GetTimestamp();
        var ids = fire.ItemIds ?? [];
        if (track.Resend is not { } original || ids.Count == 0 || _options.DryRun)
        {
            // Nothing to send (the trigger and the tracker disagree, or dry run, which never arms a pending send).
            track.Memory = TriggerEngine.MarkFired(track.Memory!);
            return;
        }

        var items = ids
            .Select(id => original.Items.FirstOrDefault(i => i.Id == id))
            .OfType<PickedItem>()
            .ToList();
        var run = track.Run ??= new SessionRun(original.RunId, NewPlan());
        OperationsLog.TriggerFired(_log, fire.Kind.ToWire(), fire.Rule.ToWire(), session.Id, fire.Model.ToWire(), item.Id, item.Name, run.RunId, fire.DedupeKey);
        var listed = string.Join("; ", items);

        try
        {
            string outcome;
            string? error = null;
            int? status = 204;
            try
            {
                await _jellyfin.PlayLastAsync(session.Id, [.. items.Select(i => i.Id)], ct).ConfigureAwait(false);
                outcome = EncoreStore.SentOutcome;
                Update(s => s with { JellyfinLastOkUtc = now, JellyfinLastError = null });
            }
            catch (JellyfinException ex) when (ex.OutcomeUnknown && !ct.IsCancellationRequested)
            {
                // As for any send: the songs may be queued already, so no retry; the next report decides.
                outcome = EncoreStore.SentNoReplyOutcome;
                status = null;
                error = Truncate(ex.Message);
                var reason = error;
                Update(s => s with { JellyfinLastError = reason });
                OperationsLog.QueueAddNoReply(_log, session.Id, error, listed);
            }

            OperationsLog.QueueResent(_log, session.Id, run.RunId, listed, original.TriggerId, status);
            track.Resend = null;
            var triggerId = RecordOutcome(track, session, run, fire, outcome, now, clock, items, status, error);
            if (triggerId is { } id)
            {
                track.Pending.Add(new PendingDelivery(id, run.RunId, items, _time.GetUtcNow(), IsResend: true));
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
        var counted = items.Count > 0 && (sent || outcome == EncoreStore.DryRunOutcome);
        if (counted)
        {
            run.LastAddedItemId = items[^1].Id;
            run.AddedCount += items.Count;
            run.AddedIds.UnionWith(items.Select(i => i.Id));
        }

        if (sent || outcome == EncoreStore.DryRunOutcome)
        {
            track.LastFireAt = now;
        }

        // The sent (or dry-run) songs go into the instance, so the next instance can tell whether they showed up (guard 9b).
        track.Memory = TriggerEngine.MarkFired(track.Memory!, counted ? [.. items.Select(i => i.Id)] : []);
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

    /// <summary>
    /// The plan of a new run: the one pick pool when fetch mode is live (v0.2 design section 4), else the v0.1 plan
    /// (mode off, and dry_run, where the v0.1 plan decides what is queued).
    /// </summary>
    private IRadioPlan NewPlan() =>
        _options.FetchMode == FetchMode.Live && _candidates is not null && _owned is not null
            ? new PoolPlan(_options.Lookahead, _options.Batch, _fetch, _candidates, _owned, _log, _indexNotReady, _timing.ExternalLookupTimeout)
            : new RadioPlan(_options.Lookahead, _options.Batch);

    /// <summary>
    /// Fetch mode dry_run (v0.2 design section 4.4): beside the v0.1 plan, a throw-away pool from the outside suggestions
    /// is logged and its unowned picks get dry-run requests (search and score only). It never changes what is queued,
    /// and never fails the fire. Keys asked for once in a run are not asked again.
    /// </summary>
    private async Task ShadowAsync(SessionRun run, PlanContext context, CancellationToken ct)
    {
        if (_options.FetchMode != FetchMode.DryRun || _candidates is null || _owned is null)
        {
            return;
        }

        var planned = new HashSet<Guid>(run.Plan.HandedOut);
        if (run.Plan is RadioPlan radio)
        {
            planned.UnionWith(radio.Items.Select(i => i.Id));
        }

        try
        {
            run.ShadowFires++;
            var shadow = new PoolPlan(_options.Lookahead, _options.Batch, _fetch, _candidates, _owned, _log, _indexNotReady,
                _timing.ExternalLookupTimeout, pickIdPrefix: $"{run.RunId}:dry{run.ShadowFires}");
            var requested = await shadow.PlanShadowAsync(context, planned, run.ShadowRequested, ct).ConfigureAwait(false);
            run.ShadowRequested.UnionWith(requested);
        }
        catch (OperationCanceledException)
        {
            // The fire was cancelled or timed out; the v0.1 plan reports that.
        }
        catch (Exception ex)
        {
            PoolLog.PoolExternalFailed(_log, context.Seed.Id, Truncate(ex.Message));
        }
    }

    /// <summary>Ends the session's run on the fetch side (once): its queued and searching fetches are cancelled.</summary>
    private void EndRun(SessionTrack track, string reason)
    {
        if (track.Run is { Ended: false } run)
        {
            run.Ended = true;
            _fetch.EndRun(run.RunId, reason, track.LastSession?.Id);
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
