using Encore.Retention;
using Encore.Slskd;
using Encore.Store;
using Encore.Tests.Fetch;
using Encore.Tests.Trigger;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.Retention;

public class SlskdUploadsAndSharesTests : IDisposable
{
    private const string Radio = "/music-radio";
    private const string SlskdRadio = "/music-radio";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-up-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
    private readonly EncoreStore _main;
    private readonly FetchStore _fetch;
    private readonly RetentionStore _ret;
    private readonly FakeSlskd _slskd;

    public SlskdUploadsAndSharesTests()
    {
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "encore.db");
        _main = EncoreStore.Open(db, _time);
        _fetch = FetchStore.Open(db);
        _ret = RetentionStore.Open(db);
        _slskd = new FakeSlskd(_time, _dir);
    }

    public void Dispose()
    {
        _ret.Dispose();
        _fetch.Dispose();
        _main.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private long Row(string job, string path)
    {
        var id = _fetch.InsertFetched(new NewFetchedFile(
            job, "key-" + job, "Artist", "Title " + job, "deezer_radio", "soulseek", "run", "sess", Guid.NewGuid(), Now));
        _fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(Path: path, JellyfinItemId: "item-" + job, ReadyUtc: Now));
        return id;
    }

    private SlskdEvents Events() => new(_slskd, _fetch, _ret, Radio, SlskdRadio, NullLogger<SlskdEvents>.Instance, _time);

    private SharesKeeper Keeper() => new(_slskd, _ret, Radio, SlskdRadio, NullLogger<SharesKeeper>.Instance, _time);

    private static SlskdUploadEvent Up(string local, DateTimeOffset at, Guid? id = null) => new(id ?? Guid.NewGuid(), "peer", local, at);

    private static SlskdShare RadioShare(bool excluded = false, string local = "/music-radio/soulseek", string remote = "soulseek") =>
        new("local", "s1", "soulseek", excluded, local, remote, 1, 10);

    // ---- SlskdEvents ----

    [Fact]
    public async Task Radio_uploads_are_counted_once_per_transfer_and_other_paths_are_ignored()
    {
        var a = Row("a", "/music-radio/soulseek/A/B/01.flac");
        var tid = Guid.NewGuid();
        _slskd.UploadEvents.Add(Up("/music-radio/soulseek/A/B/01.flac", Now, tid));
        _slskd.UploadEvents.Add(Up("/music-radio/soulseek/A/B/01.flac", Now, tid));
        _slskd.UploadEvents.Add(Up("/music/Other/02.flac", Now));
        _slskd.UploadEvents.Add(Up("/music-radio/soulseek/Unknown/03.flac", Now));

        var events = Events();
        Assert.Equal(1, await events.ReadOnceAsync(default));
        Assert.Equal(1, _ret.UploadCount(a));
        Assert.Equal(1, _fetch.GetTracked(a)!.Uploads);
        Assert.Equal(1, events.UnknownPaths);

        Assert.Equal(0, await events.ReadOnceAsync(default));
        Assert.Equal(1, _ret.UploadCount(a));
    }

    [Fact]
    public async Task Events_are_read_across_pages_back_to_the_watermark_minus_an_hour()
    {
        var a = Row("a", "/music-radio/soulseek/A/a.flac");
        _ret.SetEventsWatermark(Now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
        // Newest first: 150 events from the last 30 minutes, then older ones; page 2 reaches 3 h back and ends the read, so page 3 is never asked for.
        for (var i = 0; i < 150; i++)
        {
            _slskd.UploadEvents.Add(Up("/music-radio/soulseek/A/a.flac", Now.AddSeconds(-i * 12)));
        }

        for (var i = 0; i < 200; i++)
        {
            _slskd.UploadEvents.Add(Up("/music-radio/soulseek/A/a.flac", Now.AddHours(-3).AddSeconds(-i)));
        }

        await Events().ReadOnceAsync(default);

        Assert.Equal([(0, 100), (100, 100)], _slskd.EventReads);
        Assert.True(_ret.UploadCount(a) >= 150);
    }

    [Fact]
    public async Task The_first_read_without_a_watermark_stops_at_fifty_pages()
    {
        Row("a", "/music-radio/soulseek/A/a.flac");
        for (var i = 0; i < 6000; i++)
        {
            _slskd.UploadEvents.Add(Up("/music/x/" + i + ".flac", Now.AddSeconds(-i)));
        }

        await Events().ReadOnceAsync(default);

        Assert.Equal(50, _slskd.EventReads.Count);
        Assert.NotNull(_ret.GetEventsWatermark());
    }

    [Fact]
    public async Task A_failed_read_keeps_the_watermark_and_marks_events_unreadable()
    {
        Row("a", "/music-radio/soulseek/A/a.flac");
        _slskd.Down = new SlskdRequestException("events", 500);
        var events = Events();

        Assert.Equal(-1, await events.ReadOnceAsync(default));
        Assert.True(events.Unreadable);
        Assert.Null(_ret.GetEventsWatermark());
    }

    // ---- SharesKeeper: shared detection ----

    [Fact]
    public async Task Radio_is_shared_only_when_a_matching_share_is_not_excluded()
    {
        var keeper = Keeper();
        _slskd.Shares.Add(RadioShare(excluded: true));
        Assert.False(await keeper.CheckSharesAsync(default));

        _slskd.Shares.Clear();
        _slskd.Shares.Add(new SlskdShare("local", "m", "music", false, "/music", "music", 1, 1));
        Assert.False(await keeper.CheckSharesAsync(default));

        _slskd.Shares.Add(RadioShare());
        Assert.True(await keeper.CheckSharesAsync(default));
        Assert.Equal("soulseek", keeper.RadioShare!.RemotePath);

        _slskd.Shares.Clear();
        _slskd.Shares.Add(RadioShare(local: "/music-radio", remote: "music-radio"));
        Assert.True(await keeper.CheckSharesAsync(default));
    }

    // ---- SharesKeeper: rescans ----

    [Fact]
    public async Task Rescan_only_when_dirty_shared_and_idle()
    {
        var keeper = Keeper();
        Assert.Equal(RescanResult.NotDirty, await keeper.RescanIfDueAsync(default));

        keeper.MarkDirty("import");
        Assert.Equal(RescanResult.NotShared, await keeper.RescanIfDueAsync(default));
        Assert.Equal(0, _slskd.RescanCalls);
        Assert.True(keeper.Dirty);

        _slskd.Shares.Add(RadioShare());
        await keeper.CheckSharesAsync(default);
        _slskd.ShareState = new SlskdShareState(true, false, true, false);
        Assert.Equal(RescanResult.Scanning, await keeper.RescanIfDueAsync(default));
        Assert.Equal(0, _slskd.RescanCalls);

        _slskd.ShareState = new SlskdShareState(false, false, true, false);
        Assert.Equal(RescanResult.Started, await keeper.RescanIfDueAsync(default));
        Assert.Equal(1, _slskd.RescanCalls);
        Assert.False(keeper.Dirty);
        Assert.Equal(Now, _ret.GetLastRescan());
    }

    [Fact]
    public async Task A_second_rescan_within_six_hours_is_deferred_and_allowed_after()
    {
        var keeper = Keeper();
        _slskd.Shares.Add(RadioShare());
        keeper.MarkDirty("import");
        Assert.Equal(RescanResult.Started, await keeper.RescanIfDueAsync(default));

        _time.Advance(TimeSpan.FromHours(5));
        keeper.MarkDirty("delete");
        Assert.Equal(RescanResult.TooSoon, await keeper.RescanIfDueAsync(default));
        Assert.Equal(1, _slskd.RescanCalls);
        Assert.True(keeper.Dirty);

        _time.Advance(TimeSpan.FromHours(1.1));
        Assert.Equal(RescanResult.Started, await keeper.RescanIfDueAsync(default));
        Assert.Equal(2, _slskd.RescanCalls);
    }

    [Fact]
    public async Task The_last_rescan_time_survives_a_new_keeper()
    {
        _slskd.Shares.Add(RadioShare());
        var first = Keeper();
        first.MarkDirty("import");
        await first.RescanIfDueAsync(default);

        var second = Keeper();
        second.MarkDirty("import");
        Assert.Equal(RescanResult.TooSoon, await second.RescanIfDueAsync(default));
    }

    // ---- SharesKeeper: active uploads ----

    private async Task<SharesKeeper> SharedKeeper()
    {
        _slskd.Shares.Add(RadioShare());
        var keeper = Keeper();
        await keeper.CheckSharesAsync(default);
        return keeper;
    }

    [Fact]
    public async Task An_active_upload_with_the_exact_remote_path_defers_the_file()
    {
        var keeper = await SharedKeeper();
        _slskd.Uploads.Add(new SlskdUpload(Guid.NewGuid(), "peer1", @"soulseek\A\B\01.flac", "InProgress"));
        _slskd.Uploads.Add(new SlskdUpload(Guid.NewGuid(), "peer2", @"Soulseek\A\B\01.flac", "Queued, Remotely"));

        var check = await keeper.CheckActiveAsync(1, "/music-radio/soulseek/A/B/01.flac", default);

        Assert.True(check.Known);
        Assert.Equal(2, check.PeerCount);
        Assert.True(check.Active);
    }

    [Fact]
    public async Task The_same_relative_path_under_the_music_share_does_not_block()
    {
        var keeper = await SharedKeeper();
        _slskd.Uploads.Add(new SlskdUpload(Guid.NewGuid(), "peer1", @"music\soulseek\A\B\01.flac", "InProgress"));
        _slskd.Uploads.Add(new SlskdUpload(Guid.NewGuid(), "peer1", @"music\A\B\01.flac", "InProgress"));

        var check = await keeper.CheckActiveAsync(1, "/music-radio/soulseek/A/B/01.flac", default);

        Assert.True(check.Known);
        Assert.Equal(0, check.PeerCount);
        Assert.False(check.Active);
    }

    [Fact]
    public async Task A_completed_transfer_does_not_block()
    {
        var keeper = await SharedKeeper();
        _slskd.Uploads.Add(new SlskdUpload(Guid.NewGuid(), "peer1", @"soulseek\A\B\01.flac", "Completed, Succeeded"));

        var check = await keeper.CheckActiveAsync(1, "/music-radio/soulseek/A/B/01.flac", default);

        Assert.Equal(0, check.PeerCount);
        Assert.False(check.Active);
    }

    [Fact]
    public async Task When_the_answer_is_unknown_the_check_says_active()
    {
        var keeper = Keeper();
        var notShared = await keeper.CheckActiveAsync(1, "/music-radio/soulseek/A/B/01.flac", default);
        Assert.False(notShared.Known);
        Assert.True(notShared.Active);

        var shared = await SharedKeeper();
        _slskd.Down = new SlskdRequestException("uploads", 500);
        var failed = await shared.CheckActiveAsync(1, "/music-radio/soulseek/A/B/01.flac", default);
        Assert.False(failed.Known);
        Assert.True(failed.Active);
    }
}
