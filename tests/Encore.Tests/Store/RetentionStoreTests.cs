using System.Globalization;
using Encore.Store;
using Encore.Tests.Trigger;
using Microsoft.Data.Sqlite;

namespace Encore.Tests.Store;

public class RetentionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-ret-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    public RetentionStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
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

    private string DbPath => Path.Combine(_dir, "encore.db");

    private SqliteConnection Raw()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ConnectionString);
        db.Open();
        return db;
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private static NewFetchedFile Fetch(string job, string key = "key-1") => new(
        job, key, "Artist", "Title", "deezer_radio", "soulseek", "run-1", "sess", Guid.Parse("00000000-0000-0000-0000-000000000001"),
        new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    /// <summary>A ready row with a path, as the fetch worker leaves it.</summary>
    private static long ReadyRow(FetchStore fetch, string job, string key, string path, DateTimeOffset ready)
    {
        var id = fetch.InsertFetched(Fetch(job, key));
        fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(Path: path, JellyfinPath: "/music" + path, JellyfinItemId: "item-" + job, ReadyUtc: ready));
        return id;
    }

    [Fact]
    public void A_v4_file_with_data_upgrades_to_v5_with_a_v4_backup()
    {
        using (EncoreStore.Open(DbPath, _time))
        using (var fetch = FetchStore.Open(DbPath))
        {
            fetch.InsertFetched(Fetch("job-1"));
        }

        using (var db = Raw())
        {
            Run(db, StoreTestSql.DropV5 + "PRAGMA user_version = 4");
        }

        using (var store = EncoreStore.Open(DbPath, _time))
        {
            Assert.Equal(5, store.SchemaVersionOnDisk);
        }

        using (var db = Raw())
        {
            Assert.Equal(5, Scalar(db, "PRAGMA user_version"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM fetched_files"));
            Assert.Equal(0, Scalar(db, "SELECT play_throughs FROM fetched_files"));
            Assert.Equal(6, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('slskd_uploads','play_throughs','promotions','promotion_entries','promotion_userdata','retention_decisions')"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('fetched_files') WHERE name = 'kept_utc'"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('promotion_entries') WHERE name = 'candidate_user_ids'"));
            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('promotion_entries') WHERE name IN ('owner_user_id','entry_id')"));
        }

        var backup = DbPath + ".v4.bak";
        Assert.True(File.Exists(backup));
        using var bak = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        bak.Open();
        Assert.Equal(4, Scalar(bak, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(bak, "SELECT COUNT(*) FROM fetched_files"));
        Assert.Equal(0, Scalar(bak, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'promotions'"));

        using var retention = RetentionStore.Open(DbPath);
        Assert.Null(retention.GetEventsWatermark());
    }

    [Fact]
    public void RetentionStore_refuses_an_old_file_and_a_missing_file()
    {
        Assert.Throws<StoreException>(() => RetentionStore.Open(Path.Combine(_dir, "nope.db")));
        using (EncoreStore.Open(DbPath, _time))
        {
        }

        using (var db = Raw())
        {
            Run(db, "PRAGMA user_version = 4");
        }

        Assert.Throws<StoreException>(() => RetentionStore.Open(DbPath));
    }

    [Fact]
    public void Uploads_are_counted_once_per_transfer_and_update_the_row_counter()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var ret = RetentionStore.Open(DbPath);
        var id = ReadyRow(fetch, "j1", "k1", "/radio/a.flac", Now);

        Assert.True(ret.AddUpload("t1", id, Now, Now));
        Assert.False(ret.AddUpload("t1", id, Now, Now));
        Assert.True(ret.AddUpload("t2", id, Now, Now));

        Assert.Equal(2, ret.UploadCount(id));
        Assert.Equal(2, fetch.GetTracked(id)!.Uploads);
    }

    [Fact]
    public void Play_throughs_are_unique_per_session_item_and_start()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var ret = RetentionStore.Open(DbPath);
        var id = ReadyRow(fetch, "j1", "k1", "/radio/a.flac", Now);
        var p = new NewPlayThrough(id, "item", "user", "sess", Now, Now.AddMinutes(3));

        Assert.True(ret.AddPlayThrough(p));
        Assert.False(ret.AddPlayThrough(p));
        Assert.True(ret.AddPlayThrough(p with { StartedUtc = Now.AddHours(1), EndedUtc = Now.AddHours(1).AddMinutes(3) }));

        Assert.Equal(2, ret.PlayThroughCount(id));
        Assert.Equal(2, fetch.GetTracked(id)!.PlayThroughs);
    }

    [Fact]
    public void Prune_keeps_uploads_and_play_throughs_and_trims_only_old_decisions()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var ret = RetentionStore.Open(DbPath);
        var id = ReadyRow(fetch, "j1", "k1", "/radio/a.flac", Now);
        var old = Now.AddDays(-400);
        ret.AddUpload("t1", id, old, old);
        ret.AddPlayThrough(new NewPlayThrough(id, "item", "user", "sess", old, old.AddMinutes(3)));
        ret.AddDecision(new RetentionDecisionRecord("pass-old", old, RetentionDecision.Keep, false, id));
        ret.AddDecision(new RetentionDecisionRecord("pass-new", Now.AddDays(-1), RetentionDecision.Wait, true, id));

        var pruned = store.Prune(Now);
        Assert.Equal(0, pruned.Plays + pruned.AddedItems + pruned.Triggers);
        Assert.Equal(1, ret.PruneDecisions(Now));

        Assert.Equal(1, ret.UploadCount(id));
        Assert.Equal(1, ret.PlayThroughCount(id));
        Assert.Empty(ret.Decisions("pass-old"));
        var kept = Assert.Single(ret.Decisions("pass-new"));
        Assert.True(kept.DryRun);
        Assert.Equal(id, kept.FetchedFileId);
    }

    [Fact]
    public void A_promotion_walks_its_steps_and_resumes_while_not_done()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var ret = RetentionStore.Open(DbPath);
        var id = ReadyRow(fetch, "j1", "k1", "/radio/a.flac", Now);

        Assert.True(ret.StartPromotion(new NewPromotion(id, "old-item", Now)));
        Assert.False(ret.StartPromotion(new NewPromotion(id, "old-item", Now)));
        Assert.Equal(PromotionStep.Snapshot, ret.GetPromotion(id)!.Step);

        Assert.True(ret.UpdatePromotion(id, PromotionStep.Copied, Now.AddMinutes(1),
            new PromotionUpdate(StagedPath: "/lib/a.encore-part", TargetPath: "/lib/a.flac", Sha256: "abc", Attempts: 1, LastError: "boom")));
        var row = ret.GetPromotion(id)!;
        Assert.Equal(PromotionStep.Copied, row.Step);
        Assert.Equal("/lib/a.encore-part", row.StagedPath);
        Assert.Equal("/lib/a.flac", row.TargetPath);
        Assert.Equal("abc", row.Sha256);
        Assert.Equal("boom", row.LastError);
        Assert.Equal("old-item", row.OldItemId);
        Assert.False(row.Stalled);

        ret.UpdatePromotion(id, PromotionStep.Copied, Now.AddMinutes(2), new PromotionUpdate(ClearLastError: true, Stalled: true));
        row = ret.GetPromotion(id)!;
        Assert.Null(row.LastError);
        Assert.Equal("abc", row.Sha256);
        Assert.True(row.Stalled);
        Assert.Single(ret.ActivePromotions());

        ret.UpdatePromotion(id, PromotionStep.Done, Now.AddMinutes(3), new PromotionUpdate(NewItemId: "new-item"));
        Assert.Empty(ret.ActivePromotions());
        Assert.Equal("new-item", ret.GetPromotion(id)!.NewItemId);
        Assert.False(ret.UpdatePromotion(9999, PromotionStep.Done, Now));
    }

    [Fact]
    public void Playlist_entries_hold_candidates_and_a_position_and_come_back_highest_first()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var ret = RetentionStore.Open(DbPath);
        var id = ReadyRow(fetch, "j1", "k1", "/radio/a.flac", Now);

        ret.AddEntries(id, [new NewPromotionEntry("pl-1", ["u1", "u2"], 2), new NewPromotionEntry("pl-2", ["u3"], 7)]);
        ret.AddEntries(id, [new NewPromotionEntry("pl-1", ["u1"], 9)]);

        var entries = ret.Entries(id);
        Assert.Equal(2, entries.Count);
        Assert.Equal("pl-2", entries[0].PlaylistId);
        Assert.Equal(7, entries[0].Position);
        Assert.Equal(["u1", "u2"], entries[1].CandidateUserIds);
        Assert.Equal(2, entries[1].Position);
        Assert.Null(entries[1].EditorUserId);
        Assert.Null(entries[1].ReaddedUtc);

        Assert.True(ret.SetEditor(entries[1].Id, "u2"));
        Assert.True(ret.MarkReadded(entries[1].Id, Now));
        var after = ret.Entries(id).Single(e => e.PlaylistId == "pl-1");
        Assert.Equal("u2", after.EditorUserId);
        Assert.Equal(Now, after.ReaddedUtc);
    }

    [Fact]
    public void User_data_is_saved_per_user_and_overwritten()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var ret = RetentionStore.Open(DbPath);
        var id = ReadyRow(fetch, "j1", "k1", "/radio/a.flac", Now);

        ret.SaveUserData(id, new PromotionUserData("u2", false, 1));
        ret.SaveUserData(id, new PromotionUserData("u1", true, 4));
        ret.SaveUserData(id, new PromotionUserData("u2", true, 3));

        Assert.Equal([new PromotionUserData("u1", true, 4), new PromotionUserData("u2", true, 3)], ret.UserData(id));
    }

    [Fact]
    public void Meta_keeps_the_events_watermark_and_the_last_rescan()
    {
        using (var store = EncoreStore.Open(DbPath, _time))
        using (var ret = RetentionStore.Open(DbPath))
        {
            Assert.Null(ret.GetEventsWatermark());
            Assert.Null(ret.GetLastRescan());
            ret.SetEventsWatermark("41");
            ret.SetEventsWatermark("57");
            ret.SetLastRescan(Now);
            Assert.Equal("57", store.GetMeta("slskd_events_watermark"));
        }

        using var again = RetentionStore.Open(DbPath);
        Assert.Equal("57", again.GetEventsWatermark());
        Assert.Equal(Now, again.GetLastRescan());
    }

    [Fact]
    public void A_dedupe_decision_is_stored_under_the_old_check_and_read_back_as_dedupe()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var ret = RetentionStore.Open(DbPath);
        ret.AddDecision(new RetentionDecisionRecord("p", Now, RetentionDecision.Dedupe, true, 7, "/radio/x.flac", "requested", TargetPath: "/media/music/x.flac"));
        ret.AddDecision(new RetentionDecisionRecord("p", Now, RetentionDecision.Promote, true, 8, "/radio/y.flac", "favorite"));

        var rows = ret.Decisions("p");

        Assert.Equal((RetentionDecision.Dedupe, "requested", "/media/music/x.flac"), (rows[0].Decision, rows[0].Reason, rows[0].TargetPath));
        Assert.Equal((RetentionDecision.Promote, "favorite"), (rows[1].Decision, rows[1].Reason));
    }

    [Fact]
    public void FetchStore_marks_a_requested_and_kept_row_deleted_in_library_but_never_a_row_that_is_not_ready()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        var a = ReadyRow(fetch, "ja", "ka", "/radio/a.flac", Now);
        fetch.MarkRequested("ka");
        fetch.MarkKept(a, "requested", Now);
        Assert.False(fetch.MarkDeleted(a, "age", Now));

        Assert.True(fetch.MarkDeletedInLibrary(a, Now));
        var row = fetch.GetTracked(a)!;
        Assert.Equal((FetchState.Deleted, "in_library"), (row.State, row.DeleteReason));
        Assert.False(fetch.MarkDeletedInLibrary(a, Now));
    }

    [Fact]
    public void Decisions_reject_an_unknown_decision_word()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var ret = RetentionStore.Open(DbPath);
        Assert.Throws<SqliteException>(() => ret.AddDecision(new RetentionDecisionRecord("p", Now, "maybe", false)));
        ret.AddDecision(new RetentionDecisionRecord("p", Now, RetentionDecision.Untracked, false, Path: "/radio/x.flac", Reason: "no row"));
        var d = Assert.Single(ret.Decisions("p"));
        Assert.Null(d.FetchedFileId);
        Assert.Equal("/radio/x.flac", d.Path);
    }

    [Fact]
    public void FetchStore_tracks_ready_rows_and_marks_kept_deleted_and_promoted()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        var a = ReadyRow(fetch, "ja", "ka", "/radio/a.flac", Now.AddDays(-3));
        var b = ReadyRow(fetch, "jb", "kb", "/radio/b.flac", Now.AddDays(-2));
        var c = ReadyRow(fetch, "jc", "kc", "/radio/c.flac", Now.AddDays(-1));
        var failed = fetch.InsertFetched(Fetch("jf", "kf"));
        fetch.UpdateFetched(failed, FetchState.Failed, new FetchedUpdate(FailReason: "x"));

        Assert.Equal([a, b, c], fetch.TrackedForRetention().Select(t => t.Id));

        Assert.True(fetch.MarkKept(a, "favorite", Now));
        Assert.Equal([b, c], fetch.TrackedForRetention().Select(t => t.Id));
        var kept = fetch.GetTracked(a)!;
        Assert.Equal("favorite", kept.KeptReason);
        Assert.Equal(Now, kept.KeptUtc);
        Assert.Equal(FetchState.Ready, kept.State);

        Assert.True(fetch.MarkDeleted(b, "age", Now));
        var deleted = fetch.GetTracked(b)!;
        Assert.Equal(FetchState.Deleted, deleted.State);
        Assert.Equal("age", deleted.DeleteReason);
        Assert.False(fetch.MarkDeleted(b, "age", Now));

        Assert.True(fetch.MarkPromoted(c, "/music/Artist/Album/c.flac", "/jf/music/Artist/Album/c.flac", "new-item", Now));
        var promoted = fetch.GetTracked(c)!;
        Assert.Equal(FetchState.Promoted, promoted.State);
        Assert.Equal("/radio/c.flac", promoted.RadioPath);
        Assert.Equal("/music/Artist/Album/c.flac", promoted.Path);
        Assert.Equal("new-item", promoted.JellyfinItemId);
        Assert.Equal(Now, promoted.PromotedUtc);
        Assert.False(fetch.MarkKept(c, "x", Now));

        Assert.Empty(fetch.TrackedForRetention());
        Assert.Null(fetch.GetTracked(9999));
    }

    [Fact]
    public void A_row_requested_by_a_user_shows_up_with_its_kept_reason_but_no_kept_time()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        var id = ReadyRow(fetch, "ja", "ka", "/radio/a.flac", Now);
        fetch.MarkRequested("ka");

        var row = Assert.Single(fetch.TrackedForRetention());
        Assert.Equal(id, row.Id);
        Assert.True(row.Requested);
        Assert.Equal("requested", row.KeptReason);
        Assert.Null(row.KeptUtc);
    }

    [Fact]
    public void FetchStore_sets_the_counters()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        var id = ReadyRow(fetch, "ja", "ka", "/radio/a.flac", Now);

        Assert.True(fetch.SetCounters(id, 3, 5));
        var row = fetch.GetTracked(id)!;
        Assert.Equal(3, row.Uploads);
        Assert.Equal(5, row.PlayThroughs);
        Assert.False(fetch.SetCounters(9999, 1, 1));
    }

    [Fact]
    public async Task RetentionStore_writes_on_threads_while_FetchStore_and_EncoreStore_write()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var ret = RetentionStore.Open(DbPath);
        var id = ReadyRow(fetch, "ja", "ka", "/radio/a.flac", Now);

        var tasks = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                ret.AddUpload($"t-{w}-{i}", id, Now, Now);
                fetch.AddAttempt(new FetchAttempt($"j-{w}-{i}", "ka", "soulseek", "ok", Now));
                ret.AddDecision(new RetentionDecisionRecord("pass", Now, RetentionDecision.Wait, true, id));
            }
        })).ToArray();
        tasks = [.. tasks, Task.Run(() => store.Prune(Now))];
        await Task.WhenAll(tasks);

        Assert.Equal(40, ret.UploadCount(id));
        Assert.Equal(40, ret.Decisions("pass").Count);
    }
}
