using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Encore.Store;

/// <summary>Steps of a <c>promotions</c> row, in order (a CHECK constraint holds the same list).</summary>
public static class PromotionStep
{
    public const string Snapshot = "snapshot";
    public const string Copied = "copied";
    public const string SourceRemoved = "source_removed";
    public const string OldGone = "old_gone";
    public const string Placed = "placed";
    public const string Ingested = "ingested";
    public const string Verified = "verified";
    public const string Playlists = "playlists";
    public const string Done = "done";
}

/// <summary>Decisions of a <c>retention_decisions</c> row (a CHECK constraint holds the same list).</summary>
public static class RetentionDecision
{
    public const string Promote = "promote";
    public const string Keep = "keep";
    public const string Delete = "delete";
    public const string Wait = "wait";
    public const string Skip = "skip";
    public const string Untracked = "untracked";
}

/// <summary>A new <c>promotions</c> row, written at step <c>snapshot</c>.</summary>
public sealed record NewPromotion(long FetchedFileId, string OldItemId, DateTimeOffset StartedUtc, string? TargetPath = null, string? TargetJellyfinPath = null);

/// <summary>Fields to change on a promotion. A null field is left as it is; <see cref="ClearLastError"/> empties <c>last_error</c>.</summary>
public sealed record PromotionUpdate(
    string? StagedPath = null,
    string? TargetPath = null,
    string? TargetJellyfinPath = null,
    string? Sha256 = null,
    string? NewItemId = null,
    int? Attempts = null,
    string? LastError = null,
    bool ClearLastError = false,
    bool? Stalled = null);

/// <summary>One <c>promotions</c> row.</summary>
public sealed record PromotionRow(
    long FetchedFileId,
    string Step,
    bool Stalled,
    string? StagedPath,
    string? TargetPath,
    string? TargetJellyfinPath,
    string? Sha256,
    string OldItemId,
    string? NewItemId,
    int Attempts,
    string? LastError,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>A playlist that held the old item. <see cref="CandidateUserIds"/> are the users whose list holds it (no API gives the owner).</summary>
public sealed record NewPromotionEntry(string PlaylistId, IReadOnlyList<string> CandidateUserIds, int Position);

/// <summary>One <c>promotion_entries</c> row.</summary>
public sealed record PromotionEntryRow(
    long Id, long FetchedFileId, string PlaylistId, IReadOnlyList<string> CandidateUserIds, int Position,
    string? EditorUserId, DateTimeOffset? ReaddedUtc);

/// <summary>A user's favorite flag and play count for the old item, saved before promotion.</summary>
public sealed record PromotionUserData(string UserId, bool IsFavorite, int PlayCount);

/// <summary>A listener-started play-through of a fetched song (<c>play_throughs</c>).</summary>
public sealed record NewPlayThrough(long FetchedFileId, string ItemId, string UserId, string SessionId, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc);

/// <summary>One line of the retention decision log.</summary>
public sealed record RetentionDecisionRecord(
    string PassId,
    DateTimeOffset AtUtc,
    string Decision,
    bool DryRun,
    long? FetchedFileId = null,
    string? Path = null,
    string? Reason = null,
    double? AgeDays = null,
    int? Uploads = null,
    int? Plays = null,
    string? TargetPath = null);

/// <summary>
/// The retention feature's part of the database (v0.4 design section 9): its own connection to the same file as
/// <see cref="EncoreStore"/>, same pragmas, one lock. Autocommit statements only: no transaction is held across calls,
/// so it never holds a lock that EncoreStore's BEGIN IMMEDIATE blocks wait on. It never migrates.
/// </summary>
public sealed class RetentionStore : IDisposable
{
    public const string EventsWatermarkKey = "slskd_events_watermark";
    public const string LastRescanKey = "slskd_last_rescan_utc";

    /// <summary>The decision log keeps 90 days; uploads and play-throughs are never pruned.</summary>
    public static readonly TimeSpan DecisionsRetention = TimeSpan.FromDays(90);

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    private RetentionStore(SqliteConnection db) => _db = db;

    /// <summary>Opens the existing database. Throws <see cref="StoreException"/> unless user_version equals <see cref="EncoreStore.SchemaVersion"/>.</summary>
    public static RetentionStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);

        try
        {
            db.Open();
            using (var pragmas = db.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL; PRAGMA journal_mode = DELETE;";
                pragmas.ExecuteNonQuery();
            }

            using var ver = db.CreateCommand();
            ver.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(ver.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version != EncoreStore.SchemaVersion)
            {
                throw new StoreException($"database schema {version} does not match this build ({EncoreStore.SchemaVersion}); open the main store first");
            }

            return new RetentionStore(db);
        }
        catch (StoreException)
        {
            db.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            db.Dispose();
            throw new StoreException($"could not open the retention store at {path}: {ex.GetType().Name}", ex);
        }
    }

    // ---- slskd uploads ----

    /// <summary>
    /// Records one completed upload (once per transfer id) and recounts <c>fetched_files.uploads</c> for the row. The recount
    /// is idempotent, so a crash between the two statements is repaired by the next call. Returns false when the transfer was already recorded.
    /// </summary>
    public bool AddUpload(string transferId, long fetchedFileId, DateTimeOffset completedUtc, DateTimeOffset readUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO slskd_uploads (transfer_id, fetched_file_id, completed_utc, read_utc) VALUES ($t, $file, $completed, $read)";
            cmd.Parameters.AddWithValue("$t", transferId);
            cmd.Parameters.AddWithValue("$file", fetchedFileId);
            cmd.Parameters.AddWithValue("$completed", EncoreStore.FormatUtc(completedUtc));
            cmd.Parameters.AddWithValue("$read", EncoreStore.FormatUtc(readUtc));
            var inserted = cmd.ExecuteNonQuery() > 0;
            Recount(fetchedFileId);
            return inserted;
        }
    }

    /// <summary>How many completed uploads are recorded for the row.</summary>
    public int UploadCount(long fetchedFileId)
    {
        lock (_gate)
        {
            return CountFor("slskd_uploads", fetchedFileId);
        }
    }

    // ---- play-throughs ----

    /// <summary>Records one play-through (unique per session, item and start time) and recounts <c>fetched_files.play_throughs</c>. Returns false when it was already recorded.</summary>
    public bool AddPlayThrough(NewPlayThrough p)
    {
        ArgumentNullException.ThrowIfNull(p);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO play_throughs (fetched_file_id, item_id, user_id, session_id, started_utc, ended_utc)
                VALUES ($file, $item, $user, $session, $started, $ended)
                """;
            cmd.Parameters.AddWithValue("$file", p.FetchedFileId);
            cmd.Parameters.AddWithValue("$item", p.ItemId);
            cmd.Parameters.AddWithValue("$user", p.UserId);
            cmd.Parameters.AddWithValue("$session", p.SessionId);
            cmd.Parameters.AddWithValue("$started", EncoreStore.FormatUtc(p.StartedUtc));
            cmd.Parameters.AddWithValue("$ended", EncoreStore.FormatUtc(p.EndedUtc));
            var inserted = cmd.ExecuteNonQuery() > 0;
            Recount(p.FetchedFileId);
            return inserted;
        }
    }

    /// <summary>How many play-throughs are recorded for the row.</summary>
    public int PlayThroughCount(long fetchedFileId)
    {
        lock (_gate)
        {
            return CountFor("play_throughs", fetchedFileId);
        }
    }

    /// <summary>
    /// Whether Encore sent the item to the session at or after <paramref name="since"/>: any <c>added_items</c> row (written only for
    /// sent and sent-without-reply outcomes, so never for a failed send). Read by <c>PlayTally</c> to tell radio plays apart (v04 §4.4).
    /// </summary>
    public bool WasSentToSession(string sessionId, Guid itemId, DateTimeOffset since)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM added_items WHERE session_id = $session AND item_id = $item AND added_utc >= $since LIMIT 1";
            cmd.Parameters.AddWithValue("$session", sessionId);
            cmd.Parameters.AddWithValue("$item", itemId.ToString("N"));
            cmd.Parameters.AddWithValue("$since", EncoreStore.FormatUtc(since));
            return cmd.ExecuteScalar() is not null;
        }
    }

    // ---- promotions ----

    /// <summary>Starts a promotion at step <c>snapshot</c>. Returns false when the row already has one.</summary>
    public bool StartPromotion(NewPromotion p)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentException.ThrowIfNullOrWhiteSpace(p.OldItemId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO promotions (fetched_file_id, step, target_path, target_jellyfin_path, old_item_id, started_utc, updated_utc)
                VALUES ($file, 'snapshot', $target, $jtarget, $old, $at, $at)
                """;
            cmd.Parameters.AddWithValue("$file", p.FetchedFileId);
            cmd.Parameters.AddWithValue("$target", Db(p.TargetPath));
            cmd.Parameters.AddWithValue("$jtarget", Db(p.TargetJellyfinPath));
            cmd.Parameters.AddWithValue("$old", p.OldItemId);
            cmd.Parameters.AddWithValue("$at", EncoreStore.FormatUtc(p.StartedUtc));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Sets the step, <c>updated_utc</c> and any non-null field. Returns false when the row has no promotion.</summary>
    public bool UpdatePromotion(long fetchedFileId, string step, DateTimeOffset now, PromotionUpdate? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        fields ??= new PromotionUpdate();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE promotions SET
                  step = $step, updated_utc = $now,
                  staged_path = COALESCE($staged, staged_path), target_path = COALESCE($target, target_path),
                  target_jellyfin_path = COALESCE($jtarget, target_jellyfin_path), sha256 = COALESCE($sha, sha256),
                  new_item_id = COALESCE($new, new_item_id), attempts = COALESCE($attempts, attempts),
                  last_error = CASE WHEN $clearError = 1 THEN NULL ELSE COALESCE($error, last_error) END,
                  stalled = COALESCE($stalled, stalled)
                WHERE fetched_file_id = $file
                """;
            cmd.Parameters.AddWithValue("$file", fetchedFileId);
            cmd.Parameters.AddWithValue("$step", step);
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            cmd.Parameters.AddWithValue("$staged", Db(fields.StagedPath));
            cmd.Parameters.AddWithValue("$target", Db(fields.TargetPath));
            cmd.Parameters.AddWithValue("$jtarget", Db(fields.TargetJellyfinPath));
            cmd.Parameters.AddWithValue("$sha", Db(fields.Sha256));
            cmd.Parameters.AddWithValue("$new", Db(fields.NewItemId));
            cmd.Parameters.AddWithValue("$attempts", Db(fields.Attempts));
            cmd.Parameters.AddWithValue("$clearError", fields.ClearLastError ? 1 : 0);
            cmd.Parameters.AddWithValue("$error", Db(fields.LastError));
            cmd.Parameters.AddWithValue("$stalled", fields.Stalled is { } s ? (s ? 1 : 0) : DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>The promotion of one row, or null.</summary>
    public PromotionRow? GetPromotion(long fetchedFileId) => QueryPromotions("WHERE fetched_file_id = $file", c => c.Parameters.AddWithValue("$file", fetchedFileId)).FirstOrDefault();

    /// <summary>Promotions that are not <c>done</c>, oldest first (the work to resume after a restart).</summary>
    public IReadOnlyList<PromotionRow> ActivePromotions() => QueryPromotions("WHERE step <> 'done' ORDER BY started_utc, fetched_file_id", null);

    // ---- promotion entries (playlists that held the old item) ----

    /// <summary>Records the playlists that held the old item. A playlist already recorded for the row is left as it is, so a repeat after a crash adds nothing.</summary>
    public void AddEntries(long fetchedFileId, IReadOnlyList<NewPromotionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            foreach (var e in entries)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO promotion_entries (fetched_file_id, playlist_id, candidate_user_ids, position)
                    SELECT $file, $playlist, $users, $pos
                    WHERE NOT EXISTS (SELECT 1 FROM promotion_entries WHERE fetched_file_id = $file AND playlist_id = $playlist)
                    """;
                cmd.Parameters.AddWithValue("$file", fetchedFileId);
                cmd.Parameters.AddWithValue("$playlist", e.PlaylistId);
                cmd.Parameters.AddWithValue("$users", string.Join(',', e.CandidateUserIds));
                cmd.Parameters.AddWithValue("$pos", e.Position);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>The recorded playlists of one row, highest position first (the order the re-adds run in).</summary>
    public IReadOnlyList<PromotionEntryRow> Entries(long fetchedFileId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, fetched_file_id, playlist_id, candidate_user_ids, position, editor_user_id, readded_utc
                FROM promotion_entries WHERE fetched_file_id = $file ORDER BY position DESC, id
                """;
            cmd.Parameters.AddWithValue("$file", fetchedFileId);
            var rows = new List<PromotionEntryRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new PromotionEntryRow(
                    r.GetInt64(0), r.GetInt64(1), r.GetString(2),
                    r.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries),
                    r.GetInt32(4), Str(r, 5), Time(r, 6)));
            }

            return rows;
        }
    }

    /// <summary>Records which candidate's write was accepted for the entry.</summary>
    public bool SetEditor(long entryId, string editorUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editorUserId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE promotion_entries SET editor_user_id = $user WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", entryId);
            cmd.Parameters.AddWithValue("$user", editorUserId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Marks the entry as re-added.</summary>
    public bool MarkReadded(long entryId, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE promotion_entries SET readded_utc = $now WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", entryId);
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ---- promotion user data ----

    /// <summary>Saves (or overwrites) one user's favorite flag and play count for the old item.</summary>
    public void SaveUserData(long fetchedFileId, PromotionUserData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO promotion_userdata (fetched_file_id, user_id, is_favorite, play_count) VALUES ($file, $user, $fav, $plays)
                ON CONFLICT(fetched_file_id, user_id) DO UPDATE SET is_favorite = excluded.is_favorite, play_count = excluded.play_count
                """;
            cmd.Parameters.AddWithValue("$file", fetchedFileId);
            cmd.Parameters.AddWithValue("$user", data.UserId);
            cmd.Parameters.AddWithValue("$fav", data.IsFavorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$plays", data.PlayCount);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The saved user data of one row, ordered by user id.</summary>
    public IReadOnlyList<PromotionUserData> UserData(long fetchedFileId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT user_id, is_favorite, play_count FROM promotion_userdata WHERE fetched_file_id = $file ORDER BY user_id";
            cmd.Parameters.AddWithValue("$file", fetchedFileId);
            var rows = new List<PromotionUserData>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new PromotionUserData(r.GetString(0), r.GetInt32(1) != 0, r.GetInt32(2)));
            }

            return rows;
        }
    }

    // ---- decision log ----

    /// <summary>Writes one decision line and returns its id.</summary>
    public long AddDecision(RetentionDecisionRecord d)
    {
        ArgumentNullException.ThrowIfNull(d);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO retention_decisions (pass_id, at_utc, fetched_file_id, path, decision, reason, age_days, uploads, plays, target_path, dry_run)
                VALUES ($pass, $at, $file, $path, $decision, $reason, $age, $uploads, $plays, $target, $dry);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$pass", d.PassId);
            cmd.Parameters.AddWithValue("$at", EncoreStore.FormatUtc(d.AtUtc));
            cmd.Parameters.AddWithValue("$file", d.FetchedFileId is { } f ? f : DBNull.Value);
            cmd.Parameters.AddWithValue("$path", Db(d.Path));
            cmd.Parameters.AddWithValue("$decision", d.Decision);
            cmd.Parameters.AddWithValue("$reason", Db(d.Reason));
            cmd.Parameters.AddWithValue("$age", d.AgeDays is { } a ? a : DBNull.Value);
            cmd.Parameters.AddWithValue("$uploads", d.Uploads is { } u ? u : DBNull.Value);
            cmd.Parameters.AddWithValue("$plays", d.Plays is { } p ? p : DBNull.Value);
            cmd.Parameters.AddWithValue("$target", Db(d.TargetPath));
            cmd.Parameters.AddWithValue("$dry", d.DryRun ? 1 : 0);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The decision lines of one pass, in the order written.</summary>
    public IReadOnlyList<RetentionDecisionRecord> Decisions(string passId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT pass_id, at_utc, decision, dry_run, fetched_file_id, path, reason, age_days, uploads, plays, target_path
                FROM retention_decisions WHERE pass_id = $pass ORDER BY id
                """;
            cmd.Parameters.AddWithValue("$pass", passId);
            var rows = new List<RetentionDecisionRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new RetentionDecisionRecord(
                    r.GetString(0), Time(r, 1)!.Value, r.GetString(2), r.GetInt32(3) != 0,
                    r.IsDBNull(4) ? null : r.GetInt64(4), Str(r, 5), Str(r, 6),
                    r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetInt32(8),
                    r.IsDBNull(9) ? null : r.GetInt32(9), Str(r, 10)));
            }

            return rows;
        }
    }

    /// <summary>Deletes decision lines older than 90 days. Uploads and play-throughs are evidence and are never pruned. Returns the rows removed.</summary>
    public int PruneDecisions(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM retention_decisions WHERE at_utc < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", EncoreStore.FormatUtc(now - DecisionsRetention));
            return cmd.ExecuteNonQuery();
        }
    }

    // ---- meta: the slskd events watermark and the last share rescan ----

    /// <summary>The id of the last slskd event read, or null.</summary>
    public string? GetEventsWatermark() => GetMeta(EventsWatermarkKey);

    public void SetEventsWatermark(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        SetMeta(EventsWatermarkKey, value);
    }

    /// <summary>When Encore last asked slskd to rescan its shares, or null.</summary>
    public DateTimeOffset? GetLastRescan() =>
        GetMeta(LastRescanKey) is { } s
            ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            : null;

    public void SetLastRescan(DateTimeOffset at) => SetMeta(LastRescanKey, EncoreStore.FormatUtc(at));

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }

    private string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    private void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }

    // Callers hold _gate.
    private int CountFor(string table, long fetchedFileId)
    {
        using var cmd = _db.CreateCommand();
        // The table name is a fixed constant from this file, never user input.
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE fetched_file_id = $file";
        cmd.Parameters.AddWithValue("$file", fetchedFileId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Callers hold _gate. One statement, so it is idempotent after a crash.
    private void Recount(long fetchedFileId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE fetched_files SET
              uploads = (SELECT COUNT(*) FROM slskd_uploads WHERE fetched_file_id = $file),
              play_throughs = (SELECT COUNT(*) FROM play_throughs WHERE fetched_file_id = $file)
            WHERE id = $file
            """;
        cmd.Parameters.AddWithValue("$file", fetchedFileId);
        cmd.ExecuteNonQuery();
    }

    private List<PromotionRow> QueryPromotions(string tail, Action<SqliteCommand>? bind)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT fetched_file_id, step, stalled, staged_path, target_path, target_jellyfin_path, sha256, old_item_id,
                  new_item_id, attempts, last_error, started_utc, updated_utc
                FROM promotions {tail}
                """;
            bind?.Invoke(cmd);
            var rows = new List<PromotionRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new PromotionRow(
                    r.GetInt64(0), r.GetString(1), r.GetInt32(2) != 0, Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6),
                    r.GetString(7), Str(r, 8), r.GetInt32(9), Str(r, 10), Time(r, 11)!.Value, Time(r, 12)!.Value));
            }

            return rows;
        }
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? Time(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static object Db(int? value) => value is { } v ? v : DBNull.Value;
}
