using Encore.Config;
using Encore.Picks;
using Encore.Store;
using Encore.Tests.Trigger;
using Encore.Trigger;
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
    public void A_new_store_has_schema_1_and_reopens()
    {
        using (var store = EncoreStore.Open(Path_(), _time))
        {
            Assert.Equal(1, store.SchemaVersionOnDisk);
            Assert.NotNull(store.GetMeta("created_utc"));
        }

        using var again = EncoreStore.Open(Path_(), _time);
        Assert.Equal(1, again.SchemaVersionOnDisk);
    }

    [Fact]
    public void A_database_from_a_newer_build_is_refused()
    {
        using (var store = EncoreStore.Open(Path_(), _time))
        {
            Assert.Equal(1, store.SchemaVersionOnDisk);
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
}
