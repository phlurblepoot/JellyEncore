using System.Threading.Channels;
using Encore.Config;
using Encore.Jellyfin;
using Encore.Logging;
using Encore.Store;
using Encore.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace Encore.Retention;

/// <summary>
/// Counts the play-throughs a listener started themselves (v0.4 design section 4.4, the user's rule of 2026-11-16).
/// Jellyfin's own play count includes radio plays, so Encore keeps its own count from session watching.
/// <para>
/// The orchestrator calls <see cref="Observe"/> for every snapshot, before its allow-list check, so every session is
/// read. <see cref="Observe"/> only updates memory and queues finished plays; the store reads and writes run in
/// <see cref="ProcessPending"/> on this service's own loop, so the orchestrator is never blocked.
/// </para>
/// <list type="bullet">
/// <item>A play instance is the v0.1 one: same NowPlayingItem.Id and PlaylistItemId; a null NowPlayingItem or a vanished session ends it.</item>
/// <item>Played to the end: last position at least 90 % of the run time and listened time at least 50 % of it. Listened time sums
/// the gaps between snapshots while not paused, each capped at 60 s. Unknown run time never counts.</item>
/// <item>Radio (not counted): Encore sent the item to that session during the session's current listening stretch. A stretch
/// breaks after more than 2 min with nothing playing, or when the session disappears; pauses do not break it.</item>
/// <item>Sessions already present at the first snapshot have an unknown stretch start, taken as 6 h before the instance; instances
/// already playing at the first snapshot are never counted (listened time unknown).</item>
/// </list>
/// </summary>
public sealed class PlayTally : BackgroundService
{
    /// <summary>The 8230 reason when Encore sent the song to that session in the current stretch.</summary>
    public const string SentInStretch = "sent_in_stretch";

    /// <summary>The 8230 reason when the song was already playing when Encore started.</summary>
    public const string UnknownStart = "unknown_start";

    /// <summary>The kept reason this tally records when the threshold is reached.</summary>
    public const string KeptReasonPlays = "plays";

    /// <summary>Each gap between two snapshots adds at most this much listened time.</summary>
    public static readonly TimeSpan MaxListenGap = TimeSpan.FromSeconds(60);

    /// <summary>The stretch start assumed when it is unknown (sessions present when Encore started), as the run resume window.</summary>
    public static readonly TimeSpan DefaultUnknownStretch = TimeSpan.FromHours(6);

    private readonly FetchStore _fetch;
    private readonly RetentionStore _retention;
    private readonly int _threshold;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayTally> _log;
    private readonly TimeSpan _unknownStretch;
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly object _observeGate = new();
    private readonly object _processGate = new();
    private readonly Channel<FinishedPlay> _finished = Channel.CreateUnbounded<FinishedPlay>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private bool _seenFirstSnapshot;

    public PlayTally(FetchStore fetch, RetentionStore retention, EncoreOptions options, TimeProvider time, ILogger<PlayTally> log, TimeSpan? unknownStretch = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        ArgumentNullException.ThrowIfNull(options);
        _threshold = options.RetentionPlays;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _unknownStretch = unknownStretch ?? DefaultUnknownStretch;
    }

    /// <summary>Finished plays waiting for <see cref="ProcessPending"/> (for tests and diagnostics).</summary>
    public int PendingCount => _finished.Reader.Count;

    /// <summary>
    /// Reads one snapshot: updates every session's stretch and current play instance in memory, and queues each instance that
    /// ended played to the end. Never touches the store. Called by the orchestrator from its loop.
    /// </summary>
    public void Observe(SessionsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_observeGate)
        {
            var at = snapshot.ReceivedAt;
            var first = !_seenFirstSnapshot;
            _seenFirstSnapshot = true;
            var present = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dto in snapshot.Sessions)
            {
                if (string.IsNullOrEmpty(dto.Id) || dto.UserId == Guid.Empty || !present.Add(dto.Id))
                {
                    continue;
                }

                if (!_sessions.TryGetValue(dto.Id, out var state))
                {
                    state = new SessionState(dto.Id) { StretchUnknown = first };
                    _sessions[dto.Id] = state;
                }

                ObserveSession(state, dto, at, first);
            }

            // A session that vanished ends its instance and its stretch.
            foreach (var gone in _sessions.Keys.Where(id => !present.Contains(id)).ToList())
            {
                End(_sessions[gone], at);
                _sessions.Remove(gone);
            }
        }
    }

    /// <summary>
    /// The current stretch start of a session: null when the session is unknown or idle without a stretch, and
    /// <see cref="DateTimeOffset.MinValue"/> when the stretch began before Encore started (unknown). For tests and diagnostics.
    /// </summary>
    public DateTimeOffset? StretchStartOf(string sessionId)
    {
        lock (_observeGate)
        {
            if (!_sessions.TryGetValue(sessionId, out var state))
            {
                return null;
            }

            return state.StretchUnknown ? DateTimeOffset.MinValue : state.StretchStart;
        }
    }

    /// <summary>
    /// Handles every queued finished play: matches it to a tracked row, skips radio plays (8230), records one
    /// <c>play_throughs</c> row per instance (8220), and marks the row kept with reason <c>plays</c> at the threshold.
    /// Returns how many plays were counted. The running service calls it whenever plays are queued; tests call it directly.
    /// </summary>
    public int ProcessPending()
    {
        lock (_processGate)
        {
            if (!_finished.Reader.TryPeek(out _))
            {
                return 0;
            }

            IReadOnlyDictionary<Guid, TrackedFile> tracked;
            try
            {
                tracked = LoadTracked();
            }
            catch (Exception ex) when (IsStoreFailure(ex))
            {
                // Keep the plays queued; the next call tries again.
                OperationsLog.StoreError(_log, "play tally tracked rows", ex.Message);
                return 0;
            }

            var counted = 0;
            while (_finished.Reader.TryRead(out var play))
            {
                if (!tracked.TryGetValue(play.ItemId, out var row))
                {
                    // Not a song retention decides on: nothing to count.
                    continue;
                }

                try
                {
                    if (Handle(play, row))
                    {
                        counted++;
                    }
                }
                catch (Exception ex) when (IsStoreFailure(ex))
                {
                    OperationsLog.StoreError(_log, "play tally", ex.Message);
                }
            }

            return counted;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _finished.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                ProcessPending();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void ObserveSession(SessionState state, SessionInfoDto dto, DateTimeOffset at, bool first)
    {
        if (dto.NowPlayingItem is not { } item)
        {
            End(state, at);
            state.IdleSince ??= at;
            return;
        }

        // Back from idle: more than the break with nothing playing starts a new, known stretch.
        if (state.IdleSince is { } idle && at - idle > RetentionLimits.StretchBreak)
        {
            state.StretchUnknown = false;
            state.StretchStart = at;
        }

        state.IdleSince = null;
        if (!state.StretchUnknown && state.StretchStart is null)
        {
            state.StretchStart = at;
        }

        var position = dto.PlayState?.PositionTicks;
        var runTime = item.RunTimeTicks;
        var paused = dto.PlayState?.IsPaused ?? false;
        var current = state.Current;
        var isNew = current is null
            || current.ItemId != item.Id
            || !string.Equals(current.PlaylistItemId, dto.PlaylistItemId, StringComparison.Ordinal);

        if (!isNew && current is not null)
        {
            AddListened(current, at);

            // RepeatOne or a jump back to the start of the same entry: the finished play ends, a new one starts.
            if (current.RunTimeTicks is > 0 and var rt && position is { } pos
                && current.LastPositionTicks >= rt * RetentionLimits.PositionFraction
                && current.LastPositionTicks - pos > rt / 2)
            {
                End(state, at);
                isNew = true;
            }
            else
            {
                current.LastPositionTicks = position ?? current.LastPositionTicks;
                current.RunTimeTicks = runTime is > 0 ? runTime : current.RunTimeTicks;
                current.Paused = paused;
                current.LastAt = at;
            }
        }
        else if (current is not null)
        {
            End(state, at);
        }

        if (isNew)
        {
            state.Current = new Instance(item.Id, dto.PlaylistItemId, dto.UserId, at)
            {
                LastPositionTicks = position ?? 0,
                RunTimeTicks = runTime is > 0 ? runTime : null,
                Paused = paused,
                LastAt = at,
                // Already playing when Encore started: how much was listened is unknown.
                InProgressAtStart = first,
            };
        }
    }

    private static void AddListened(Instance instance, DateTimeOffset at)
    {
        if (instance.Paused)
        {
            return;
        }

        var gap = at - instance.LastAt;
        if (gap > TimeSpan.Zero)
        {
            instance.Listened += gap > MaxListenGap ? MaxListenGap : gap;
        }
    }

    /// <summary>Ends the session's current instance (if any) and queues it when it was played to the end.</summary>
    private void End(SessionState state, DateTimeOffset at)
    {
        if (state.Current is not { } instance)
        {
            return;
        }

        state.Current = null;
        AddListened(instance, at);
        if (instance.RunTimeTicks is not { } runTime || runTime <= 0)
        {
            return;
        }

        var reachedEnd = instance.LastPositionTicks >= runTime * RetentionLimits.PositionFraction;
        var listened = instance.Listened.Ticks >= runTime * RetentionLimits.ListenedFraction;
        if (!reachedEnd || !listened)
        {
            return;
        }

        var since = state.StretchUnknown || state.StretchStart is null
            ? instance.FirstSeen - _unknownStretch
            : state.StretchStart.Value;
        _finished.Writer.TryWrite(new FinishedPlay(state.SessionId, instance.UserId, instance.ItemId, instance.FirstSeen, at, since, instance.InProgressAtStart));
    }

    private bool Handle(FinishedPlay play, TrackedFile row)
    {
        var itemId = play.ItemId.ToString("N");
        if (play.InProgressAtStart)
        {
            RetentionLog.PlaysRadioSkipped(_log, row.Id, itemId, play.SessionId, UnknownStart);
            return false;
        }

        if (_retention.WasSentToSession(play.SessionId, play.ItemId, play.StretchSince))
        {
            RetentionLog.PlaysRadioSkipped(_log, row.Id, itemId, play.SessionId, SentInStretch);
            return false;
        }

        // One row per instance: the store is unique on session, item and start minute, so a repeated report adds nothing.
        var started = new DateTimeOffset(play.StartedUtc.UtcDateTime.Ticks - (play.StartedUtc.UtcDateTime.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
        var userId = play.UserId.ToString("N");
        if (!_retention.AddPlayThrough(new NewPlayThrough(row.Id, itemId, userId, play.SessionId, started, play.EndedUtc)))
        {
            return false;
        }

        var total = _retention.PlayThroughCount(row.Id);
        RetentionLog.PlaysCounted(_log, row.Id, itemId, userId, play.SessionId, total);
        if (total >= _threshold && row.KeptReason is null && row.KeptUtc is null)
        {
            // Once kept, always kept (v04 §2): a later rule change cannot take it back.
            _fetch.MarkKept(row.Id, KeptReasonPlays, _time.GetUtcNow());
        }

        return true;
    }

    private Dictionary<Guid, TrackedFile> LoadTracked()
    {
        var map = new Dictionary<Guid, TrackedFile>();
        foreach (var row in _fetch.TrackedForRetention())
        {
            if (row.JellyfinItemId is { } id && Guid.TryParse(id, out var guid))
            {
                map[guid] = row;
            }
        }

        return map;
    }

    private static bool IsStoreFailure(Exception ex) => ex is StoreException or SqliteException or InvalidOperationException or ObjectDisposedException;

    private sealed class SessionState(string sessionId)
    {
        public string SessionId { get; } = sessionId;

        /// <summary>True while the stretch began before Encore saw the session (present at the first snapshot).</summary>
        public bool StretchUnknown { get; set; }

        public DateTimeOffset? StretchStart { get; set; }

        /// <summary>When the session was first seen with nothing playing, since its last playing snapshot.</summary>
        public DateTimeOffset? IdleSince { get; set; }

        public Instance? Current { get; set; }
    }

    private sealed class Instance(Guid itemId, string? playlistItemId, Guid userId, DateTimeOffset firstSeen)
    {
        public Guid ItemId { get; } = itemId;

        public string? PlaylistItemId { get; } = playlistItemId;

        public Guid UserId { get; } = userId;

        public DateTimeOffset FirstSeen { get; } = firstSeen;

        public long LastPositionTicks { get; set; }

        public long? RunTimeTicks { get; set; }

        public bool Paused { get; set; }

        public DateTimeOffset LastAt { get; set; }

        public TimeSpan Listened { get; set; }

        public bool InProgressAtStart { get; init; }
    }

    private sealed record FinishedPlay(
        string SessionId, Guid UserId, Guid ItemId, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, DateTimeOffset StretchSince, bool InProgressAtStart);
}
