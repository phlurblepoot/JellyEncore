using System.Globalization;
using Encore.Store;
using Encore.Tests.Trigger;
using Microsoft.Data.Sqlite;

namespace Encore.Tests.Store;

public class RequestStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-req-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    public RequestStoreTests() => Directory.CreateDirectory(_dir);

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

    private static NewRequest Song(string id, string user, DateTimeOffset at, string? key = "key-1", string kind = "song") =>
        new(id, kind, user, "Name-" + user, "Artist", at, Title: "Title", TrackKey: key, DeezerTrackId: 42);

    private static NewFetchedFile Fetch(string job, string key) => new(
        job, key, "Artist", "Title", "deezer_radio", "soulseek", "run-1", "sess", Guid.Parse("00000000-0000-0000-0000-000000000001"),
        new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_v2_file_with_data_upgrades_to_v3_with_a_v2_backup()
    {
        using (var store = EncoreStore.Open(DbPath, _time))
        {
            using var fetch = FetchStore.Open(DbPath);
            fetch.InsertFetched(Fetch("job-1", "key-1"));
        }

        using (var db = Raw())
        {
            // Turn the file back into a real v2 one: no requests table, no requested column.
            Run(db, "DROP TABLE requests; ALTER TABLE fetched_files DROP COLUMN requested; PRAGMA user_version = 2");
        }

        using (var store = EncoreStore.Open(DbPath, _time))
        {
            Assert.Equal(3, store.SchemaVersionOnDisk);
        }

        using (var db = Raw())
        {
            Assert.Equal(3, Scalar(db, "PRAGMA user_version"));
            Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM fetched_files"));
            Assert.Equal(0, Scalar(db, "SELECT requested FROM fetched_files"));
            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM requests"));
            Assert.Equal(3, Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ix_requests_user','ix_requests_state','ix_requests_key')"));
        }

        var backup = DbPath + ".v2.bak";
        Assert.True(File.Exists(backup));
        using var bak = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        bak.Open();
        Assert.Equal(2, Scalar(bak, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(bak, "SELECT COUNT(*) FROM fetched_files"));
        Assert.Equal(0, Scalar(bak, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'requests'"));
    }

    [Fact]
    public void RequestStore_refuses_an_old_file_and_a_missing_file()
    {
        Assert.Throws<StoreException>(() => RequestStore.Open(Path.Combine(_dir, "nope.db")));
        using (EncoreStore.Open(DbPath, _time))
        {
        }

        using (var db = Raw())
        {
            Run(db, "PRAGMA user_version = 2");
        }

        Assert.Throws<StoreException>(() => RequestStore.Open(DbPath));
    }

    [Fact]
    public void Insert_get_and_update_round_trip()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var req = RequestStore.Open(DbPath);
        var at = _time.GetUtcNow();
        req.Insert(new NewRequest("r1", "album", "u1", "Nate", "Artist", at, Album: "Album", Year: 1999,
            CoverUrl: "http://x/c.jpg", ForeignAlbumId: "mb-al", ForeignArtistId: "mb-ar"));

        var row = req.Get("r1")!;
        Assert.Equal("queued", row.State);
        Assert.Equal("Nate", row.UserName);
        Assert.Equal(1999, row.Year);
        Assert.Equal("mb-al", row.ForeignAlbumId);
        Assert.Equal(at, row.CreatedUtc);
        Assert.Equal(0, row.Attempts);
        Assert.Null(req.Get("nope"));

        var later = at.AddMinutes(5);
        Assert.True(req.Update("r1", RequestState.Searching, later, new RequestUpdate(Detail: "looking", Attempts: 1, NextTryUtc: later.AddMinutes(10), TracksTotal: 12)));
        Assert.True(req.Update("r1", RequestState.Downloading, later, new RequestUpdate(TracksDone: 3)));
        row = req.Get("r1")!;
        Assert.Equal("downloading", row.State);
        Assert.Equal("looking", row.Detail);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(12, row.TracksTotal);
        Assert.Equal(3, row.TracksDone);
        Assert.Equal(later.AddMinutes(10), row.NextTryUtc);
        Assert.Equal(later, row.UpdatedUtc);

        Assert.True(req.Update("r1", RequestState.Ready, later, new RequestUpdate(ClearNextTry: true, ReadyUtc: later)));
        row = req.Get("r1")!;
        Assert.Null(row.NextTryUtc);
        Assert.Equal(later, row.ReadyUtc);
        Assert.False(req.Update("nope", RequestState.Ready, later));
    }

    [Fact]
    public void The_database_refuses_an_unknown_state_or_kind()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var req = RequestStore.Open(DbPath);
        var at = _time.GetUtcNow();
        req.Insert(Song("r1", "u1", at));

        Assert.Throws<SqliteException>(() => req.Update("r1", "sideways", at));
        Assert.Throws<SqliteException>(() => req.Insert(Song("r2", "u1", at, kind: "video")));
    }

    [Fact]
    public void Lists_counts_and_active_lookups()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var req = RequestStore.Open(DbPath);
        var t = _time.GetUtcNow();
        req.Insert(Song("a", "u1", t.AddHours(-30), "k1"));
        req.Insert(Song("b", "u1", t.AddHours(-2), "k2"));
        req.Insert(Song("c", "u2", t.AddHours(-1), "k2"));
        req.Insert(Song("d", "u1", t.AddMinutes(-10), null, "album"));
        req.Update("b", RequestState.Ready, t);
        req.Update("a", RequestState.Failed, t);

        Assert.Equal(["d", "b", "a"], req.ListByUser("u1").Select(r => r.Id));
        Assert.Equal(["d"], req.ListByUser("u1", activeOnly: true).Select(r => r.Id));
        Assert.Equal(["d", "c", "b", "a"], req.ListAll().Select(r => r.Id));
        Assert.Equal(["c", "d"], req.Active().Select(r => r.Id));
        Assert.Equal(["c"], req.ActiveForKey("k2").Select(r => r.Id));
        Assert.Empty(req.ActiveForKey("other"));
        Assert.Equal(1, req.CountCreatedSince("u1", "song", t.AddHours(-24)));
        Assert.Equal(2, req.CountCreatedSince("u1", "song", t.AddHours(-48)));
        Assert.Equal(1, req.CountCreatedSince("u1", "album", t.AddHours(-24)));
        Assert.Equal(0, req.CountCreatedSince("u3", "song", t.AddHours(-24)));
    }

    [Fact]
    public void MarkRequested_flags_non_failed_rows_of_the_key_only()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        var ok = fetch.InsertFetched(Fetch("j1", "k"));
        var failed = fetch.InsertFetched(Fetch("j2", "k"));
        var other = fetch.InsertFetched(Fetch("j3", "other"));
        fetch.UpdateFetched(failed, FetchState.Failed);

        Assert.Equal(1, fetch.MarkRequested("k"));

        using var db = Raw();
        Assert.Equal(1, Scalar(db, $"SELECT requested FROM fetched_files WHERE id = {ok}"));
        Assert.Equal(1, Scalar(db, $"SELECT COUNT(*) FROM fetched_files WHERE id = {ok} AND kept_reason = 'requested'"));
        Assert.Equal(0, Scalar(db, $"SELECT requested FROM fetched_files WHERE id = {failed}"));
        Assert.Equal(0, Scalar(db, $"SELECT requested FROM fetched_files WHERE id = {other}"));
    }

    [Fact]
    public void A_request_can_reference_a_fetched_file_and_prune_never_deletes_requests()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var req = RequestStore.Open(DbPath);
        var old = _time.GetUtcNow() - TimeSpan.FromDays(400);
        var file = fetch.InsertFetched(Fetch("j1", "k"));
        req.Insert(Song("r1", "u1", old, "k"));
        req.Update("r1", RequestState.Ready, old, new RequestUpdate(FetchedFileId: file));

        store.Prune(_time.GetUtcNow());

        Assert.Equal(file, req.Get("r1")!.FetchedFileId);
        Assert.Single(req.ListAll());
    }

    [Fact]
    public async Task RequestStore_writes_while_EncoreStore_and_FetchStore_are_open_and_busy()
    {
        using var store = EncoreStore.Open(DbPath, _time);
        using var fetch = FetchStore.Open(DbPath);
        using var req = RequestStore.Open(DbPath);
        var now = _time.GetUtcNow();

        var requests = Task.Run(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                req.Insert(Song($"r{i}", "u1", now, $"k{i}"));
                req.Update($"r{i}", RequestState.Downloading, now);
            }
        });
        var fetches = Task.Run(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                var id = fetch.InsertFetched(Fetch($"j{i}", $"k{i}"));
                fetch.UpdateFetched(id, FetchState.Ready);
                fetch.MarkRequested($"k{i}");
            }
        });
        var prunes = Task.Run(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                store.Prune(now);
            }
        });

        await Task.WhenAll(requests, fetches, prunes);

        Assert.Equal(40, req.Active().Count);
        using var db = Raw();
        Assert.Equal(40, Scalar(db, "SELECT COUNT(*) FROM fetched_files WHERE requested = 1"));
    }
}
