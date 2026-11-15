using System.Globalization;

namespace Encore.Trigger;

/// <summary>
/// Pure trigger logic (design sections 2 and 3). Given a session snapshot, the session's memory and
/// the context, it decides whether Encore should add songs now. No I/O, no clock, no state of its own.
/// Since T28 it fires when one song is left after the current one (near-end), keeps the last-song rule as a
/// fallback, and judges pending sends at each new play instance (re-sending a missing one once).
/// </summary>
public static class TriggerEngine
{
    /// <summary>Finamp's <c>Client</c> value (Finamp lib/services/jellyfin_api.dart:702).</summary>
    public const string FinampClient = "Finamp";

    /// <summary>jellyfin-web numbers its entries <c>"playlistItem"+N</c> (playqueuemanager.js:6).</summary>
    public const string WebPlaylistItemPrefix = "playlistItem";

    private const string AudioType = "Audio";
    private const string RepeatNone = "RepeatNone";

    /// <summary>Evaluates one snapshot of one session. The guards run in the design's order; the first failure is the reason.</summary>
    public static TriggerOutcome Evaluate(TriggerSession session, SessionMemory? memory, TriggerContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        // 1. No user, or Encore's own session: ignore silently and keep no tracker.
        if (session.UserId is not { } userId || userId == Guid.Empty || IsOwnSession(session, context))
        {
            return new(null, TriggerDecision.None.Instance, false);
        }

        var start = memory is not null && memory.SessionId == session.Id ? memory : SessionMemory.Create(session.Id);
        var (tracked, started) = Track(start, session);
        var item = session.NowPlayingItem;
        var webShaped = false;
        var model = item is null || tracked.Instance is null ? QueueModel.Unknown : Classify(session, tracked.Instance, out webShaped);

        // 2. Allow-list, by user id. Reported once per session.
        if (!context.AllowedUserIds.Contains(userId))
        {
            var decision = new TriggerDecision.Skip(SkipReason.NotAllowed, model, !tracked.NotAllowedReported);
            return new(tracked with { NotAllowedReported = true }, decision, started);
        }

        // 3. Nothing playing: idle, no decision.
        if (item is null || tracked.Instance is null)
        {
            return new(tracked, TriggerDecision.None.Instance, started);
        }

        // 3b (T75). The ending song's instance took Finamp's queue-first report: it never fires again.
        if (tracked.Instance.QueueFirstSeen)
        {
            return Skip(tracked, SkipReason.QueueFirst, model, started);
        }

        // 7b. Delivery verdicts, on the first snapshot of a new instance only, before guards 4-7.
        IReadOnlyList<SendVerdict>? verdicts = null;
        if (started && context.PendingSends.Count > 0)
        {
            (tracked, verdicts) = JudgePending(tracked, session, model, context);
        }

        var outcome = Decide(session, tracked, started, model, webShaped, context);
        return verdicts is null ? outcome : outcome with { Verdicts = verdicts };
    }

    private static TriggerOutcome Decide(TriggerSession session, SessionMemory tracked, bool started, QueueModel model, bool webShaped, TriggerContext context)
    {
        var item = session.NowPlayingItem!;
        var instance = tracked.Instance!;

        // 4. Audio only (not videos, episodes or audiobooks).
        if (!string.Equals(item.Type, AudioType, StringComparison.Ordinal))
        {
            return Skip(tracked, SkipReason.NotAudio, model, started);
        }

        // 5. The server silently accepts PlayLast for sessions without remote control, so check it here.
        if (session.SupportsRemoteControl != true)
        {
            return Skip(tracked, SkipReason.NoRemoteControl, model, started);
        }

        // 6. Repeat on: the queue never ends. Re-checked every snapshot.
        if (!IsRepeatOff(session.RepeatMode))
        {
            return Skip(tracked, SkipReason.RepeatOn, model, started);
        }

        // 7. Queue model.
        if (model == QueueModel.Unknown)
        {
            return Skip(tracked, webShaped ? SkipReason.StaleQueue : SkipReason.UnknownClient, model, started);
        }

        // 7c. Re-send path: this instance's one fire is the re-send; guards 8, 9, 9b and 11 do not apply.
        if (instance.ResendIds.Count > 0)
        {
            if (Waiting(instance, context))
            {
                return new(tracked, TriggerDecision.None.Instance, started);
            }

            if (Limited(context, instance.ResendIds.Count, applyRunCap: true) is { } limit)
            {
                return Skip(tracked, limit, model, started);
            }

            var original = instance.ResendOf?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var resend = new TriggerDecision.Fire(
                FireKind.Resend, model, $"{session.Id}|resend|{original}", instance.Attempts + 1, FireRule.Resend, instance.ResendIds);
            return Firing(tracked, resend, started);
        }

        // 8-9. Fresh queue and the position rules (near-end, last-song fallback).
        FireRule rule;
        if (model == QueueModel.Full)
        {
            var after = SongsAfter(session, instance, model);
            if (after >= 2)
            {
                return Skip(tracked, SkipReason.NotLast, model, started);
            }

            rule = after == 1 ? FireRule.NearEnd : FireRule.LastSong;
        }
        else
        {
            var (passed, reason) = UpcomingRule(instance.EntryQueue, tracked.PrevEntryQueue, item.Id, context.Run);
            if (reason is { } failed)
            {
                return Skip(tracked, failed, model, started);
            }

            rule = passed;
        }

        // 9b. The previous instance sent songs this report does not show: the delivery check owns that case.
        if (tracked.PrevSentIds.Count > 0
            && !tracked.PrevSentIds.Contains(item.Id)
            && !instance.EntryQueue.Any(e => tracked.PrevSentIds.Contains(e.Id)))
        {
            return Skip(tracked, SkipReason.AlreadyQueued, model, started);
        }

        // 10. One fire per instance; a failed fire waits before its retry.
        if (Waiting(instance, context))
        {
            return new(tracked, TriggerDecision.None.Instance, started);
        }

        // 11. Durable dedupe, Full model only (Finamp is restart-safe through no_history).
        var key = DedupeKey.Build(session.Id, item.Id, session.PlaylistItemId, instance.EntryQueue, tracked.PrevEntryQueue, model);
        if (model == QueueModel.Full && context.IsDuplicate(key))
        {
            return Skip(tracked, SkipReason.Duplicate, model, started);
        }

        // 12. The fire kind (T77): topup when every song still to come (or, with none, the current song) is one the
        // run added, in any order; otherwise start. Finamp may play a PlayLast batch in reverse (sent [A,B], plays B, A).
        var kind = ContinuesRun(session, instance, model, context.Run) ? FireKind.TopUp : FireKind.Start;

        // 13. Rate limits for every fire; the run cap only for a fire that continues the run (a start gets a fresh cap).
        if (Limited(context, context.Batch, kind == FireKind.TopUp) is { } limited)
        {
            return Skip(tracked, limited, model, started);
        }

        return Firing(tracked, new TriggerDecision.Fire(kind, model, key, instance.Attempts + 1, rule), started);
    }

    /// <summary>The orchestrator finished a fire that sent nothing (no picks): no more fires in this instance.</summary>
    public static SessionMemory MarkFired(SessionMemory memory) => MarkFired(memory, []);

    /// <summary>
    /// The orchestrator finished a fire (sent, sent without an answer, dry run, or no picks): no more fires in this
    /// instance. <paramref name="sentIds"/> are the songs it sent or dry-ran (empty for no picks), for guard 9b.
    /// </summary>
    public static SessionMemory MarkFired(SessionMemory memory, IReadOnlyList<Guid> sentIds)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(sentIds);
        return memory.Instance is { State: InstanceState.Firing } instance
            ? memory with { Instance = instance with { State = InstanceState.Fired, SentIds = [.. sentIds] } }
            : memory;
    }

    /// <summary>
    /// The orchestrator's fire failed. The instance retries after <see cref="TriggerLimits.FireRetryDelay"/>,
    /// or gives up after <see cref="TriggerLimits.FireAttempts"/> attempts. A re-send keeps its songs for the retry.
    /// </summary>
    public static (SessionMemory Memory, bool GaveUp) MarkFailed(SessionMemory memory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (memory.Instance is not { State: InstanceState.Firing } instance)
        {
            return (memory, false);
        }

        var gaveUp = instance.Attempts >= TriggerLimits.FireAttempts;
        var next = instance with
        {
            State = gaveUp ? InstanceState.GaveUp : InstanceState.FireFailed,
            FailedAt = now,
        };
        return (memory with { Instance = next }, gaveUp);
    }

    /// <summary>Classifies the instance's queue: Full when the entry queue holds the current entry, Upcoming for Finamp, else Unknown.</summary>
    public static QueueModel Classify(TriggerSession session, PlayInstance instance, out bool webShaped)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(instance);

        webShaped = IsWebPlaylistItem(session.PlaylistItemId)
            || instance.EntryQueue.Any(e => IsWebPlaylistItem(e.PlaylistItemId));

        if (session.PlaylistItemId is { } current
            && instance.EntryQueue.Any(e => e.Id == instance.ItemId && string.Equals(e.PlaylistItemId, current, StringComparison.Ordinal)))
        {
            webShaped = false;
            return QueueModel.Full;
        }

        if (string.Equals(session.Client, FinampClient, StringComparison.OrdinalIgnoreCase))
        {
            webShaped = false;
            return QueueModel.Upcoming;
        }

        return QueueModel.Unknown;
    }

    /// <summary>
    /// Songs still to come after the current one: Full counts the entries after the current entry, Upcoming the
    /// whole list. -1 for Unknown.
    /// </summary>
    private static int SongsAfter(TriggerSession session, PlayInstance instance, QueueModel model)
    {
        if (model == QueueModel.Upcoming)
        {
            return instance.EntryQueue.Count;
        }

        if (model != QueueModel.Full)
        {
            return -1;
        }

        var queue = instance.EntryQueue;
        for (var i = queue.Count - 1; i >= 0; i--)
        {
            if (queue[i].Id == instance.ItemId && string.Equals(queue[i].PlaylistItemId, session.PlaylistItemId, StringComparison.Ordinal))
            {
                return queue.Count - 1 - i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Step 7b: judges every pending send against this new instance's report. A missing send that may be re-sent
    /// (not a re-send itself, at most one song still to come) becomes the instance's <see cref="PlayInstance.ResendIds"/>.
    /// </summary>
    private static (SessionMemory Memory, IReadOnlyList<SendVerdict> Verdicts) JudgePending(
        SessionMemory tracked, TriggerSession session, QueueModel model, TriggerContext context)
    {
        var instance = tracked.Instance!;
        var snapshotAt = context.SnapshotAt ?? context.Now;
        var after = SongsAfter(session, instance, model);
        var verdicts = new List<SendVerdict>(context.PendingSends.Count);
        var resend = new List<Guid>();
        long? resendOf = null;

        foreach (var send in context.PendingSends)
        {
            // A send a stop already judged missing stays missing; this new instance may take its re-send.
            var verdict = send.MissingLoggedAt is not null
                ? DeliveryVerdict.Missing
                : DeliveryJudge.Judge(send.ItemIds, instance.ItemId, instance.EntryQueue, send.DoneAt, snapshotAt, context.SnapshotFromPoll, context.PollSeconds);
            if (verdict != DeliveryVerdict.Missing)
            {
                verdicts.Add(new SendVerdict(send.TriggerId, verdict));
                continue;
            }

            string plan;
            if (send.IsResend)
            {
                plan = ResendNames.AlreadyResent;
            }
            else if (after >= 2)
            {
                plan = ResendNames.SkippedUserSongs;
            }
            else
            {
                plan = ResendNames.Pending;
                resendOf ??= send.TriggerId;
                foreach (var id in send.ItemIds.Where(id => !resend.Contains(id)))
                {
                    resend.Add(id);
                }
            }

            verdicts.Add(new SendVerdict(send.TriggerId, verdict, plan));
        }

        if (resend.Count > 0)
        {
            tracked = tracked with { Instance = instance with { ResendIds = resend, ResendOf = resendOf } };
        }

        return (tracked, verdicts);
    }

    /// <summary>Finamp's position rules (design section 2). Returns the rule that passed, or the reason none did.</summary>
    private static (FireRule Rule, SkipReason? Reason) UpcomingRule(
        IReadOnlyList<QueueEntry> entry,
        IReadOnlyList<QueueEntry>? prev,
        Guid current,
        RunInfo? run)
    {
        // Two or more songs still to come: a fresh report, not near the end.
        if (entry.Count >= 2)
        {
            return (FireRule.NearEnd, SkipReason.NotLast);
        }

        // First instance since Encore started: the list cannot be trusted.
        if (prev is null)
        {
            return (FireRule.NearEnd, SkipReason.NoHistory);
        }

        if (entry.Count == 1)
        {
            // Near-end: we moved here through a known queue, or this is a song the run added (right after a re-send).
            return prev.Any(e => e.Id == current) || run?.Added(current) == true
                ? (FireRule.NearEnd, null)
                : (FireRule.NearEnd, SkipReason.StaleQueue);
        }

        // Fallback (a): the previous instance's upcoming list ended with this song.
        if (prev.Count > 0 && prev[^1].Id == current)
        {
            return (FireRule.LastSong, null);
        }

        // Fallback (b), the top-up clause: this is a song the run added (needed for ENCORE_BATCH=1). Any of the run's
        // songs, not only the last one sent: Finamp may play a batch in reverse, so the first song sent plays last (T77).
        if (run?.Added(current) == true)
        {
            return (FireRule.LastSong, null);
        }

        return (FireRule.LastSong, prev.Count == 0 ? SkipReason.StaleQueue : SkipReason.NotLast);
    }

    private static (SessionMemory Memory, bool Started) Track(SessionMemory memory, TriggerSession session)
    {
        if (session.NowPlayingItem is not { } item)
        {
            // Null snapshots belong to no instance and never change queue memory.
            return (memory.Idle ? memory : memory with { Idle = true }, false);
        }

        var current = memory.Instance;
        var isNew = current is null
            || memory.Idle
            || current.ItemId != item.Id
            || !string.Equals(current.PlaylistItemId, session.PlaylistItemId, StringComparison.Ordinal);
        if (!isNew)
        {
            return (memory, false);
        }

        // T75: Finamp's queue-first report joins the ending song's instance instead of starting one.
        if (current is not null && IsQueueFirst(current, session))
        {
            return (memory with { Idle = false, Instance = current with { QueueFirstSeen = true } }, false);
        }

        var sequence = memory.InstanceCount + 1;
        var next = memory with
        {
            Idle = false,
            InstanceCount = sequence,
            PrevEntryQueue = current?.EntryQueue ?? memory.PrevEntryQueue,
            PrevSentIds = current?.SentIds ?? [],
            Instance = new PlayInstance
            {
                ItemId = item.Id,
                PlaylistItemId = session.PlaylistItemId,
                EntryQueue = [.. session.NowPlayingQueue],
                Sequence = sequence,
            },
        };
        return (next, true);
    }

    /// <summary>
    /// T75: since late 2026 Finamp reports a song change twice, about 20 ms apart: first the OLD song with the NEW
    /// upcoming list (the songs after the next song), then the new song with that list. The first report is the
    /// queue-first report: the same song as the current instance, after a stop (or a source change), whose list
    /// begins with this instance's list minus its first song (the next song). Songs the user added may follow.
    /// It is never a new play: as an instance of its own it would hide the ending song's list from the next song's
    /// rules (near-end needs the previous list to hold the new song; the fallback needs it to end with it), and its
    /// <c>[]</c> at the queue's end would look like a stale list.
    /// A replay of the same song from a stopped player with the same list, or from an empty list, is still a new instance.
    /// </summary>
    private static bool IsQueueFirst(PlayInstance current, TriggerSession session)
    {
        if (current.ItemId != session.NowPlayingItem!.Id
            || !string.Equals(session.Client, FinampClient, StringComparison.OrdinalIgnoreCase)
            || current.EntryQueue.Count == 0
            || session.NowPlayingQueue.Count < current.EntryQueue.Count - 1)
        {
            return false;
        }

        for (var i = 1; i < current.EntryQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i - 1].Id != current.EntryQueue[i].Id)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Guard 12 (T77): the songs still to come after the current one are all songs the run added, or, when none are
    /// left, the current song is. Order does not matter, so a batch played in reverse still continues its run; a song
    /// the user queued anywhere in that tail makes it a start.
    /// </summary>
    private static bool ContinuesRun(TriggerSession session, PlayInstance instance, QueueModel model, RunInfo? run)
    {
        if (run is null)
        {
            return false;
        }

        var queue = instance.EntryQueue;
        var after = SongsAfter(session, instance, model);
        if (after <= 0)
        {
            return run.Added(instance.ItemId);
        }

        for (var i = queue.Count - after; i < queue.Count; i++)
        {
            if (!run.Added(queue[i].Id))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Guard 10: the instance already fired, is firing, gave up, or waits for its retry.</summary>
    private static bool Waiting(PlayInstance instance, TriggerContext context) => instance.State switch
    {
        InstanceState.Firing or InstanceState.Fired or InstanceState.GaveUp => true,
        InstanceState.FireFailed => instance.FailedAt is { } failedAt && context.Now - failedAt < TriggerLimits.FireRetryDelay,
        _ => false,
    };

    private static TriggerOutcome Firing(SessionMemory tracked, TriggerDecision.Fire fire, bool started)
    {
        // The Firing state is set here, before the orchestrator awaits anything.
        var firing = tracked with
        {
            Instance = tracked.Instance! with { State = InstanceState.Firing, Attempts = fire.Attempt, FailedAt = null },
        };
        return new(firing, fire, started);
    }

    private static TriggerOutcome Skip(SessionMemory memory, SkipReason reason, QueueModel model, bool started)
    {
        var instance = memory.Instance!;
        var first = !instance.ReportedReasons.Contains(reason);
        var next = first
            ? memory with { Instance = instance with { ReportedReasons = instance.ReportedReasons.Add(reason) } }
            : memory;
        return new(next, new TriggerDecision.Skip(reason, model, first), started);
    }

    /// <summary>Guard 12/13 for <paramref name="count"/> songs: cooldown and hourly limits, then the run cap when <paramref name="applyRunCap"/>.</summary>
    private static SkipReason? Limited(TriggerContext context, int count, bool applyRunCap)
    {
        if ((context.LastSessionFireAt is { } last && context.Now - last < TriggerLimits.SessionCooldown)
            || context.SessionItemsInLastHour() + count > TriggerLimits.SessionItemsPerHour
            || context.GlobalItemsInLastHour() + count > TriggerLimits.GlobalItemsPerHour)
        {
            return SkipReason.RateLimited;
        }

        return applyRunCap && (context.Run?.AddedCount ?? 0) >= TriggerLimits.RunCap ? SkipReason.RunCap : null;
    }

    private static bool IsOwnSession(TriggerSession session, TriggerContext context) =>
        !string.IsNullOrEmpty(context.OwnDeviceId)
        && string.Equals(session.DeviceId, context.OwnDeviceId, StringComparison.Ordinal);

    private static bool IsRepeatOff(string? repeatMode) =>
        string.IsNullOrEmpty(repeatMode) || string.Equals(repeatMode, RepeatNone, StringComparison.OrdinalIgnoreCase);

    private static bool IsWebPlaylistItem(string? playlistItemId) =>
        playlistItemId is not null && playlistItemId.StartsWith(WebPlaylistItemPrefix, StringComparison.Ordinal);
}
