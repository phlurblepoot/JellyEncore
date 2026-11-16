using System.Security.Cryptography;
using Encore.Config;
using Encore.Fetch.Import;
using Encore.Jellyfin;
using Encore.Picks;
using Encore.Retention;
using Encore.Slskd;
using Encore.Store;
using Encore.Tests.Fetch;
using Encore.Tests.Retention;
using Encore.Tests.Watch;
using Encore.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// The retention parts together (v04 section 14 T132): the real pass, deleter, promoter, slskd events reader, play tally and worker
/// over real stores and real folders, with the fake Jellyfin, slskd and Lidarr. Nothing is stubbed between the worker and the disk.
/// </summary>
public sealed class RetentionTests : IDisposable
{
    private const string SlskdRadio = "/music-radio";
    private static readonly Guid Ann = PromoterRig.Ann;
    private static readonly Guid Listener = Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f");

    private readonly PromoterRig _rig = new();
    private readonly FakeSlskd _slskd;
    private DateTimeOffset _t;
    private int _counter;

    public RetentionTests()
    {
        _slskd = new FakeSlskd(_rig.Time, _rig.Dir);
        _t = _rig.Time.GetUtcNow();
    }

    public void Dispose()
    {
        _rig.Dispose();
    }

    private EncoreOptions Options(RetentionMode mode) => _rig.Options with { RetentionMode = mode };

    private RetentionWorker Worker(RetentionMode mode, PlayTally? tally = null)
    {
        var options = Options(mode);
        var signals = new RetentionSignals(_rig.Jellyfin, _rig.Jellyfin, options, _rig.Time, NullLogger<RetentionSignals>.Instance);
        var pass = new RetentionPass(_rig.Fetch, _rig.Store, signals, options, _rig.Time, NullLogger<RetentionPass>.Instance, PromotionPaths.From(options).ReportTarget(_rig.Fetch));
        var deleter = new Deleter(_rig.Fetch, _rig.Store, signals, _rig.Jellyfin, _rig.Owned, _rig.Shares, _rig.Shares, options, _rig.Time, NullLogger<Deleter>.Instance);
        var promoter = new Promoter(
            _rig.Fetch, _rig.Store, _rig.Requests, _rig.Jellyfin, _rig.Jellyfin, _rig.Owned, new LidarrGuard(_rig.LidarrApi, _rig.Lidarr),
            _rig.Free, _rig.Shares, _rig.Shares, options, _rig.Time, _rig.Log);
        lastPass = pass;
        return new RetentionWorker(RetentionSteps.From(pass, deleter, promoter), options, _rig.Time, NullLogger<RetentionWorker>.Instance);
    }

    private RetentionPass? lastPass;

    /// <summary>A ready song with a real radio file and its Jellyfin item. No keep signal is set.</summary>
    private Song Add(string title, double ageDays = 1, string artist = "Artist", string album = "Album (2020)")
    {
        var item = Guid.NewGuid();
        var file = $"{(++_counter):00} - {title}.flac";
        var path = Path.Combine(_rig.Radio, SafeNames.SoulseekFolder, artist, album, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "audio " + title + " " + Guid.NewGuid().ToString("N"));
        var jellyfin = $"{PromoterJellyfin.RadioPrefix}/{SafeNames.SoulseekFolder}/{artist}/{album}/{file}";
        var at = _rig.Time.GetUtcNow() - TimeSpan.FromDays(ageDays);
        var id = _rig.Fetch.InsertFetched(new NewFetchedFile("job-" + title, "key-" + title, artist, title, "deezer_radio", "soulseek", "run-1", "s1", Ann, at));
        _rig.Fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(Path: path, JellyfinPath: jellyfin, JellyfinItemId: item.ToString("N"), ReadyUtc: at));
        _rig.Jellyfin.Items[item] = jellyfin;
        return new Song(id, item, path, "", $"{artist}/{album}");
    }

    private string Everything() => string.Join("|", new[] { _rig.Radio, _rig.Library }
        .SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(f => f + (File.Exists(f) ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) : "/")))
        .Order(StringComparer.Ordinal));

    private string State(Song s) => _rig.Fetch.GetTracked(s.Id)!.State;

    private static string Trash(string radio) => Path.Combine(radio, ".encore-trash");

    // ---- dry run ----

    [Fact]
    public async Task E01_a_dry_run_over_two_passes_changes_nothing_on_disk_or_in_jellyfin()
    {
        var old = Add("Old", ageDays: 45);
        var fav = Add("Fav", ageDays: 45);
        var asked = Add("Asked", ageDays: 45);
        var young = Add("Young");
        _rig.Fetch.MarkRequested("key-Asked");
        _rig.Jellyfin.Favorites[Ann] = [fav.Item];
        var worker = Worker(RetentionMode.DryRun);
        var before = Everything();

        var first = await worker.RunOnceAsync();
        _rig.Time.Advance(TimeSpan.FromDays(1));
        var second = await worker.RunOnceAsync();

        Assert.False(first.Crashed);
        Assert.False(second.Crashed);
        Assert.Equal(before, Everything());
        Assert.Empty(_rig.Jellyfin.MediaUpdated);
        Assert.Empty(_rig.Jellyfin.Writes);
        Assert.Empty(_rig.Jellyfin.FavoriteMarks);
        Assert.Empty(_rig.Owned.Removed);
        Assert.Empty(_rig.Shares.Dirty);
        Assert.False(Directory.Exists(Trash(_rig.Radio)));
        Assert.All(new[] { old, fav, asked, young }, s => Assert.Equal(FetchState.Ready, State(s)));
        Assert.Empty(first.Promotions);
        Assert.Null(first.Deleted);
        Assert.Empty(second.Promotions);

        // It still decided: the same plan twice, every record marked dry.
        foreach (var run in new[] { first, second })
        {
            var rows = _rig.Store.Decisions(run.Pass!.PassId);
            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => Assert.True(r.DryRun));
            Assert.Equal("delete", rows.Single(r => r.FetchedFileId == old.Id).Decision);
            Assert.Equal("promote", rows.Single(r => r.FetchedFileId == fav.Id).Decision);
            Assert.Equal("promote", rows.Single(r => r.FetchedFileId == asked.Id).Decision);
            Assert.Equal("wait", rows.Single(r => r.FetchedFileId == young.Id).Decision);
        }
    }

    // ---- live ----

    [Fact]
    public async Task E02_a_live_pass_deletes_the_old_song_and_promotes_the_favorite_and_the_requested_one()
    {
        var old = Add("Old", ageDays: 45);
        var fav = Add("Fav", ageDays: 45, artist: "Loved", album: "Hits (2019)");
        var asked = Add("Asked", ageDays: 45, artist: "Wanted", album: "Pick (2021)");
        var young = Add("Young", artist: "Fresh", album: "New (2026)");
        _rig.Fetch.MarkRequested("key-Asked");
        _rig.Jellyfin.Favorites[Ann] = [fav.Item];
        _rig.Jellyfin.UserData[(Ann, fav.Item)] = new UserItemData(true, 2);
        var worker = Worker(RetentionMode.Live);

        var run = await worker.RunOnceAsync();

        Assert.False(run.Crashed);
        Assert.False(run.DeletesSkipped);
        Assert.Equal(2, run.Promotions.Count(p => p.Outcome == PromotionOutcome.Done));
        Assert.Equal(1, run.Deleted!.Deleted);

        // The old song went to the dated trash and Jellyfin forgot it.
        Assert.False(File.Exists(old.Path));
        Assert.Single(Directory.EnumerateFiles(Trash(_rig.Radio), "*", SearchOption.AllDirectories));
        var oldRow = _rig.Fetch.GetTracked(old.Id)!;
        Assert.Equal((FetchState.Deleted, "age"), (oldRow.State, oldRow.DeleteReason));
        Assert.DoesNotContain(old.Item, _rig.Jellyfin.Items.Keys);
        Assert.Contains(old.Item, _rig.Owned.Removed);

        // The favorite and the requested song moved into the library; the radio copies are gone.
        foreach (var song in new[] { fav, asked })
        {
            var row = _rig.Fetch.GetTracked(song.Id)!;
            Assert.Equal(FetchState.Promoted, row.State);
            Assert.StartsWith(_rig.Library, row.Path);
            Assert.True(File.Exists(row.Path));
            Assert.False(File.Exists(song.Path));
            Assert.DoesNotContain(song.Item, _rig.Jellyfin.Items.Keys);
            Assert.StartsWith(PromoterJellyfin.LibraryPrefix, row.JellyfinPath);
        }

        var libraryFiles = Directory.EnumerateFiles(_rig.Library, "*", SearchOption.AllDirectories).Count(f => !f.Contains(".encore-incoming"));
        Assert.Equal(2, libraryFiles);
        Assert.True(_rig.Jellyfin.UserData[(Ann, _rig.Jellyfin.ItemAt(_rig.Fetch.GetTracked(fav.Id)!.JellyfinPath!)!.Value)].IsFavorite);

        // A young song with no signal stays where it is.
        Assert.Equal(FetchState.Ready, State(young));
        Assert.True(File.Exists(young.Path));
        Assert.Contains(young.Item, _rig.Jellyfin.Items.Keys);
    }

    [Fact]
    public async Task E03_a_requested_song_is_promoted_and_never_deleted_however_old()
    {
        var asked = Add("Asked", ageDays: 400, artist: "Wanted", album: "Pick (2021)");
        _rig.Fetch.MarkRequested("key-Asked");
        var worker = Worker(RetentionMode.Live);

        var first = await worker.RunOnceAsync();
        _rig.Time.Advance(TimeSpan.FromDays(2));
        var second = await worker.RunOnceAsync();

        Assert.Equal(PromotionOutcome.Done, Assert.Single(first.Promotions).Outcome);
        Assert.Equal(0, first.Deleted!.Deleted);
        var row = _rig.Fetch.GetTracked(asked.Id)!;
        Assert.Equal(FetchState.Promoted, row.State);
        Assert.Null(row.DeleteReason);
        Assert.True(File.Exists(row.Path));
        Assert.False(Directory.Exists(Trash(_rig.Radio)));

        // The promoted row is no longer a retention row: the next pass leaves it alone.
        Assert.Empty(second.Pass!.Rows);
        Assert.True(File.Exists(row.Path));
    }

    // ---- uploads ----

    private static SlskdUploadEvent Up(string local, DateTimeOffset at) => new(Guid.NewGuid(), "peer", local, at);

    private string SlskdPath(Song s) => SlskdRadio + "/" + Path.GetRelativePath(_rig.Radio, s.Path).Replace('\\', '/');

    [Fact]
    public async Task E04_three_uploads_make_a_young_song_due_and_two_do_not()
    {
        var three = Add("Three");
        var two = Add("Two");
        for (var i = 0; i < 3; i++)
        {
            _slskd.UploadEvents.Add(Up(SlskdPath(three), _t.AddSeconds(-i)));
        }

        for (var i = 0; i < 2; i++)
        {
            _slskd.UploadEvents.Add(Up(SlskdPath(two), _t.AddSeconds(-10 - i)));
        }

        var events = new SlskdEvents(_slskd, _rig.Fetch, _rig.Store, _rig.Radio, SlskdRadio, NullLogger<SlskdEvents>.Instance, _rig.Time);
        Assert.Equal(5, await events.ReadOnceAsync(default));
        Assert.Equal(3, _rig.Fetch.GetTracked(three.Id)!.Uploads);
        var worker = Worker(RetentionMode.Live);

        var run = await worker.RunOnceAsync();

        Assert.Equal(1, run.Deleted!.Deleted);
        Assert.Equal((FetchState.Deleted, "uploads"), (_rig.Fetch.GetTracked(three.Id)!.State, _rig.Fetch.GetTracked(three.Id)!.DeleteReason));
        Assert.False(File.Exists(three.Path));
        Assert.Equal(FetchState.Ready, State(two));
        Assert.True(File.Exists(two.Path));
    }

    // ---- plays ----

    private static SessionInfoDto Playing(string session, Guid item, double fraction) => new()
    {
        Id = session,
        UserId = Listener,
        Client = "Finamp",
        NowPlayingItem = new BaseItemDto { Id = item, Type = "Audio", RunTimeTicks = TimeSpan.FromMinutes(3).Ticks },
        PlayState = new PlayerStateInfo { PositionTicks = (long)(fraction * TimeSpan.FromMinutes(3).Ticks), IsPaused = false },
    };

    private static SessionInfoDto Idle(string session) => new() { Id = session, UserId = Listener, Client = "Finamp" };

    private void Snap(PlayTally tally, params SessionInfoDto[] sessions) => tally.Observe(new SessionsSnapshot(_t, SnapshotSource.Socket, sessions));

    private void PlayThrough(PlayTally tally, string session, Guid item)
    {
        for (var s = 0; s <= 180; s += 10)
        {
            Snap(tally, Playing(session, item, Math.Min(1.0, s / 180.0)));
            _t += TimeSpan.FromSeconds(10);
        }

        Snap(tally, Idle(session));
    }

    [Fact]
    public async Task E05_radio_plays_never_keep_a_song_but_user_started_plays_do()
    {
        var radioOld = Add("RadioOld", ageDays: 45);
        var radioYoung = Add("RadioYoung");
        var chosen = Add("Chosen", artist: "Mine", album: "Own (2018)");
        var options = Options(RetentionMode.Live);
        using var tally = new PlayTally(_rig.Fetch, _rig.Store, options, _rig.Time, NullLogger<PlayTally>.Instance);
        Snap(tally);

        // Encore sent both radio songs to the session; they play three times inside the same stretch (stops under two minutes).
        var other = Guid.NewGuid();
        Snap(tally, Playing("s1", other, 0));
        foreach (var song in new[] { radioOld, radioYoung })
        {
            // A new stretch starts with another song playing, then Encore sends the radio song to the session.
            Snap(tally, Playing("s1", other, 0.1));
            _rig.Main.RecordFire(
                new TriggerRecord(_t, "s1", Listener, "Finamp", "upcoming", "topup", other, null, "dedupe-" + Guid.NewGuid().ToString("N"), EncoreStore.SentOutcome, "run-1", 5),
                [new PickedItem(song.Item, "Song", null, "Artist", PickSource.DeezerRadio, PickCopy.Soulseek)]);
            _t += TimeSpan.FromSeconds(10);
            for (var i = 0; i < 3; i++)
            {
                PlayThrough(tally, "s1", song.Item);
                _t += TimeSpan.FromSeconds(30);
            }

            _t += TimeSpan.FromMinutes(5);
            Snap(tally, Idle("s1"));
        }

        // The user picks this one three times, minutes apart, with nothing sent by Encore.
        for (var i = 0; i < 3; i++)
        {
            PlayThrough(tally, "s2", chosen.Item);
            _t += TimeSpan.FromMinutes(5);
            Snap(tally, Idle("s2"));
        }

        tally.ProcessPending();
        Assert.Equal(0, _rig.Store.PlayThroughCount(radioOld.Id));
        Assert.Equal(0, _rig.Store.PlayThroughCount(radioYoung.Id));
        Assert.Equal(3, _rig.Store.PlayThroughCount(chosen.Id));

        // The deleter leaves a song Encore sent to a session in the last 3 hours; let that window pass.
        _rig.Time.Advance(TimeSpan.FromHours(4));
        var run = await Worker(RetentionMode.Live, tally).RunOnceAsync();

        // The old radio song is deleted by age whatever its plays; the young one just waits; the chosen one is kept and promoted.
        Assert.Equal((FetchState.Deleted, "age"), (State(radioOld), _rig.Fetch.GetTracked(radioOld.Id)!.DeleteReason));
        Assert.Equal(FetchState.Ready, State(radioYoung));
        Assert.Null(_rig.Fetch.GetTracked(radioYoung.Id)!.KeptReason);
        Assert.Equal("plays", _rig.Fetch.GetTracked(chosen.Id)!.KeptReason);
        Assert.Equal(FetchState.Promoted, State(chosen));
        Assert.Equal("wait", run.Pass!.Rows.Single(r => r.Row.Id == radioYoung.Id).Decision);
    }

    // ---- failed signal read ----

    [Fact]
    public async Task E06_a_failed_signal_read_deletes_nothing_even_for_an_old_song()
    {
        var old = Add("Old", ageDays: 90);
        var worker = Worker(RetentionMode.Live);
        _rig.Jellyfin.FailFavorites = true;

        var run = await worker.RunOnceAsync();

        Assert.True(run.DeletesSkipped);
        Assert.Null(run.Deleted);
        Assert.Equal(FetchState.Ready, State(old));
        Assert.True(File.Exists(old.Path));
    }

    // ---- mode off ----

    [Fact]
    public async Task E07_mode_off_does_nothing_at_all()
    {
        var old = Add("Old", ageDays: 90);
        var asked = Add("Asked", ageDays: 90);
        _rig.Fetch.MarkRequested("key-Asked");
        var worker = Worker(RetentionMode.Off);
        var before = Everything();

        var run = await worker.RunOnceAsync();

        Assert.Null(run.Pass);
        Assert.Null(run.Deleted);
        Assert.Empty(run.Promotions);
        Assert.Equal(before, Everything());
        Assert.Empty(_rig.Jellyfin.MediaUpdated);
        Assert.Empty(_rig.Jellyfin.Writes);
        Assert.Equal(FetchState.Ready, State(old));
        Assert.Equal(FetchState.Ready, State(asked));
        Assert.Null(lastPass!.Latest);
    }
}

