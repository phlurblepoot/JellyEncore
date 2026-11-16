using System.Globalization;
using Encore.Config;
using Encore.Picks;
using Encore.Trigger;
using Microsoft.Data.Sqlite;

namespace Encore.Store;

/// <summary>The store could not be opened or migrated (exit code 3 at startup).</summary>
public sealed class StoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One trigger outcome as written to <c>triggers</c>.</summary>
/// <param name="Outcome">sent | sent_unconfirmed | dry_run | no_picks | failed.</param>
/// <param name="Kind">start | topup | resend (T28).</param>
public sealed record TriggerRecord(
    DateTimeOffset AtUtc,
    string SessionId,
    Guid UserId,
    string? Client,
    string QueueModel,
    string Kind,
    Guid NowPlayingItemId,
    string? PlaylistItemId,
    string DedupeKey,
    string Outcome,
    string RunId,
    int DurationMs,
    int? HttpStatus = null,
    string? Error = null);

/// <summary>What the store knows about a session's latest radio run.</summary>
/// <param name="AddedIds">Every song the run added (re-sends included), oldest first (T28: the run's <c>addedIds</c>).</param>
public sealed record StoredRun(string RunId, Guid LastAddedItemId, int AddedCount, IReadOnlyList<Guid>? AddedIds = null);

/// <summary>Rows removed by one prune pass.</summary>
public sealed record PruneResult(int Plays, int AddedItems, int Triggers, int Attempts = 0, int Negative = 0);

/// <summary>
/// SQLite store at <c>/data/encore.db</c> (design section 5). One connection, used only by the orchestrator loop (the fetch worker uses <see cref="FetchStore"/> on its own connection).
/// Journal in DELETE mode (not WAL): the folder may sit on Unraid's FUSE path, and the write rate is tiny.
/// </summary>
public sealed class EncoreStore : IDisposable
{
    /// <summary>The schema this build writes. A database with a higher user_version refuses to open.</summary>
    public const int SchemaVersion = 5;

    /// <summary>Trigger outcomes that block a repeat fire with the same dedupe key.</summary>
    public const string SentOutcome = "sent";
    public const string DryRunOutcome = "dry_run";

    /// <summary>
    /// PlayLast was sent but no answer came back (a timeout, or a connection that broke after sending), so Jellyfin may
    /// have queued the songs. Treated like <see cref="SentOutcome"/>: it writes added songs and blocks a repeat, and the
    /// delivery check decides whether the songs arrived.
    /// </summary>
    public const string SentNoReplyOutcome = "sent_unconfirmed";

    /// <summary>Retention (design section 5): plays 14 days, triggers and added songs 90 days.</summary>
    public static readonly TimeSpan PlaysRetention = TimeSpan.FromDays(14);
    public static readonly TimeSpan TriggersRetention = TimeSpan.FromDays(90);

    /// <summary>Migrations in order. Index 0 is schema version 1.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE sessions (
          session_id TEXT PRIMARY KEY, user_id TEXT NOT NULL, client TEXT, device_name TEXT,
          first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL);
        CREATE TABLE triggers (
          id INTEGER PRIMARY KEY, at_utc TEXT NOT NULL, session_id TEXT NOT NULL, user_id TEXT NOT NULL,
          client TEXT, queue_model TEXT NOT NULL,
          kind TEXT NOT NULL,
          now_playing_item_id TEXT NOT NULL, playlist_item_id TEXT, dedupe_key TEXT NOT NULL,
          outcome TEXT NOT NULL,
          item_ids TEXT,
          sources TEXT,
          http_status INTEGER, error TEXT, duration_ms INTEGER, run_id TEXT NOT NULL);
        CREATE INDEX ix_triggers_dedupe ON triggers(dedupe_key, at_utc);
        CREATE INDEX ix_triggers_session ON triggers(session_id, at_utc);
        CREATE TABLE added_items (
          id INTEGER PRIMARY KEY, trigger_id INTEGER NOT NULL REFERENCES triggers(id),
          session_id TEXT NOT NULL, user_id TEXT NOT NULL, run_id TEXT NOT NULL,
          item_id TEXT NOT NULL, batch_pos INTEGER NOT NULL, source TEXT NOT NULL,
          added_utc TEXT NOT NULL, confirmed_utc TEXT, unconfirmed_utc TEXT);
        CREATE INDEX ix_added_session ON added_items(session_id, added_utc);
        CREATE TABLE plays (
          id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, user_id TEXT NOT NULL, item_id TEXT NOT NULL,
          artist_id TEXT, kind TEXT NOT NULL,
          at_utc TEXT NOT NULL);
        CREATE INDEX ix_plays_session ON plays(session_id, at_utc);
        CREATE INDEX ix_plays_user ON plays(user_id, at_utc);
        """,
        """
        CREATE TABLE fetched_files (
          id INTEGER PRIMARY KEY,
          job_id TEXT NOT NULL,
          track_key TEXT NOT NULL,
          artist TEXT NOT NULL, title TEXT NOT NULL, album TEXT, year INTEGER, track_no INTEGER, disc_no INTEGER,
          isrc TEXT, deezer_track_id INTEGER, mb_artist_id TEXT, mb_recording_id TEXT, duration_s INTEGER,
          origin TEXT NOT NULL,
          source TEXT NOT NULL,
          peer TEXT, remote_path TEXT, transfer_id TEXT,
          format TEXT, bitrate_kbps INTEGER, sample_rate INTEGER, bit_depth INTEGER, size_bytes INTEGER,
          path TEXT, jellyfin_path TEXT, jellyfin_item_id TEXT,
          requested_utc TEXT NOT NULL, fetched_utc TEXT, imported_utc TEXT, ready_utc TEXT,
          state TEXT NOT NULL CHECK (state IN ('downloading','importing','imported','ready','failed','deleted','promoted')),
          fail_reason TEXT,
          run_id TEXT NOT NULL, session_id TEXT NOT NULL, user_id TEXT NOT NULL,
          uploads INTEGER NOT NULL DEFAULT 0,
          kept_reason TEXT,
          deleted_utc TEXT);
        CREATE INDEX ix_fetched_key ON fetched_files(track_key);
        CREATE INDEX ix_fetched_state ON fetched_files(state, requested_utc);
        CREATE UNIQUE INDEX ix_fetched_path ON fetched_files(path) WHERE path IS NOT NULL AND state NOT IN ('failed','deleted');
        CREATE TABLE fetch_attempts (
          id INTEGER PRIMARY KEY,
          job_id TEXT NOT NULL, track_key TEXT NOT NULL, fetched_file_id INTEGER REFERENCES fetched_files(id),
          source TEXT NOT NULL, query TEXT, peer TEXT, filename TEXT,
          outcome TEXT NOT NULL CHECK (outcome IN ('no_results','rejected','stalled','failed','cancelled','ok','dry_run')),
          reason TEXT, score_eta_s REAL, at_utc TEXT NOT NULL, duration_ms INTEGER);
        CREATE INDEX ix_attempts_at ON fetch_attempts(at_utc);
        CREATE INDEX ix_attempts_key ON fetch_attempts(track_key, at_utc);
        CREATE TABLE fetch_negative (
          track_key TEXT PRIMARY KEY, until_utc TEXT NOT NULL, reason TEXT NOT NULL, misses INTEGER NOT NULL DEFAULT 1);
        """,
        """
        ALTER TABLE fetched_files ADD COLUMN requested INTEGER NOT NULL DEFAULT 0 CHECK (requested IN (0,1));
        CREATE TABLE requests (
          id TEXT PRIMARY KEY,
          kind TEXT NOT NULL CHECK (kind IN ('song','album')),
          user_id TEXT NOT NULL, user_name TEXT NOT NULL,
          state TEXT NOT NULL CHECK (state IN ('queued','upcoming','searching','downloading','importing','ready','failed','cancelled')),
          detail TEXT, fail_reason TEXT,
          artist TEXT NOT NULL, title TEXT, album TEXT, year INTEGER, cover_url TEXT,
          track_key TEXT, deezer_track_id INTEGER, duration_s INTEGER,
          foreign_album_id TEXT, foreign_artist_id TEXT,
          lidarr_artist_id INTEGER, lidarr_album_id INTEGER, tracks_done INTEGER, tracks_total INTEGER,
          fetched_file_id INTEGER REFERENCES fetched_files(id), jellyfin_item_id TEXT,
          attempts INTEGER NOT NULL DEFAULT 0, next_try_utc TEXT, cancelled_by TEXT,
          retries INTEGER NOT NULL DEFAULT 0, album_was_monitored INTEGER, artist_was_monitored INTEGER,
          created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, ready_utc TEXT, notified_utc TEXT);
        CREATE INDEX ix_requests_user ON requests(user_id, created_utc);
        CREATE INDEX ix_requests_state ON requests(state, updated_utc);
        CREATE INDEX ix_requests_key ON requests(track_key) WHERE track_key IS NOT NULL;
        """,
        """
        CREATE TABLE page_tokens (
          token_hash TEXT PRIMARY KEY,
          user_id TEXT NOT NULL,
          created_utc TEXT NOT NULL,
          last_used_utc TEXT NOT NULL);
        CREATE INDEX ix_page_tokens_user ON page_tokens(user_id);
        """,
        """
        ALTER TABLE fetched_files ADD COLUMN kept_utc TEXT;
        ALTER TABLE fetched_files ADD COLUMN promoted_utc TEXT;
        ALTER TABLE fetched_files ADD COLUMN radio_path TEXT;
        ALTER TABLE fetched_files ADD COLUMN delete_reason TEXT;
        ALTER TABLE fetched_files ADD COLUMN play_throughs INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE slskd_uploads (
          transfer_id TEXT PRIMARY KEY, fetched_file_id INTEGER NOT NULL REFERENCES fetched_files(id),
          completed_utc TEXT NOT NULL, read_utc TEXT NOT NULL);
        CREATE INDEX ix_uploads_file ON slskd_uploads(fetched_file_id);
        CREATE TABLE play_throughs (
          id INTEGER PRIMARY KEY, fetched_file_id INTEGER NOT NULL REFERENCES fetched_files(id),
          item_id TEXT NOT NULL, user_id TEXT NOT NULL, session_id TEXT NOT NULL, started_utc TEXT NOT NULL,
          ended_utc TEXT NOT NULL, UNIQUE (session_id, item_id, started_utc));
        CREATE TABLE promotions (
          fetched_file_id INTEGER PRIMARY KEY REFERENCES fetched_files(id),
          step TEXT NOT NULL CHECK (step IN ('snapshot','copied','source_removed','old_gone','placed','ingested','verified','playlists','done')),
          stalled INTEGER NOT NULL DEFAULT 0 CHECK (stalled IN (0,1)),
          staged_path TEXT, target_path TEXT, target_jellyfin_path TEXT, sha256 TEXT, old_item_id TEXT NOT NULL, new_item_id TEXT,
          attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT, started_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
        CREATE TABLE promotion_entries (
          id INTEGER PRIMARY KEY, fetched_file_id INTEGER NOT NULL REFERENCES fetched_files(id),
          playlist_id TEXT NOT NULL,
          candidate_user_ids TEXT NOT NULL,
          position INTEGER NOT NULL,
          editor_user_id TEXT,
          readded_utc TEXT);
        CREATE TABLE promotion_userdata (
          fetched_file_id INTEGER NOT NULL REFERENCES fetched_files(id), user_id TEXT NOT NULL,
          is_favorite INTEGER NOT NULL, play_count INTEGER NOT NULL, PRIMARY KEY (fetched_file_id, user_id));
        CREATE TABLE retention_decisions (
          id INTEGER PRIMARY KEY, pass_id TEXT NOT NULL, at_utc TEXT NOT NULL,
          fetched_file_id INTEGER, path TEXT,
          decision TEXT NOT NULL CHECK (decision IN ('promote','keep','delete','wait','skip','untracked')),
          reason TEXT, age_days REAL, uploads INTEGER, plays INTEGER, target_path TEXT, dry_run INTEGER NOT NULL);
        CREATE INDEX ix_decisions_pass ON retention_decisions(pass_id);
        """,
    ];

    private readonly SqliteConnection _db;

    private EncoreStore(string path, SqliteConnection db, int schemaVersion)
    {
        Path = path;
        _db = db;
        SchemaVersionOnDisk = schemaVersion;
    }

    public string Path { get; }

    /// <summary>The user_version after migration (equals <see cref="SchemaVersion"/> for this build).</summary>
    public int SchemaVersionOnDisk { get; }

    /// <summary>Opens (creating if needed) the database, applies pragmas and migrations. Throws <see cref="StoreException"/>.</summary>
    public static EncoreStore Open(string path, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString);

        try
        {
            db.Open();
            Execute(db, "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA journal_mode = DELETE;");

            var version = ReadUserVersion(db);
            if (version > SchemaVersion)
            {
                throw new StoreException($"database schema {version} is newer than this build supports ({SchemaVersion}); refusing to start");
            }

            if (version < SchemaVersion)
            {
                if (version >= 1)
                {
                    BackupBeforeMigration(db, path, version);
                }

                using var tx = db.BeginTransaction();
                for (var target = version + 1; target <= SchemaVersion; target++)
                {
                    using var script = db.CreateCommand();
                    script.Transaction = tx;
                    script.CommandText = Migrations[target - 1];
                    script.ExecuteNonQuery();
                    using var set = db.CreateCommand();
                    set.Transaction = tx;
                    set.CommandText = $"PRAGMA user_version = {target}";
                    set.ExecuteNonQuery();
                }

                using var meta = db.CreateCommand();
                meta.Transaction = tx;
                meta.CommandText = "INSERT OR IGNORE INTO meta (key, value) VALUES ('created_utc', $at)";
                meta.Parameters.AddWithValue("$at", FormatUtc(time.GetUtcNow()));
                meta.ExecuteNonQuery();
                tx.Commit();
            }

            return new EncoreStore(path, db, SchemaVersion);
        }
        catch (StoreException)
        {
            db.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            db.Dispose();
            throw new StoreException($"could not open the store at {path}: {ex.GetType().Name}", ex);
        }
    }

    /// <summary>True when a sent (answered or not) or dry-run trigger with this key exists within <paramref name="ttl"/> (design section 3, durable dedupe).</summary>
    public bool IsDuplicate(string dedupeKey, DateTimeOffset now, TimeSpan ttl)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM triggers
            WHERE dedupe_key = $key AND at_utc >= $since AND outcome IN ('sent', 'sent_unconfirmed', 'dry_run')
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$key", dedupeKey);
        cmd.Parameters.AddWithValue("$since", FormatUtc(now - ttl));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Writes one fire outcome. A <c>sent</c> or <c>sent_unconfirmed</c> outcome also writes the added songs (in send order) and their
    /// <c>plays</c> rows, all in one transaction. Returns the trigger id.
    /// </summary>
    public long RecordFire(TriggerRecord record, IReadOnlyList<PickedItem> items)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(items);

        using var tx = _db.BeginTransaction();
        var triggerId = InsertTrigger(record, items, tx);
        if (record.Outcome is SentOutcome or SentNoReplyOutcome)
        {
            var at = FormatUtc(record.AtUtc);
            for (var pos = 0; pos < items.Count; pos++)
            {
                var item = items[pos];
                using var added = _db.CreateCommand();
                added.Transaction = tx;
                added.CommandText = """
                    INSERT INTO added_items (trigger_id, session_id, user_id, run_id, item_id, batch_pos, source, added_utc)
                    VALUES ($trigger, $session, $user, $run, $item, $pos, $source, $at)
                    """;
                added.Parameters.AddWithValue("$trigger", triggerId);
                added.Parameters.AddWithValue("$session", record.SessionId);
                added.Parameters.AddWithValue("$user", Id(record.UserId));
                added.Parameters.AddWithValue("$run", record.RunId);
                added.Parameters.AddWithValue("$item", Id(item.Id));
                added.Parameters.AddWithValue("$pos", pos);
                added.Parameters.AddWithValue("$source", item.WireSource());
                added.Parameters.AddWithValue("$at", at);
                added.ExecuteNonQuery();

                InsertPlay(tx, record.SessionId, record.UserId, item.Id, item.ArtistId, "added", record.AtUtc);
            }
        }

        tx.Commit();
        return triggerId;
    }

    /// <summary>Writes a <c>played</c> row for a new play instance of an allow-listed user's song (design section 5).</summary>
    public void RecordPlayed(string sessionId, Guid userId, Guid itemId, Guid? artistId, DateTimeOffset at)
    {
        using var tx = _db.BeginTransaction();
        InsertPlay(tx, sessionId, userId, itemId, artistId, "played", at);
        tx.Commit();
    }

    /// <summary>Upserts the <c>sessions</c> row for an allow-listed user's session.</summary>
    public void UpsertSession(string sessionId, Guid userId, string? client, string? deviceName, DateTimeOffset at)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, user_id, client, device_name, first_seen_utc, last_seen_utc)
            VALUES ($session, $user, $client, $device, $at, $at)
            ON CONFLICT(session_id) DO UPDATE SET
              user_id = excluded.user_id, client = excluded.client,
              device_name = excluded.device_name, last_seen_utc = excluded.last_seen_utc
            """;
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$user", Id(userId));
        cmd.Parameters.AddWithValue("$client", (object?)client ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$device", (object?)deviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", FormatUtc(at));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets delivery state for every song of one sent trigger (design section 3, delivery check).</summary>
    public void MarkDelivery(long triggerId, bool confirmed, DateTimeOffset at)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = confirmed
            ? "UPDATE added_items SET confirmed_utc = $at WHERE trigger_id = $id"
            : "UPDATE added_items SET unconfirmed_utc = $at WHERE trigger_id = $id";
        cmd.Parameters.AddWithValue("$at", FormatUtc(at));
        cmd.Parameters.AddWithValue("$id", triggerId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// How many trigger rows have this outcome (sent, sent_unconfirmed, dry_run, no_picks or failed), optionally only
    /// of one kind (start, topup or resend).
    /// </summary>
    public int CountTriggers(string outcome, string? kind = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = kind is null
            ? "SELECT COUNT(*) FROM triggers WHERE outcome = $outcome"
            : "SELECT COUNT(*) FROM triggers WHERE outcome = $outcome AND kind = $kind";
        cmd.Parameters.AddWithValue("$outcome", outcome);
        if (kind is not null)
        {
            cmd.Parameters.AddWithValue("$kind", kind);
        }

        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Songs Encore added inside the window (all sessions when <paramref name="sessionId"/> is null).</summary>
    public int CountAddedSince(string? sessionId, DateTimeOffset since)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sessionId is null
            ? "SELECT COUNT(*) FROM added_items WHERE added_utc >= $since"
            : "SELECT COUNT(*) FROM added_items WHERE added_utc >= $since AND session_id = $session";
        cmd.Parameters.AddWithValue("$since", FormatUtc(since));
        if (sessionId is not null)
        {
            cmd.Parameters.AddWithValue("$session", sessionId);
        }

        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Plays and given songs that exclude picks (design section 4): every row of the session for a
    /// <c>session</c> window, or every row of the user since <c>now - duration</c>.
    /// </summary>
    public IReadOnlyList<RecentPlay> RecentPlays(RecentWindow window, string sessionId, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = window.IsSession
            ? "SELECT session_id, user_id, item_id, at_utc FROM plays WHERE session_id = $session"
            : "SELECT session_id, user_id, item_id, at_utc FROM plays WHERE user_id = $user AND at_utc >= $since";
        if (window.IsSession)
        {
            cmd.Parameters.AddWithValue("$session", sessionId);
        }
        else
        {
            cmd.Parameters.AddWithValue("$user", Id(userId));
            cmd.Parameters.AddWithValue("$since", FormatUtc(now - window.Duration!.Value));
        }

        var rows = new List<RecentPlay>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RecentPlay(
                reader.GetString(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)));
        }

        return rows;
    }

    /// <summary>The session's plays, newest first, for the picker's secondary seeds.</summary>
    public IReadOnlyList<HistorySong> SessionHistory(string sessionId, int limit)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT item_id, artist_id FROM plays WHERE session_id = $session ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<HistorySong>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var artist = reader.IsDBNull(1) ? (Guid?)null : Guid.ParseExact(reader.GetString(1), "N");
            rows.Add(new HistorySong(Guid.ParseExact(reader.GetString(0), "N"), artist));
        }

        return rows;
    }

    /// <summary>
    /// The session's latest radio run, if one added songs at or after <paramref name="since"/>: its id, the last song
    /// added, how many songs the run has added and their ids. Used after a restart (design section 3).
    /// </summary>
    public StoredRun? LatestRun(string sessionId, DateTimeOffset since)
    {
        using var last = _db.CreateCommand();
        last.CommandText = """
            SELECT run_id, item_id FROM added_items
            WHERE session_id = $session AND added_utc >= $since
            ORDER BY id DESC LIMIT 1
            """;
        last.Parameters.AddWithValue("$session", sessionId);
        last.Parameters.AddWithValue("$since", FormatUtc(since));
        string runId;
        Guid lastItem;
        using (var reader = last.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            runId = reader.GetString(0);
            lastItem = Guid.ParseExact(reader.GetString(1), "N");
        }

        using var all = _db.CreateCommand();
        all.CommandText = "SELECT item_id FROM added_items WHERE run_id = $run ORDER BY id";
        all.Parameters.AddWithValue("$run", runId);
        var ids = new List<Guid>();
        using (var reader = all.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
            }
        }

        return new StoredRun(runId, lastItem, ids.Count, ids);
    }

    /// <summary>Deletes rows past retention: plays after 14 days, added songs, triggers and fetch attempts after 90 days, expired negative-cache rows. Never <c>fetched_files</c>, <c>requests</c>, <c>slskd_uploads</c> or <c>play_throughs</c> (the retention evidence; <see cref="RetentionStore.PruneDecisions"/> trims only the decision log).</summary>
    public PruneResult Prune(DateTimeOffset now)
    {
        using var tx = _db.BeginTransaction();
        var plays = Delete(tx, "plays", "at_utc", now - PlaysRetention);
        var added = Delete(tx, "added_items", "added_utc", now - TriggersRetention);
        var triggers = Delete(tx, "triggers", "at_utc", now - TriggersRetention);
        var attempts = Delete(tx, "fetch_attempts", "at_utc", now - TriggersRetention);
        var negative = Delete(tx, "fetch_negative", "until_utc", now);
        tx.Commit();
        return new PruneResult(plays, added, triggers, attempts, negative);
    }

    /// <summary>A value stored in <c>meta</c>, or null.</summary>
    public string? GetMeta(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Dispose() => _db.Dispose();

    private static long InsertTrigger(TriggerRecord record, IReadOnlyList<PickedItem> items, SqliteTransaction tx)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO triggers (at_utc, session_id, user_id, client, queue_model, kind, now_playing_item_id,
              playlist_item_id, dedupe_key, outcome, item_ids, sources, http_status, error, duration_ms, run_id)
            VALUES ($at, $session, $user, $client, $model, $kind, $item, $playlist, $key, $outcome,
              $ids, $sources, $status, $error, $duration, $run);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$at", FormatUtc(record.AtUtc));
        cmd.Parameters.AddWithValue("$session", record.SessionId);
        cmd.Parameters.AddWithValue("$user", Id(record.UserId));
        cmd.Parameters.AddWithValue("$client", (object?)record.Client ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", record.QueueModel);
        cmd.Parameters.AddWithValue("$kind", record.Kind);
        cmd.Parameters.AddWithValue("$item", Id(record.NowPlayingItemId));
        cmd.Parameters.AddWithValue("$playlist", (object?)record.PlaylistItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", record.DedupeKey);
        cmd.Parameters.AddWithValue("$outcome", record.Outcome);
        cmd.Parameters.AddWithValue("$ids", items.Count == 0 ? DBNull.Value : string.Join(',', items.Select(i => Id(i.Id))));
        cmd.Parameters.AddWithValue("$sources", items.Count == 0 ? DBNull.Value : string.Join(',', items.Select(i => i.WireSource())));
        cmd.Parameters.AddWithValue("$status", (object?)record.HttpStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", record.DurationMs);
        cmd.Parameters.AddWithValue("$run", record.RunId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertPlay(SqliteTransaction tx, string sessionId, Guid userId, Guid itemId, Guid? artistId, string kind, DateTimeOffset at)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO plays (session_id, user_id, item_id, artist_id, kind, at_utc) VALUES ($session, $user, $item, $artist, $kind, $at)";
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$user", Id(userId));
        cmd.Parameters.AddWithValue("$item", Id(itemId));
        cmd.Parameters.AddWithValue("$artist", artistId is { } a ? Id(a) : DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$at", FormatUtc(at));
        cmd.ExecuteNonQuery();
    }

    private static int Delete(SqliteTransaction tx, string table, string column, DateTimeOffset cutoff)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        // Table and column names are fixed constants from this file, never user input.
        cmd.CommandText = $"DELETE FROM {table} WHERE {column} < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes <c>{path}.v{old}.bak</c> before a migration (rollback path: the older build refuses the newer schema). An existing backup is a
    /// leftover of an earlier migration (a rollback restored it, the old build ran for a while, now the new build migrates again), and
    /// the days on the old build exist only in the file about to change, so the old backup is renamed with the date it was written
    /// (<c>.bak.yyyyMMdd-HHmmss</c>, a counter if that name is taken) and a fresh one is made. VACUUM INTO makes a consistent copy
    /// through the open connection.
    /// </summary>
    private static void BackupBeforeMigration(SqliteConnection db, string path, int version)
    {
        var backup = $"{path}.v{version}.bak";
        if (File.Exists(backup))
        {
            var stamp = File.GetLastWriteTimeUtc(backup).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var renamed = $"{backup}.{stamp}";
            for (var n = 2; File.Exists(renamed); n++)
            {
                renamed = $"{backup}.{stamp}-{n}";
            }

            File.Move(backup, renamed);
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = "VACUUM INTO $target";
        cmd.Parameters.AddWithValue("$target", backup);
        cmd.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Ids are stored as 32 lowercase hex digits (the "N" form).</summary>
    private static string Id(Guid id) => id.ToString("N");

    /// <summary>Times are ISO-8601 UTC text with a fixed width, so string comparison orders them.</summary>
    internal static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
