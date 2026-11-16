using Encore.Config;
using Encore.Picks;
using Encore.Store;
using Encore.Tests.Trigger;
using Encore.Trigger;
using Encore.Tests.Store;
using Microsoft.Data.Sqlite;

namespace Encore.Tests.EndToEnd;

public class EncoreStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-store-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    public EncoreStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Pooling is off in the store, so the files can be deleted once the test's stores are disposed.
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

    private string Path_(string name = "encore.db") => System.IO.Path.Combine(_dir, name);

    private static readonly Guid User = Catalog.G(1);
    private const string Session = "sess-1";

    private static PickedItem Pick(long n, Guid artist, PickSource source = PickSource.SimilarArtist) =>
        new(Catalog.G(n), $"Song {n}", artist, "Artist", source);

    private static TriggerRecord Record(DateTimeOffset at, string outcome, string key, string run = "run-1") => new(
        at, Session, User, "Jellyfin Web", "full", "start", Catalog.G(100), "playlistItem2", key, outcome, run, 4, outcome == "failed" ? 503 : null);

    [Fact]
    public void A_new_store_has_schema_5_and_reopens()
    {
        using (var store = EncoreStore.Open(Path_(), _time))
        {
            Assert.Equal(5, store.SchemaVersionOnDisk);
            Assert.NotNull(store.GetMeta("created_utc"));
        }

        using var again = EncoreStore.Open(Path_(), _time);
        Assert.Equal(5, again.SchemaVersionOnDisk);
    }

    [Fact]
    public void A_database_from_a_newer_build_is_refused()
    {
        using (var store = EncoreStore.Open(Path_(), _time))
        {
            Assert.Equal(5, store.SchemaVersionOnDisk);
        }

        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path_(), Pooling = false }.ConnectionString))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<StoreException>(() => EncoreStore.Open(Path_(), _time));
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sent_fire_writes_the_added_songs_and_plays_in_order()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        var items = new[] { Pick(1001, Catalog.G(201)), Pick(1002, Catalog.G(202), PickSource.InstantMix) };

        var triggerId = store.RecordFire(Record(_time.GetUtcNow(), "sent", "key-a"), items);

        Assert.True(triggerId > 0);
        Assert.Equal(2, store.CountAddedSince(Session, _time.GetUtcNow() - TimeSpan.FromHours(1)));
        var run = store.LatestRun(Session, _time.GetUtcNow() - TimeSpan.FromHours(6));
        Assert.NotNull(run);
        Assert.Equal(Catalog.G(1002), run.LastAddedItemId);
        Assert.Equal(2, run.AddedCount);
    }

    [Fact]
    public void Only_sent_and_dry_run_keys_count_as_duplicates_within_the_ttl()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        var now = _time.GetUtcNow();
        store.RecordFire(Record(now, "failed", "key-failed"), []);
        store.RecordFire(Record(now, "sent", "key-sent"), [Pick(1001, Catalog.G(201))]);

        Assert.False(store.IsDuplicate("key-failed", now, TriggerLimits.DedupeTtl));
        Assert.True(store.IsDuplicate("key-sent", now, TriggerLimits.DedupeTtl));
        Assert.False(store.IsDuplicate("key-sent", now + TimeSpan.FromMinutes(61), TriggerLimits.DedupeTtl));
    }

    [Fact]
    public void Dry_run_counts_as_a_duplicate_but_writes_no_added_songs()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        var now = _time.GetUtcNow();
        store.RecordFire(Record(now, "dry_run", "key-dry"), [Pick(1001, Catalog.G(201))]);

        Assert.True(store.IsDuplicate("key-dry", now, TriggerLimits.DedupeTtl));
        Assert.Equal(0, store.CountAddedSince(null, now - TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Recent_plays_follow_the_window()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        var now = _time.GetUtcNow();
        store.RecordPlayed(Session, User, Catalog.G(1), null, now - TimeSpan.FromDays(2));
        store.RecordPlayed("other-session", User, Catalog.G(2), null, now - TimeSpan.FromMinutes(5));

        var session = store.RecentPlays(RecentWindow.Session, Session, User, now);
        Assert.Equal(new[] { Catalog.G(1) }, session.Select(p => p.ItemId));

        var sixHours = store.RecentPlays(RecentWindow.TryParse("6h", out var w) ? w! : RecentWindow.Session, Session, User, now);
        Assert.Equal(new[] { Catalog.G(2) }, sixHours.Select(p => p.ItemId));
    }

    [Fact]
    public void Session_history_is_newest_first()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        var now = _time.GetUtcNow();
        store.RecordPlayed(Session, User, Catalog.G(1), Catalog.G(201), now);
        store.RecordPlayed(Session, User, Catalog.G(2), Catalog.G(202), now);

        var history = store.SessionHistory(Session, 20);

        Assert.Equal(new[] { Catalog.G(2), Catalog.G(1) }, history.Select(h => h.ItemId));
        Assert.Equal(Catalog.G(202), history[0].ArtistId);
    }

    [Fact]
    public void Prune_removes_plays_older_than_14_days_and_keeps_recent_rows()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        var now = _time.GetUtcNow();
        store.RecordPlayed(Session, User, Catalog.G(1), null, now - TimeSpan.FromDays(20));
        store.RecordPlayed(Session, User, Catalog.G(2), null, now - TimeSpan.FromDays(1));

        var pruned = store.Prune(now);

        Assert.Equal(1, pruned.Plays);
        Assert.Equal(new[] { Catalog.G(2) }, store.SessionHistory(Session, 20).Select(h => h.ItemId));
    }

    [Fact]
    public void Delivery_marks_the_songs_of_one_trigger()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        var now = _time.GetUtcNow();
        var id = store.RecordFire(Record(now, "sent", "key-d"), [Pick(1001, Catalog.G(201))]);

        store.MarkDelivery(id, confirmed: true, now);

        using var cmd = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path_(), Pooling = false }.ConnectionString);
        cmd.Open();
        using var query = cmd.CreateCommand();
        query.CommandText = "SELECT confirmed_utc IS NOT NULL, unconfirmed_utc IS NULL FROM added_items";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
    }

    // ---- Schema 2 and FetchStore (v0.2 design section 9, ST01-ST14) ----

    private SqliteConnection Raw()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path_(), Pooling = false }.ConnectionString);
        db.Open();
        return db;
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Makes a real v1 file: a store with v1 rows, then the schema-2 tables dropped and user_version set back to 1.</summary>
    private void MakeV1File()
    {
        using (var store = EncoreStore.Open(Path_(), _time))
        {
            var now = _time.GetUtcNow();
            store.RecordFire(Record(now, "sent", "key-v1"), [Pick(1001, Catalog.G(201)), Pick(1002, Catalog.G(202))]);
            store.RecordPlayed(Session, User, Catalog.G(3), null, now);
        }

        using var db = Raw();
        Run(db, StoreTestSql.DropV5 + "DROP TABLE page_tokens; DROP TABLE requests; DROP TABLE fetch_attempts; DROP TABLE fetch_negative; DROP TABLE fetched_files; PRAGMA user_version = 1");
    }

    private static NewFetchedFile NewFetch(string job, string key = "key", DateTimeOffset? at = null) => new(
        job, key, "Artist", "Title", "deezer_radio", "soulseek", "run-1", Session, User, at ?? new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_new_file_is_schema_2_with_the_fetch_tables_and_indexes()
    {
        using (EncoreStore.Open(Path_(), _time))
        {
        }

        using var db = Raw();
        Assert.Equal(5, Scalar(db, "PRAGMA user_version"));
        Assert.Equal(3, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('fetched_files','fetch_attempts','fetch_negative')"));
        Assert.Equal(5, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('ix_fetched_key','ix_fetched_state','ix_fetched_path','ix_attempts_at','ix_attempts_key')"));
    }

    [Fact]
    public void A_v1_file_with_data_migrates_to_5_and_keeps_a_v1_backup()
    {
        MakeV1File();

        using (var store = EncoreStore.Open(Path_(), _time))
        {
            Assert.Equal(5, store.SchemaVersionOnDisk);
        }

        using (var db = Raw())
        {
            Assert.Equal(5, Scalar(db, "PRAGMA user_version"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM triggers"));
            Assert.Equal(2, Scalar(db, "SELECT COUNT(*) FROM added_items"));
            Assert.Equal(3, Scalar(db, "SELECT COUNT(*) FROM plays"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fetched_files'"));
        }

        var backup = Path_() + ".v1.bak";
        Assert.True(File.Exists(backup));
        using var bak = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        bak.Open();
        Assert.Equal(1, Scalar(bak, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(bak, "SELECT COUNT(*) FROM triggers"));
        Assert.Equal(2, Scalar(bak, "SELECT COUNT(*) FROM added_items"));
        Assert.Equal(0, Scalar(bak, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fetched_files'"));
    }

    [Fact]
    public void A_new_file_makes_no_backup_and_a_reopen_does_not_rewrite_it()
    {
        using (EncoreStore.Open(Path_(), _time))
        {
        }

        Assert.False(File.Exists(Path_() + ".v1.bak"));

        MakeV1File();
        EncoreStore.Open(Path_(), _time).Dispose();
        var first = File.GetLastWriteTimeUtc(Path_() + ".v1.bak");
        var size = new FileInfo(Path_() + ".v1.bak").Length;
        EncoreStore.Open(Path_(), _time).Dispose();

        Assert.Equal(first, File.GetLastWriteTimeUtc(Path_() + ".v1.bak"));
        Assert.Equal(size, new FileInfo(Path_() + ".v1.bak").Length);
    }

    [Fact]
    public void A_v2_file_is_newer_than_a_v1_only_build_accepts()
    {
        // The v0.1.1 build refuses any user_version above its own SchemaVersion (1). Here the same rule is applied
        // by this build to a file one version ahead of it.
        using (EncoreStore.Open(Path_(), _time))
        {
        }

        using (var db = Raw())
        {
            Assert.True(Scalar(db, "PRAGMA user_version") > 1);
            Run(db, "PRAGMA user_version = 6");
        }

        var ex = Assert.Throws<StoreException>(() => EncoreStore.Open(Path_(), _time));
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchStore_refuses_a_v1_file_and_opens_a_v2_file()
    {
        MakeV1File();
        Assert.Throws<StoreException>(() => FetchStore.Open(Path_()));

        EncoreStore.Open(Path_(), _time).Dispose();
        FetchStore.Open(Path_()).Dispose();
    }

    [Fact]
    public void FetchStore_refuses_a_missing_file()
    {
        Assert.Throws<StoreException>(() => FetchStore.Open(Path_("nope.db")));
    }

    [Fact]
    public async Task FetchStore_writes_on_worker_threads_while_EncoreStore_writes_fires()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());
        var now = _time.GetUtcNow();

        var workers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                var id = fetch.InsertFetched(NewFetch($"job-{w}-{i}", $"key-{w}-{i}", now));
                fetch.UpdateFetched(id, FetchState.Importing, new FetchedUpdate(Path: $"/dl/{w}/{i}.flac"));
                fetch.UpdateFetched(id, FetchState.Imported);
                fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(ReadyUtc: now));
            }
        })).ToList();
        var fires = Task.Run(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                store.RecordFire(Record(now, "sent", $"key-f{i}"), [Pick(2000 + i, Catalog.G(201))]);
            }
        });

        await Task.WhenAll(workers.Append(fires));

        using var db = Raw();
        Assert.Equal(40, Scalar(db, "SELECT COUNT(*) FROM fetched_files WHERE state = 'ready'"));
        Assert.Equal(40, Scalar(db, "SELECT COUNT(*) FROM triggers"));
    }

    [Fact]
    public void An_unknown_state_or_attempt_outcome_is_refused_by_the_database()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());
        var id = fetch.InsertFetched(NewFetch("job-1"));

        Assert.Throws<SqliteException>(() => fetch.UpdateFetched(id, "sideways"));
        Assert.Throws<SqliteException>(() => fetch.AddAttempt(new FetchAttempt("job-1", "key", "soulseek", "maybe", _time.GetUtcNow())));
    }

    [Fact]
    public void Prune_keeps_every_fetched_file_and_removes_old_attempts_and_expired_negatives()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());
        var now = _time.GetUtcNow();
        var old = now - TimeSpan.FromDays(365);

        foreach (var state in new[] { "downloading", "importing", "imported", "ready", "failed", "deleted", "promoted" })
        {
            var id = fetch.InsertFetched(NewFetch("job-" + state, "key-" + state, old));
            fetch.UpdateFetched(id, state);
        }

        fetch.AddAttempt(new FetchAttempt("j", "k", "soulseek", "ok", now - TimeSpan.FromDays(91)));
        fetch.AddAttempt(new FetchAttempt("j", "k", "soulseek", "ok", now - TimeSpan.FromDays(89)));
        fetch.UpsertNegative("expired", now - TimeSpan.FromHours(1), "no_results");
        fetch.UpsertNegative("live", now + TimeSpan.FromHours(1), "no_results");

        var pruned = store.Prune(now);

        Assert.Equal(1, pruned.Attempts);
        Assert.Equal(1, pruned.Negative);
        using var db = Raw();
        Assert.Equal(7, Scalar(db, "SELECT COUNT(*) FROM fetched_files"));
        Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM fetch_attempts"));
        Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM fetch_negative"));
    }

    [Fact]
    public void The_negative_cache_expires_and_a_second_miss_extends_it()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());
        var now = _time.GetUtcNow();

        fetch.UpsertNegative("k", now + TimeSpan.FromHours(24), "no_results");
        Assert.True(fetch.IsNegative("k", now));
        Assert.False(fetch.IsNegative("k", now + TimeSpan.FromHours(25)));
        Assert.False(fetch.IsNegative("other", now));
        Assert.Contains("k", fetch.NegativeKeys(now));
        Assert.Empty(fetch.NegativeKeys(now + TimeSpan.FromHours(25)));

        fetch.UpsertNegative("k", now + TimeSpan.FromHours(48), "no_results");

        Assert.True(fetch.IsNegative("k", now + TimeSpan.FromHours(30)));
        using var db = Raw();
        Assert.Equal(2, Scalar(db, "SELECT misses FROM fetch_negative WHERE track_key = 'k'"));
    }

    [Fact]
    public void CountFetchesSince_counts_only_the_window()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());
        var now = _time.GetUtcNow();
        fetch.InsertFetched(NewFetch("a", "ka", now - TimeSpan.FromMinutes(90)));
        fetch.InsertFetched(NewFetch("b", "kb", now - TimeSpan.FromMinutes(30)));
        fetch.InsertFetched(NewFetch("c", "kc", now));

        Assert.Equal(2, fetch.CountFetchesSince(now - TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ActiveForRestart_returns_downloading_importing_and_imported_only()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());
        foreach (var state in new[] { "downloading", "importing", "imported", "ready", "failed", "deleted", "promoted" })
        {
            var id = fetch.InsertFetched(NewFetch("job-" + state, "key-" + state));
            fetch.UpdateFetched(id, state);
        }

        var active = fetch.ActiveForRestart();

        Assert.Equal(new[] { "downloading", "importing", "imported" }, active.Select(a => a.State));
    }

    [Fact]
    public void A_path_is_unique_among_live_rows_only()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());
        var a = fetch.InsertFetched(NewFetch("a", "ka"));
        var b = fetch.InsertFetched(NewFetch("b", "kb"));
        fetch.UpdateFetched(a, FetchState.Importing, new FetchedUpdate(Path: "/dl/x.flac"));

        Assert.Throws<SqliteException>(() => fetch.UpdateFetched(b, FetchState.Importing, new FetchedUpdate(Path: "/dl/x.flac")));

        Assert.True(fetch.UpdateFetched(b, FetchState.Failed, new FetchedUpdate(Path: "/dl/x.flac", FailReason: "file_missing")));
    }

    [Fact]
    public async Task Eight_threads_of_fifty_inserts_through_one_FetchStore_give_400_rows()
    {
        using var store = EncoreStore.Open(Path_(), _time);
        using var fetch = FetchStore.Open(Path_());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                fetch.InsertFetched(NewFetch($"job-{t}-{i}", $"key-{t}-{i}"));
            }
        })));

        using var db = Raw();
        Assert.Equal(400, Scalar(db, "SELECT COUNT(*) FROM fetched_files"));
    }
}
