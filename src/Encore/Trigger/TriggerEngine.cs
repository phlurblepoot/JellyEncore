namespace Encore.Trigger;

/// <summary>
/// Pure trigger logic (design sections 2 and 3). Given a session snapshot, the session's memory and
/// the context, it decides whether Encore should add songs now. No I/O, no clock, no state of its own.
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

        var instance = tracked.Instance;

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

        // 8-9. Fresh queue and the last-song rule.
        var lastAdded = context.Run?.LastAddedItemId;
        if (model == QueueModel.Full)
        {
            var last = instance.EntryQueue[^1];
            if (last.Id != item.Id || !string.Equals(last.PlaylistItemId, session.PlaylistItemId, StringComparison.Ordinal))
            {
                return Skip(tracked, SkipReason.NotLast, model, started);
            }
        }
        else
        {
            var reason = UpcomingRule(instance.EntryQueue, tracked.PrevEntryQueue, item.Id, lastAdded);
            if (reason is { } failed)
            {
                return Skip(tracked, failed, model, started);
            }
        }

        // 10. One fire per instance; a failed fire waits before its retry.
        switch (instance.State)
        {
            case InstanceState.Firing or InstanceState.Fired or InstanceState.GaveUp:
                return new(tracked, TriggerDecision.None.Instance, started);
            case InstanceState.FireFailed when instance.FailedAt is { } failedAt
                && context.Now - failedAt < TriggerLimits.FireRetryDelay:
                return new(tracked, TriggerDecision.None.Instance, started);
        }

        // 11. Durable dedupe, Full model only (Finamp is restart-safe through no_history).
        var key = DedupeKey.Build(session.Id, item.Id, session.PlaylistItemId, instance.EntryQueue, tracked.PrevEntryQueue, model);
        if (model == QueueModel.Full && context.IsDuplicate(key))
        {
            return Skip(tracked, SkipReason.Duplicate, model, started);
        }

        // 12. Rate limits and the run cap.
        if (IsRateLimited(context))
        {
            return Skip(tracked, SkipReason.RateLimited, model, started);
        }

        if ((context.Run?.AddedCount ?? 0) >= TriggerLimits.RunCap)
        {
            return Skip(tracked, SkipReason.RunCap, model, started);
        }

        // 13. Fire. The Firing state is set here, before the orchestrator awaits anything.
        var kind = lastAdded is { } added && added == item.Id ? FireKind.TopUp : FireKind.Start;
        var attempt = instance.Attempts + 1;
        var firing = tracked with
        {
            Instance = instance with { State = InstanceState.Firing, Attempts = attempt, FailedAt = null },
        };
        return new(firing, new TriggerDecision.Fire(kind, model, key, attempt), started);
    }

    /// <summary>The orchestrator finished a fire (sent, dry run, or no picks): no more fires in this instance.</summary>
    public static SessionMemory MarkFired(SessionMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return memory.Instance is { State: InstanceState.Firing } instance
            ? memory with { Instance = instance with { State = InstanceState.Fired } }
            : memory;
    }

    /// <summary>
    /// The orchestrator's fire failed. The instance retries after <see cref="TriggerLimits.FireRetryDelay"/>,
    /// or gives up after <see cref="TriggerLimits.FireAttempts"/> attempts.
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
    /// Finamp's last-song rule (design section 2). Returns null when the current song is the last one,
    /// otherwise the reason it is not.
    /// </summary>
    private static SkipReason? UpcomingRule(
        IReadOnlyList<QueueEntry> entry,
        IReadOnlyList<QueueEntry>? prev,
        Guid current,
        Guid? lastAdded)
    {
        // A non-empty upcoming list is a fresh report of songs still to come.
        if (entry.Count > 0)
        {
            return SkipReason.NotLast;
        }

        // First instance since Encore started: [] cannot be trusted.
        if (prev is null)
        {
            return SkipReason.NoHistory;
        }

        // (a) the previous instance's upcoming list ended with this song.
        if (prev.Count > 0 && prev[^1].Id == current)
        {
            return null;
        }

        // (b) top-up clause: this is the last song Encore added (needed for ENCORE_BATCH=1).
        if (lastAdded is { } added && added == current)
        {
            return null;
        }

        return prev.Count == 0 ? SkipReason.StaleQueue : SkipReason.NotLast;
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

        var sequence = memory.InstanceCount + 1;
        var next = memory with
        {
            Idle = false,
            InstanceCount = sequence,
            PrevEntryQueue = current?.EntryQueue ?? memory.PrevEntryQueue,
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

    private static TriggerOutcome Skip(SessionMemory memory, SkipReason reason, QueueModel model, bool started)
    {
        var instance = memory.Instance!;
        var first = !instance.ReportedReasons.Contains(reason);
        var next = first
            ? memory with { Instance = instance with { ReportedReasons = instance.ReportedReasons.Add(reason) } }
            : memory;
        return new(next, new TriggerDecision.Skip(reason, model, first), started);
    }

    private static bool IsRateLimited(TriggerContext context) =>
        (context.LastSessionFireAt is { } last && context.Now - last < TriggerLimits.SessionCooldown)
        || context.SessionItemsLastHour + context.Batch > TriggerLimits.SessionItemsPerHour
        || context.GlobalItemsLastHour + context.Batch > TriggerLimits.GlobalItemsPerHour;

    private static bool IsOwnSession(TriggerSession session, TriggerContext context) =>
        !string.IsNullOrEmpty(context.OwnDeviceId)
        && string.Equals(session.DeviceId, context.OwnDeviceId, StringComparison.Ordinal);

    private static bool IsRepeatOff(string? repeatMode) =>
        string.IsNullOrEmpty(repeatMode) || string.Equals(repeatMode, RepeatNone, StringComparison.OrdinalIgnoreCase);

    private static bool IsWebPlaylistItem(string? playlistItemId) =>
        playlistItemId is not null && playlistItemId.StartsWith(WebPlaylistItemPrefix, StringComparison.Ordinal);
}
