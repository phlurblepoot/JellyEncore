using Encore.Config;
using Encore.Jellyfin;
using Encore.Picks;
using Encore.Retention;
using Encore.Store;
using Encore.Tests.EndToEnd;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Encore.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.Retention;

/// <summary>PlayTally (v04 §4.4, §14 T125): PT01-PT12 plus RepeatOne.</summary>
public sealed class PlayTallyTests : IDisposable
{
    private const string S1 = "sess-one";
    private const string S2 = "sess-two";
    private static readonly long RunTicks = TimeSpan.FromMinutes(3).Ticks;
    private static readonly Guid User = Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f");
    private static readonly Guid X = Guid.Parse("aaaaaaaa00000000000000000000000a");
    private static readonly Guid Y = Guid.Parse("bbbbbbbb00000000000000000000000b");
    private static readonly Guid Other = Guid.Parse("cccccccc00000000000000000000000c");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-tally-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
    private readonly EncoreStore _store;
    private readonly FetchStore _fetch;
    private readonly RetentionStore _retention;
    private readonly ListLogger<PlayTally> _log = new();
    private PlayTally _tally;
    private DateTimeOffset _t;

    public PlayTallyTests()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "encore.db");
        _store = EncoreStore.Open(path, _time);
        _fetch = FetchStore.Open(path);
        _retention = RetentionStore.Open(path);
        _tally = NewTally(3);
        _t = _time.GetUtcNow();

        // Encore was already running: the first snapshot (nobody playing) is behind us.
        Snap();
    }

    public void Dispose()
    {
        _tally.Dispose();
        _retention.Dispose();
        _fetch.Dispose();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private PlayTally NewTally(int plays) => new(_fetch, _retention, Options(plays), _time, _log);

    private static EncoreOptions Options(int plays) => new()
    {
        JellyfinUrl = new Uri("http://jellyfin.test:8096"),
        JellyfinApiKey = "test-key-not-real",
        AllowedUsers = ["nathan"],
        DataDir = Path.GetTempPath(),
        RetentionPlays = plays,
    };

    private long Ready(Guid item)
    {
        var id = _fetch.InsertFetched(new NewFetchedFile(
            "job-" + item.ToString("N"), "key-" + item.ToString("N"), "Artist", "Title", "deezer_radio", "soulseek", "run-1", S1, User, _t));
        _fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(Path: "/radio/" + item.ToString("N") + ".flac", JellyfinItemId: item.ToString("N"), ReadyUtc: _t));
        return id;
    }

    /// <summary>Encore sends the item to the session at <paramref name="at"/> (an added_items row).</summary>
    private void Sent(string session, Guid item, DateTimeOffset at) =>
        _store.RecordFire(
            new TriggerRecord(at, session, User, "Finamp", "upcoming", "topup", Other, null, "dedupe-" + Guid.NewGuid().ToString("N"), EncoreStore.SentOutcome, "run-1", 5),
            [new PickedItem(item, "Song", null, "Artist", PickSource.DeezerRadio, PickCopy.Soulseek)]);

    private static SessionInfoDto Playing(string session, Guid item, double fraction, bool paused = false, Guid? user = null, long? runTime = -1, string? entry = null) => new()
    {
        Id = session,
        UserId = user ?? User,
        Client = "Finamp",
        NowPlayingItem = new BaseItemDto { Id = item, Type = "Audio", RunTimeTicks = runTime == -1 ? RunTicks : runTime },
        PlaylistItemId = entry,
        PlayState = new PlayerStateInfo { PositionTicks = (long)(fraction * RunTicks), IsPaused = paused },
    };

    private static SessionInfoDto Idle(string session, Guid? user = null) => new() { Id = session, UserId = user ?? User, Client = "Finamp" };

    private void Snap(params SessionInfoDto[] sessions) => _tally.Observe(new SessionsSnapshot(_t, SnapshotSource.Socket, sessions));

    /// <summary>Plays the item on the session from 0 to the end, one snapshot every 10 s; <paramref name="others"/> stay in every snapshot.</summary>
    private void PlayThrough(string session, Guid item, Guid? user = null, params SessionInfoDto[] others)
    {
        for (var s = 0; s <= 180; s += 10)
        {
            Snap([Playing(session, item, Math.Min(1.0, s / 180.0), user: user), .. others]);
            _t += TimeSpan.FromSeconds(10);
        }
    }

    private void Pass(TimeSpan span) => _t += span;

    private int Plays(long row) => _retention.PlayThroughCount(row);

    private int Logged(string name) => _log.Named(name).Count();

    [Fact]
    public void PT01_a_radio_song_played_through_in_its_run_is_not_counted()
    {
        var row = Ready(X);
        PlayThrough(S1, Other);
        Sent(S1, X, _t - TimeSpan.FromSeconds(30));
        PlayThrough(S1, X);
        Snap(Idle(S1));

        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(0, Plays(row));
        var skipped = Assert.Single(_log.Named("plays.radio_skipped"));
        Assert.Equal(PlayTally.SentInStretch, skipped.Fields["Why"]);
    }

    [Fact]
    public void PT02_the_same_song_after_a_stop_of_more_than_two_minutes_counts()
    {
        var row = Ready(X);
        Snap(Playing(S1, Other, 0));
        Sent(S1, X, _t);
        Pass(TimeSpan.FromSeconds(10));
        PlayThrough(S1, X);
        Snap(Idle(S1));
        Pass(TimeSpan.FromMinutes(3));
        Snap(Idle(S1));
        PlayThrough(S1, X);
        Snap(Idle(S1));

        Assert.Equal(1, _tally.ProcessPending());
        Assert.Equal(1, Plays(row));
        var counted = Assert.Single(_log.Named("plays.counted"));
        Assert.Equal(row, counted.Fields["RowId"]);
        Assert.Equal(1, counted.Fields["Total"]);
        Assert.Equal(S1, counted.Fields["SessionId"]);
        Assert.Equal(User.ToString("N"), counted.Fields["UserId"]);
    }

    [Fact]
    public void A_stop_shorter_than_two_minutes_keeps_the_stretch()
    {
        var row = Ready(X);
        Snap(Playing(S1, Other, 0));
        Sent(S1, X, _t);
        Pass(TimeSpan.FromSeconds(10));
        PlayThrough(S1, X);
        Snap(Idle(S1));
        Pass(TimeSpan.FromSeconds(90));
        PlayThrough(S1, X);
        Snap(Idle(S1));

        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(0, Plays(row));
        Assert.Equal(2, Logged("plays.radio_skipped"));
    }

    [Fact]
    public void PT03_the_song_on_another_session_counts_while_the_radio_runs()
    {
        var row = Ready(X);
        Snap(Playing(S1, Other, 0));
        Sent(S1, X, _t);
        PlayThrough(S2, X, others: Playing(S1, Other, 0.5));
        Snap(Idle(S2), Playing(S1, Other, 0.6));

        Assert.Equal(1, _tally.ProcessPending());
        Assert.Equal(1, Plays(row));
    }

    [Fact]
    public void PT04_a_seek_to_the_end_is_not_counted()
    {
        var row = Ready(X);
        Snap(Playing(S1, X, 0));
        Pass(TimeSpan.FromSeconds(10));
        Snap(Playing(S1, X, 0.95));
        Pass(TimeSpan.FromSeconds(10));
        Snap(Playing(S1, X, 1.0));
        Pass(TimeSpan.FromSeconds(10));
        Snap(Playing(S1, Y, 0));

        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(0, Plays(row));
        Assert.Equal(0, Logged("plays.radio_skipped"));
    }

    [Fact]
    public void PT05_a_skip_at_sixty_percent_is_not_counted()
    {
        var row = Ready(X);
        for (var s = 0; s <= 108; s += 10)
        {
            Snap(Playing(S1, X, s / 180.0));
            Pass(TimeSpan.FromSeconds(10));
        }

        Snap(Playing(S1, Y, 0));

        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(0, Plays(row));
    }

    [Fact]
    public void PT06_a_twenty_minute_pause_does_not_break_the_stretch_and_the_finished_play_counts()
    {
        var row = Ready(X);
        var started = _t;
        for (var s = 0; s <= 70; s += 10)
        {
            Snap(Playing(S1, X, s / 180.0));
            Pass(TimeSpan.FromSeconds(10));
        }

        // Paused for 20 minutes: the song stays the now-playing item.
        for (var i = 0; i < 40; i++)
        {
            Snap(Playing(S1, X, 70 / 180.0, paused: true));
            Pass(TimeSpan.FromSeconds(30));
        }

        for (var s = 80; s <= 180; s += 10)
        {
            Snap(Playing(S1, X, s / 180.0));
            Pass(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(started, _tally.StretchStartOf(S1));
        Snap(Idle(S1));

        Assert.Equal(1, _tally.ProcessPending());
        Assert.Equal(1, Plays(row));
    }

    [Fact]
    public void PT07_after_a_restart_mid_stretch_a_song_sent_two_hours_ago_is_radio()
    {
        var row = Ready(X);
        var inProgress = Ready(Y);
        Sent(S1, X, _t - TimeSpan.FromHours(2));

        // Encore restarts: the new tally's first snapshot finds S1 halfway through Y.
        _tally.Dispose();
        _tally = NewTally(3);
        for (var s = 90; s <= 180; s += 10)
        {
            Snap(Playing(S1, Y, s / 180.0));
            Pass(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(DateTimeOffset.MinValue, _tally.StretchStartOf(S1));
        PlayThrough(S1, X);
        Snap(Idle(S1));

        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(0, Plays(row));
        Assert.Equal(0, Plays(inProgress));
        var whys = _log.Named("plays.radio_skipped").Select(e => ((long)e.Fields["RowId"]!, (string)e.Fields["Why"]!)).ToList();
        Assert.Contains((inProgress, PlayTally.UnknownStart), whys);
        Assert.Contains((row, PlayTally.SentInStretch), whys);
    }

    [Fact]
    public void PT08_an_unknown_run_time_never_counts()
    {
        var row = Ready(X);
        for (var s = 0; s <= 180; s += 10)
        {
            Snap(Playing(S1, X, s / 180.0, runTime: null));
            Pass(TimeSpan.FromSeconds(10));
        }

        Snap(Idle(S1));

        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(0, Plays(row));
    }

    [Fact]
    public void PT09_duplicate_snapshots_and_a_flip_flop_give_one_row()
    {
        var row = Ready(X);
        for (var s = 0; s <= 180; s += 10)
        {
            var snap = Playing(S1, X, s / 180.0);
            Snap(snap);
            Snap(snap);
            Pass(TimeSpan.FromSeconds(10));
        }

        // Finamp's queue-first flip-flop: Y, then X again for a moment, then Y.
        Snap(Playing(S1, Y, 0));
        _t += TimeSpan.FromMilliseconds(20);
        Snap(Playing(S1, X, 1.0));
        _t += TimeSpan.FromMilliseconds(20);
        Snap(Playing(S1, Y, 0));
        Snap(Playing(S1, Y, 0));

        Assert.Equal(1, _tally.ProcessPending());
        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(1, Plays(row));
    }

    [Fact]
    public void PT10_an_untracked_item_records_nothing()
    {
        var row = Ready(X);
        PlayThrough(S1, Y);
        Snap(Idle(S1));

        Assert.Equal(0, _tally.ProcessPending());
        Assert.Equal(0, Plays(row));
        Assert.Equal(0, Logged("plays.counted"));
        Assert.Equal(0, Logged("plays.radio_skipped"));
    }

    [Fact]
    public async Task PT11_a_user_not_on_the_allow_list_is_counted_through_the_orchestrator()
    {
        using var h = new OrchestratorHarness();
        using var fetch = FetchStore.Open(h.DbPath);
        using var retention = RetentionStore.Open(h.DbPath);
        using var tally = new PlayTally(fetch, retention, h.Options, h.Time, _log);
        var id = fetch.InsertFetched(new NewFetchedFile("job-x", "key-x", "Artist", "Title", "deezer_radio", "soulseek", "run-1", S1, User, h.Time.GetUtcNow()));
        fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(Path: "/radio/x.flac", JellyfinItemId: X.ToString("N"), ReadyUtc: h.Time.GetUtcNow()));
        var guest = Guid.Parse("11111111222233334444555555555555");
        var orchestrator = new Encore.Orchestration.Orchestrator(
            h.Jellyfin, new Picker(h.Jellyfin, NullLogger<Picker>.Instance), h.Store, h.Options, h.Channel.Reader, h.Time, h.Log, h.Timing,
            playTally: tally);

        await h.Send(orchestrator);
        for (var s = 0; s <= 180; s += 10)
        {
            await h.Send(orchestrator, Playing(S2, X, s / 180.0, user: guest));
            h.Time.Advance(TimeSpan.FromSeconds(10));
        }

        await h.Send(orchestrator, Idle(S2, guest));

        Assert.Equal(1, tally.ProcessPending());
        Assert.Equal(1, retention.PlayThroughCount(id));
        var counted = Assert.Single(_log.Named("plays.counted"));
        Assert.Equal(guest.ToString("N"), counted.Fields["UserId"]);
    }

    [Fact]
    public void PT12_reaching_the_threshold_keeps_the_row_with_reason_plays()
    {
        _tally.Dispose();
        _tally = NewTally(2);
        Snap();
        var row = Ready(X);
        PlayThrough(S1, X);
        Snap(Idle(S1));
        Assert.Equal(1, _tally.ProcessPending());
        Assert.Null(_fetch.GetTracked(row)!.KeptReason);

        PlayThrough(S2, X);
        Snap(Idle(S1), Idle(S2));
        Assert.Equal(1, _tally.ProcessPending());

        var kept = _fetch.GetTracked(row)!;
        Assert.Equal(PlayTally.KeptReasonPlays, kept.KeptReason);
        Assert.NotNull(kept.KeptUtc);
        Assert.Equal(2, kept.PlayThroughs);
    }

    [Fact]
    public void A_requested_row_keeps_its_reason_at_the_threshold()
    {
        _tally.Dispose();
        _tally = NewTally(1);
        Snap();
        var row = Ready(X);
        _fetch.MarkRequested("key-" + X.ToString("N"));
        var before = _fetch.GetTracked(row)!.KeptReason;
        PlayThrough(S1, X);
        Snap(Idle(S1));

        Assert.Equal(1, _tally.ProcessPending());
        Assert.Equal(before, _fetch.GetTracked(row)!.KeptReason);
    }

    [Fact]
    public void Repeat_one_of_a_user_started_song_counts_each_play()
    {
        var row = Ready(X);
        PlayThrough(S1, X);
        PlayThrough(S1, X);
        Snap(Idle(S1));

        Assert.Equal(2, _tally.ProcessPending());
        Assert.Equal(2, Plays(row));
    }

    [Fact]
    public async Task The_running_service_processes_queued_plays()
    {
        var row = Ready(X);
        await _tally.StartAsync(CancellationToken.None);
        PlayThrough(S1, X);
        Snap(Idle(S1));

        await Wait.Until(() => Plays(row) == 1, "play-through recorded");
        await _tally.StopAsync(CancellationToken.None);
    }
}
