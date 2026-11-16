using Encore.Config;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Picks;
using Encore.Retention;
using Encore.Store;
using Encore.Tests.EndToEnd;
using Encore.Tests.Picks;
using Encore.Tests.Watch;

namespace Encore.Tests.Retention;

/// <summary>Deleter (v04 section 6, section 14 T128).</summary>
public sealed class DeleterTests : IDisposable
{
    private readonly PassRig _rig = new(RetentionMode.Live);
    private readonly FakeUploads _uploads = new();
    private readonly FakeDirty _dirty = new();
    private readonly TestOwned _owned = new();
    private readonly ListLogger<Deleter> _log = new();

    public void Dispose() => _rig.Dispose();

    private Deleter Make(Func<EncoreOptions, EncoreOptions>? tweak = null)
    {
        var options = tweak?.Invoke(_rig.Pass.Options) ?? _rig.Pass.Options;
        var signals = new RetentionSignals(_rig.Jellyfin, _rig.Jellyfin, options, _rig.Time, new ListLogger<RetentionSignals>());
        return new Deleter(_rig.Fetch, _rig.Retention, signals, _rig.Jellyfin, _owned, _uploads, _dirty, options, _rig.Time, _log);
    }

    private async Task<DeleteResult> Act(Deleter? deleter = null, PassResult? pass = null)
    {
        pass ??= await _rig.Pass.RunAsync();
        return await (deleter ?? Make()).RunAsync(pass);
    }

    private (long Id, Guid Item, string Path) Old(string title, string artist = "Artist", double ageDays = 45)
    {
        var song = _rig.Ready(title, ageDays, artist);
        TrackKey.TryCreate(artist, title, out var key);
        _owned.Add(new OwnedEntry(song.Item, key, title, null, artist, 180, song.Path, null, true));
        return song;
    }

    private static string Trash(PassRig rig, string day, long id) => Path.Combine(rig.Radio, ".encore-trash", day, id + ".flac");

    [Fact]
    public async Task DL01_an_old_song_goes_to_dated_trash_the_row_is_deleted_and_the_index_forgets_it()
    {
        var song = Old("Gone");

        var result = await Act();

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(song.Path));
        Assert.True(File.Exists(Trash(_rig, "2026-11-20", song.Id)));
        var row = _rig.Fetch.GetTracked(song.Id)!;
        Assert.Equal((FetchState.Deleted, "age"), (row.State, row.DeleteReason));
        Assert.Null(row.KeptReason);
        Assert.Null(_owned.KeyOf(song.Item));
        Assert.Single(_log.Named("delete.done"));
    }

    [Fact]
    public async Task DL02_empty_album_and_artist_folders_go_but_soulseek_stays_and_a_folder_with_other_files_stays()
    {
        var one = Old("Solo", "Alone");
        var a = Old("A", "Pair");
        var b = Old("B", "Pair", ageDays: 2);
        var cover = Path.Combine(Path.GetDirectoryName(a.Path)!, "cover.jpg");
        File.WriteAllText(cover, "img");

        await Act();

        Assert.False(Directory.Exists(Path.Combine(_rig.Radio, "soulseek", "Alone")));
        Assert.True(Directory.Exists(Path.Combine(_rig.Radio, "soulseek", "Pair", "Album")));
        Assert.True(File.Exists(b.Path));
        Assert.True(File.Exists(cover));
        Assert.True(Directory.Exists(Path.Combine(_rig.Radio, "soulseek")));
        Assert.False(File.Exists(one.Path));
    }

    [Fact]
    public async Task DL03_the_last_song_of_all_folders_leaves_soulseek_itself()
    {
        Old("Only");

        await Act();

        Assert.True(Directory.Exists(Path.Combine(_rig.Radio, "soulseek")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_rig.Radio, "soulseek")));
    }

    [Fact]
    public async Task DL04_a_favorite_that_appeared_after_the_plan_keeps_the_song()
    {
        var song = Old("Fav");
        var pass = await _rig.Pass.RunAsync();
        Assert.Equal(RetentionDecision.Delete, pass.Rows.Single().Decision);
        _rig.Jellyfin.Favorites[PassRig.Ann] = [new BaseItemDto { Id = song.Item }];

        var result = await Act(pass: pass);

        Assert.Equal((0, 1), (result.Deleted, result.Kept));
        Assert.True(File.Exists(song.Path));
        Assert.Equal("favorite", _rig.Fetch.GetTracked(song.Id)!.KeptReason);
        Assert.Empty(_dirty.Reasons);
    }

    [Fact]
    public async Task DL05_a_requested_row_is_never_deleted_even_with_a_forced_decision()
    {
        var song = Old("Wanted");
        var pass = await _rig.Pass.RunAsync();
        _rig.Fetch.MarkRequested("key-Wanted");
        Assert.True(_rig.Fetch.GetTracked(song.Id)!.Requested);

        var result = await Act(pass: pass);

        Assert.Equal((0, 1), (result.Deleted, result.Kept));
        Assert.True(File.Exists(song.Path));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(song.Id)!.State);
    }

    [Fact]
    public async Task DL06_a_kept_row_is_never_deleted_even_with_a_forced_decision()
    {
        var song = Old("Kept");
        var pass = await _rig.Pass.RunAsync();
        _rig.Fetch.MarkKept(song.Id, "plays", _rig.Time.GetUtcNow());

        var result = await Act(pass: pass);

        Assert.Equal(1, result.Kept);
        Assert.True(File.Exists(song.Path));
    }

    [Fact]
    public async Task DL07_a_song_that_is_now_playing_is_skipped()
    {
        var song = Old("Playing");
        _rig.Jellyfin.Sessions = [new SessionInfoDto { Id = "s1", NowPlayingItem = new BaseItemDto { Id = song.Item } }];

        var result = await Act();

        Assert.Equal((0, 1), (result.Deleted, result.Skipped));
        Assert.True(File.Exists(song.Path));
        Assert.Contains("now_playing", _log.Named("delete.skipped").Single().Message);
    }

    [Fact]
    public async Task DL08_a_song_in_any_session_queue_is_skipped()
    {
        var song = Old("Queued");
        _rig.Jellyfin.Sessions = [new SessionInfoDto { Id = "s1", NowPlayingQueue = [new QueueItem { Id = song.Item }] }];

        var result = await Act();

        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(song.Path));
        Assert.Contains("queued", _log.Named("delete.skipped").Single().Message);
    }

    [Fact]
    public async Task DL09_a_song_encore_added_in_the_last_3_hours_is_skipped_and_an_older_add_is_not()
    {
        var recent = Old("Recent");
        var older = Old("Older");
        Sent(recent.Item, _rig.Time.GetUtcNow() - TimeSpan.FromHours(2));
        Sent(older.Item, _rig.Time.GetUtcNow() - TimeSpan.FromHours(4));

        var result = await Act();

        Assert.Equal((1, 1), (result.Deleted, result.Skipped));
        Assert.True(File.Exists(recent.Path));
        Assert.False(File.Exists(older.Path));
        Assert.Contains("recent_add", _log.Named("delete.skipped").Single().Message);
    }

    [Fact]
    public async Task DL10_a_song_a_peer_is_downloading_is_skipped_and_an_unknown_answer_defers()
    {
        var song = Old("Uploading");
        _uploads.Result = new ActiveUploadCheck(true, 1);
        var first = await Act();
        _uploads.Result = new ActiveUploadCheck(false, 0);
        var second = await Act();

        Assert.Equal(0, first.Deleted + second.Deleted);
        Assert.True(File.Exists(song.Path));
        var why = _log.Named("delete.skipped").Select(e => e.Message).ToList();
        Assert.Contains(why, m => m.EndsWith("uploading"));
        Assert.Contains(why, m => m.EndsWith("uploads_unknown"));
    }

    [Fact]
    public async Task DL11_a_skipped_song_is_deleted_by_a_later_pass()
    {
        var song = Old("Later");
        _rig.Jellyfin.Sessions = [new SessionInfoDto { Id = "s1", NowPlayingItem = new BaseItemDto { Id = song.Item } }];
        await Act();
        _rig.Jellyfin.Sessions = [];

        var result = await Act();

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(song.Path));
    }

    [Fact]
    public async Task DL12_one_media_updated_per_album_folder_and_shares_marked_dirty()
    {
        var a = Old("A1", "X");
        var b = Old("A2", "X");
        var c = Old("B1", "Y");
        // A1 and A2 sit in the same album folder (Ready puts every song of an artist in artist/Album).

        var result = await Act();

        Assert.Equal(3, result.Deleted);
        var paths = _rig.Jellyfin.MediaUpdatedPaths.ToList();
        Assert.Equal(["/music-radio/soulseek/X/Album", "/music-radio/soulseek/Y/Album"], paths.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["delete"], _dirty.Reasons);
        _ = (a, b, c);
    }

    [Fact]
    public async Task DL13_nothing_is_reported_and_shares_stay_clean_when_nothing_was_deleted()
    {
        Old("Young", ageDays: 1);

        var result = await Act();

        Assert.Equal(0, result.Deleted);
        Assert.Empty(_rig.Jellyfin.MediaUpdatedPaths);
        Assert.Empty(_dirty.Reasons);
    }

    [Fact]
    public async Task DL14_trash_older_than_7_days_is_purged_and_newer_trash_stays()
    {
        var root = Path.Combine(_rig.Radio, ".encore-trash");
        foreach (var day in new[] { "2026-11-10", "2026-11-12", "2026-11-18", "notadate" })
        {
            Directory.CreateDirectory(Path.Combine(root, day));
            File.WriteAllText(Path.Combine(root, day, "1.flac"), "x");
        }

        var result = await Act();

        Assert.Equal((2, 2), (result.TrashFoldersPurged, result.TrashFilesPurged));
        Assert.False(Directory.Exists(Path.Combine(root, "2026-11-10")));
        Assert.False(Directory.Exists(Path.Combine(root, "2026-11-12")));
        Assert.True(Directory.Exists(Path.Combine(root, "2026-11-18")));
        Assert.True(Directory.Exists(Path.Combine(root, "notadate")));
        Assert.Single(_log.Named("trash.purged"));
    }

    [Fact]
    public async Task DL15_at_most_the_pass_cap_is_deleted_and_the_rest_wait()
    {
        var songs = Enumerable.Range(0, 4).Select(i => Old("S" + i, "Art" + i)).ToList();

        var result = await Act(Make(o => o with { RetentionMaxDeletes = 2 }));

        Assert.Equal(2, result.Deleted);
        Assert.Equal(2, songs.Count(s => !File.Exists(s.Path)));
        Assert.Equal(2, songs.Count(s => _rig.Fetch.GetTracked(s.Id)!.State == FetchState.Ready));
    }

    [Fact]
    public async Task DL16_a_skipped_song_does_not_use_up_the_cap()
    {
        var first = Old("P0", "Art0");
        var second = Old("P1", "Art1");
        _rig.Jellyfin.Sessions = [new SessionInfoDto { Id = "s1", NowPlayingItem = new BaseItemDto { Id = first.Item } }];

        var result = await Act(Make(o => o with { RetentionMaxDeletes = 1 }));

        Assert.Equal((1, 1), (result.Deleted, result.Skipped));
        Assert.False(File.Exists(second.Path));
    }

    [Fact]
    public async Task DL17_dry_run_and_off_never_touch_a_file()
    {
        var song = Old("Dry");
        var pass = await _rig.Pass.RunAsync();

        var dry = await Make(o => o with { RetentionMode = RetentionMode.DryRun }).RunAsync(pass);
        var off = await Make(o => o with { RetentionMode = RetentionMode.Off }).RunAsync(pass);

        Assert.Equal(DeleteResult.None, dry);
        Assert.Equal(DeleteResult.None, off);
        Assert.True(File.Exists(song.Path));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(song.Id)!.State);
    }

    [Fact]
    public async Task DL18_a_failed_signal_read_or_sessions_read_defers_everything()
    {
        var song = Old("Careful");
        _rig.Jellyfin.SmartLists = new SmartListsInfo(true, false, new HashSet<Guid>());
        var blocked = await Act();

        Assert.Equal(0, blocked.Deleted);
        Assert.True(File.Exists(song.Path));
    }

    [Fact]
    public async Task DL19_a_row_whose_file_is_already_gone_is_still_marked_deleted()
    {
        var song = Old("Vanished");
        var pass = await _rig.Pass.RunAsync();
        File.Delete(song.Path);

        var result = await Act(pass: pass);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(FetchState.Deleted, _rig.Fetch.GetTracked(song.Id)!.State);
    }

    [Fact]
    public async Task DL20_a_row_whose_path_leaves_the_soulseek_folder_is_never_touched()
    {
        var song = Old("Outside");
        var pass = await _rig.Pass.RunAsync();
        var outside = Path.Combine(_rig.Dir, "elsewhere.flac");
        File.WriteAllText(outside, "x");
        _rig.Fetch.UpdateFetched(song.Id, FetchState.Ready, new FetchedUpdate(Path: outside));

        var result = await Act(pass: pass);

        Assert.Equal((0, 1), (result.Deleted, result.Failed));
        Assert.True(File.Exists(outside));
        Assert.Single(_log.Named("delete.failed"));
    }

    [Fact]
    public async Task DL21_a_row_with_a_promotion_row_is_never_deleted_even_with_a_forced_decision()
    {
        var song = Old("Promoting");
        var pass = await _rig.Pass.RunAsync();
        Assert.Equal(RetentionDecision.Delete, pass.Rows.Single().Decision);
        _rig.Retention.StartPromotion(new NewPromotion(song.Id, song.Item.ToString("N"), _rig.Time.GetUtcNow()));
        _rig.Retention.UpdatePromotion(song.Id, PromotionStep.OldGone, _rig.Time.GetUtcNow(), new PromotionUpdate(Stalled: true));

        var result = await Act(pass: pass);

        Assert.Equal((0, 1), (result.Deleted, result.Skipped));
        Assert.True(File.Exists(song.Path));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(song.Id)!.State);
        Assert.Contains("promoting", _log.Named("delete.skipped").Single().Message);
    }

    [Fact]
    public async Task DL22_a_request_that_lands_during_the_delete_puts_the_file_back_and_keeps_the_row()
    {
        var song = Old("LateRequest");
        var pass = await _rig.Pass.RunAsync();

        // After the re-check, before the move: the user requests the song.
        _uploads.OnCheck = () => _rig.Fetch.MarkRequested("key-LateRequest");
        var result = await Act(pass: pass);

        Assert.Equal((0, 1), (result.Deleted, result.Kept));
        Assert.True(File.Exists(song.Path));
        Assert.False(File.Exists(Trash(_rig, "2026-11-20", song.Id)));
        var row = _rig.Fetch.GetTracked(song.Id)!;
        Assert.Equal((FetchState.Ready, true), (row.State, row.Requested));
        Assert.NotNull(_owned.KeyOf(song.Item));
        Assert.Empty(_dirty.Reasons);
    }

    [Fact]
    public void DL23_the_store_refuses_to_mark_a_requested_or_kept_row_deleted()
    {
        var requested = Old("Req");
        var kept = Old("Kpt");
        _rig.Fetch.MarkRequested("key-Req");
        _rig.Fetch.MarkKept(kept.Id, "favorite", _rig.Time.GetUtcNow());

        Assert.False(_rig.Fetch.MarkDeleted(requested.Id, "age", _rig.Time.GetUtcNow()));
        Assert.False(_rig.Fetch.MarkDeleted(kept.Id, "age", _rig.Time.GetUtcNow()));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(requested.Id)!.State);
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(kept.Id)!.State);
    }

    private void Sent(Guid item, DateTimeOffset at) =>
        _rig.Store.RecordFire(
            new TriggerRecord(at, "sess-one", PassRig.User, "Finamp", "upcoming", "topup", Guid.NewGuid(), null, "dedupe-" + Guid.NewGuid().ToString("N"), EncoreStore.SentOutcome, "run-1", 5),
            [new PickedItem(item, "Song", null, "Artist", PickSource.DeezerRadio, PickCopy.Soulseek)]);

    private sealed class FakeUploads : IUploadCheck
    {
        public ActiveUploadCheck Result { get; set; } = new(true, 0);

        /// <summary>Runs inside the check: after the deleter's re-check, before its move.</summary>
        public Action? OnCheck { get; set; }

        public Task<ActiveUploadCheck> CheckActiveAsync(long rowId, string radioPath, CancellationToken ct)
        {
            OnCheck?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeDirty : ISharesDirty
    {
        public List<string> Reasons { get; } = [];

        public void MarkDirty(string reason) => Reasons.Add(reason);
    }
}

/// <summary>OwnedIndex.Remove (v04 section 6 step 4).</summary>
public sealed class OwnedIndexRemoveTests
{
    private static OwnedEntry Entry(Guid id, string title)
    {
        TrackKey.TryCreate("Artist", title, out var key);
        return new OwnedEntry(id, key, title, null, "Artist", 180, "/music-radio/soulseek/a/b.flac", null, true);
    }

    [Fact]
    public void Remove_drops_the_song_from_both_lookups_and_ignores_an_unknown_id()
    {
        var index = new OwnedIndex(new FakeJellyfin(), "/music-radio", new ListLogger<OwnedIndex>());
        var id = Guid.NewGuid();
        var entry = Entry(id, "Song");
        index.Add(entry);
        index.Add(Entry(Guid.NewGuid(), "Other"));

        index.Remove(Guid.NewGuid());
        Assert.Equal(2, index.Count);
        index.Remove(id);

        Assert.Equal(1, index.Count);
        Assert.Null(index.KeyOf(id));
        Assert.Null(index.Resolve(entry.Key, 180));
    }
}
