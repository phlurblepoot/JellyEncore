using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Encore.Store;

/// <summary>States of a <c>fetched_files</c> row (a CHECK constraint holds the same list).</summary>
public static class FetchState
{
    public const string Downloading = "downloading";
    public const string Importing = "importing";
    public const string Imported = "imported";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Deleted = "deleted";
    public const string Promoted = "promoted";
}

/// <summary>A new <c>fetched_files</c> row, written at the first download request (state downloading).</summary>
public sealed record NewFetchedFile(
    string JobId,
    string TrackKey,
    string Artist,
    string Title,
    string Origin,
    string Source,
    string RunId,
    string SessionId,
    Guid UserId,
    DateTimeOffset RequestedUtc,
    string? Album = null,
    int? Year = null,
    int? TrackNo = null,
    int? DiscNo = null,
    string? Isrc = null,
    long? DeezerTrackId = null,
    string? MbArtistId = null,
    string? MbRecordingId = null,
    int? DurationSeconds = null,
    string? Peer = null,
    string? RemotePath = null,
    string? TransferId = null);

/// <summary>Fields to change on a <c>fetched_files</c> row. A null field is left as it is.</summary>
public sealed record FetchedUpdate(
    string? Peer = null,
    string? RemotePath = null,
    string? TransferId = null,
    string? Format = null,
    int? BitrateKbps = null,
    int? SampleRate = null,
    int? BitDepth = null,
    long? SizeBytes = null,
    string? Path = null,
    string? JellyfinPath = null,
    string? JellyfinItemId = null,
    DateTimeOffset? FetchedUtc = null,
    DateTimeOffset? ImportedUtc = null,
    DateTimeOffset? ReadyUtc = null,
    DateTimeOffset? DeletedUtc = null,
    string? FailReason = null);

/// <summary>One <c>fetch_attempts</c> row. Outcome: no_results | rejected | stalled | failed | cancelled | ok | dry_run.</summary>
public sealed record FetchAttempt(
    string JobId,
    string TrackKey,
    string Source,
    string Outcome,
    DateTimeOffset AtUtc,
    long? FetchedFileId = null,
    string? Query = null,
    string? Peer = null,
    string? Filename = null,
    string? Reason = null,
    double? ScoreEtaSeconds = null,
    int? DurationMs = null);

/// <summary>A <c>fetched_files</c> row still in flight, read after a restart. <see cref="Peer"/> and <see cref="JellyfinPath"/> are null until known.</summary>
public sealed record ActiveFetch(
    long Id, string JobId, string TrackKey, string State, string? Path, string? TransferId, string RunId,
    string? Peer = null, string? JellyfinPath = null);

/// <summary>A <c>fetched_files</c> row as the retention pass reads it (v0.4).</summary>
public sealed record TrackedFile(
    long Id, string TrackKey, string Artist, string Title, string State,
    string? Path, string? JellyfinPath, string? JellyfinItemId, bool Requested, string? KeptReason, DateTimeOffset? KeptUtc,
    DateTimeOffset? PromotedUtc, string? RadioPath, string? DeleteReason, int Uploads, int PlayThroughs, DateTimeOffset? ReadyUtc,
    string UserId, string? Peer, string? RemotePath);

/// <summary>
/// The fetch worker's part of the database (design section 9 of v0.2): its own connection to the same file as
/// <see cref="EncoreStore"/>, same pragmas. Every method takes one lock, so any worker thread may call it. It never
/// migrates: <see cref="Open"/> refuses a file that <see cref="EncoreStore"/> has not brought to the current schema.
/// </summary>
public sealed class FetchStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    private FetchStore(SqliteConnection db) => _db = db;

    /// <summary>Opens the existing database. Throws <see cref="StoreException"/> unless user_version equals <see cref="EncoreStore.SchemaVersion"/>.</summary>
    public static FetchStore Open(string path)
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

            return new FetchStore(db);
        }
        catch (StoreException)
        {
            db.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            db.Dispose();
            throw new StoreException($"could not open the fetch store at {path}: {ex.GetType().Name}", ex);
        }
    }

    /// <summary>Inserts a row in state <c>downloading</c> and returns its id.</summary>
    public long InsertFetched(NewFetchedFile f)
    {
        ArgumentNullException.ThrowIfNull(f);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO fetched_files (job_id, track_key, artist, title, album, year, track_no, disc_no, isrc,
                  deezer_track_id, mb_artist_id, mb_recording_id, duration_s, origin, source, peer, remote_path, transfer_id,
                  requested_utc, state, run_id, session_id, user_id)
                VALUES ($job, $key, $artist, $title, $album, $year, $trackNo, $discNo, $isrc,
                  $deezer, $mbArtist, $mbRec, $duration, $origin, $source, $peer, $remote, $transfer,
                  $requested, 'downloading', $run, $session, $user);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$job", f.JobId);
            cmd.Parameters.AddWithValue("$key", f.TrackKey);
            cmd.Parameters.AddWithValue("$artist", f.Artist);
            cmd.Parameters.AddWithValue("$title", f.Title);
            cmd.Parameters.AddWithValue("$album", Db(f.Album));
            cmd.Parameters.AddWithValue("$year", Db(f.Year));
            cmd.Parameters.AddWithValue("$trackNo", Db(f.TrackNo));
            cmd.Parameters.AddWithValue("$discNo", Db(f.DiscNo));
            cmd.Parameters.AddWithValue("$isrc", Db(f.Isrc));
            cmd.Parameters.AddWithValue("$deezer", Db(f.DeezerTrackId));
            cmd.Parameters.AddWithValue("$mbArtist", Db(f.MbArtistId));
            cmd.Parameters.AddWithValue("$mbRec", Db(f.MbRecordingId));
            cmd.Parameters.AddWithValue("$duration", Db(f.DurationSeconds));
            cmd.Parameters.AddWithValue("$origin", f.Origin);
            cmd.Parameters.AddWithValue("$source", f.Source);
            cmd.Parameters.AddWithValue("$peer", Db(f.Peer));
            cmd.Parameters.AddWithValue("$remote", Db(f.RemotePath));
            cmd.Parameters.AddWithValue("$transfer", Db(f.TransferId));
            cmd.Parameters.AddWithValue("$requested", EncoreStore.FormatUtc(f.RequestedUtc));
            cmd.Parameters.AddWithValue("$run", f.RunId);
            cmd.Parameters.AddWithValue("$session", f.SessionId);
            cmd.Parameters.AddWithValue("$user", f.UserId.ToString("N"));
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Marks every non-failed row of <paramref name="trackKey"/> as requested by a user (<c>requested = 1</c>, <c>kept_reason = 'requested'</c>). Returns the rows changed.</summary>
    public int MarkRequested(string trackKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackKey);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE fetched_files SET requested = 1, kept_reason = 'requested' WHERE track_key = $key AND state <> 'failed'";
            cmd.Parameters.AddWithValue("$key", trackKey);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the state and any non-null field of one row. Returns false when the id does not exist.</summary>
    public bool UpdateFetched(long id, string state, FetchedUpdate? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        fields ??= new FetchedUpdate();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE fetched_files SET
                  state = $state,
                  peer = COALESCE($peer, peer), remote_path = COALESCE($remote, remote_path),
                  transfer_id = COALESCE($transfer, transfer_id), format = COALESCE($format, format),
                  bitrate_kbps = COALESCE($bitrate, bitrate_kbps), sample_rate = COALESCE($rate, sample_rate),
                  bit_depth = COALESCE($depth, bit_depth), size_bytes = COALESCE($size, size_bytes),
                  path = COALESCE($path, path), jellyfin_path = COALESCE($jpath, jellyfin_path),
                  jellyfin_item_id = COALESCE($jitem, jellyfin_item_id),
                  fetched_utc = COALESCE($fetched, fetched_utc), imported_utc = COALESCE($imported, imported_utc),
                  ready_utc = COALESCE($ready, ready_utc), deleted_utc = COALESCE($deleted, deleted_utc),
                  fail_reason = COALESCE($fail, fail_reason)
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$peer", Db(fields.Peer));
            cmd.Parameters.AddWithValue("$remote", Db(fields.RemotePath));
            cmd.Parameters.AddWithValue("$transfer", Db(fields.TransferId));
            cmd.Parameters.AddWithValue("$format", Db(fields.Format));
            cmd.Parameters.AddWithValue("$bitrate", Db(fields.BitrateKbps));
            cmd.Parameters.AddWithValue("$rate", Db(fields.SampleRate));
            cmd.Parameters.AddWithValue("$depth", Db(fields.BitDepth));
            cmd.Parameters.AddWithValue("$size", Db(fields.SizeBytes));
            cmd.Parameters.AddWithValue("$path", Db(fields.Path));
            cmd.Parameters.AddWithValue("$jpath", Db(fields.JellyfinPath));
            cmd.Parameters.AddWithValue("$jitem", Db(fields.JellyfinItemId));
            cmd.Parameters.AddWithValue("$fetched", Db(fields.FetchedUtc));
            cmd.Parameters.AddWithValue("$imported", Db(fields.ImportedUtc));
            cmd.Parameters.AddWithValue("$ready", Db(fields.ReadyUtc));
            cmd.Parameters.AddWithValue("$deleted", Db(fields.DeletedUtc));
            cmd.Parameters.AddWithValue("$fail", Db(fields.FailReason));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Writes one <c>fetch_attempts</c> row and returns its id.</summary>
    public long AddAttempt(FetchAttempt a)
    {
        ArgumentNullException.ThrowIfNull(a);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO fetch_attempts (job_id, track_key, fetched_file_id, source, query, peer, filename, outcome,
                  reason, score_eta_s, at_utc, duration_ms)
                VALUES ($job, $key, $file, $source, $query, $peer, $filename, $outcome, $reason, $eta, $at, $duration);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$job", a.JobId);
            cmd.Parameters.AddWithValue("$key", a.TrackKey);
            cmd.Parameters.AddWithValue("$file", Db(a.FetchedFileId));
            cmd.Parameters.AddWithValue("$source", a.Source);
            cmd.Parameters.AddWithValue("$query", Db(a.Query));
            cmd.Parameters.AddWithValue("$peer", Db(a.Peer));
            cmd.Parameters.AddWithValue("$filename", Db(a.Filename));
            cmd.Parameters.AddWithValue("$outcome", a.Outcome);
            cmd.Parameters.AddWithValue("$reason", Db(a.Reason));
            cmd.Parameters.AddWithValue("$eta", Db(a.ScoreEtaSeconds));
            cmd.Parameters.AddWithValue("$at", EncoreStore.FormatUtc(a.AtUtc));
            cmd.Parameters.AddWithValue("$duration", Db(a.DurationMs));
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>True when the track is on the negative cache and its <c>until_utc</c> is after <paramref name="now"/>.</summary>
    public bool IsNegative(string trackKey, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM fetch_negative WHERE track_key = $key AND until_utc > $now LIMIT 1";
            cmd.Parameters.AddWithValue("$key", trackKey);
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Adds the track to the negative cache. An existing row gets the new <c>until_utc</c>, the new reason and misses + 1.</summary>
    public void UpsertNegative(string trackKey, DateTimeOffset until, string reason)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO fetch_negative (track_key, until_utc, reason, misses) VALUES ($key, $until, $reason, 1)
                ON CONFLICT(track_key) DO UPDATE SET
                  until_utc = excluded.until_utc, reason = excluded.reason, misses = misses + 1
                """;
            cmd.Parameters.AddWithValue("$key", trackKey);
            cmd.Parameters.AddWithValue("$until", EncoreStore.FormatUtc(until));
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every track key on the negative cache that has not expired (the caller caches this for about 60 s).</summary>
    public IReadOnlySet<string> NegativeKeys(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT track_key FROM fetch_negative WHERE until_utc > $now";
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                keys.Add(reader.GetString(0));
            }

            return keys;
        }
    }

    /// <summary>How many downloads were requested at or after <paramref name="since"/> (any state; the hourly cap window).</summary>
    public int CountFetchesSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM fetched_files WHERE requested_utc >= $since";
            cmd.Parameters.AddWithValue("$since", EncoreStore.FormatUtc(since));
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The <c>requested_utc</c> of every row requested at or after <paramref name="since"/>, oldest first (seeds the hourly cap window).</summary>
    public IReadOnlyList<DateTimeOffset> FetchTimesSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT requested_utc FROM fetched_files WHERE requested_utc >= $since ORDER BY requested_utc";
            cmd.Parameters.AddWithValue("$since", EncoreStore.FormatUtc(since));
            var times = new List<DateTimeOffset>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                times.Add(DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
            }

            return times;
        }
    }

    /// <summary>Rows in state downloading, importing or imported, oldest first: the work to settle after a restart.</summary>
    public IReadOnlyList<ActiveFetch> ActiveForRestart()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, job_id, track_key, state, path, transfer_id, run_id, peer, jellyfin_path FROM fetched_files
                WHERE state IN ('downloading', 'importing', 'imported') ORDER BY id
                """;
            var rows = new List<ActiveFetch>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ActiveFetch(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return rows;
        }
    }

    /// <summary>
    /// The rows retention looks at: state <c>ready</c> and not yet kept (<c>kept_utc</c> empty), oldest ready first.
    /// A row held back by a request has <c>kept_reason = 'requested'</c> but no <c>kept_utc</c>, so it shows here with its reason.
    /// </summary>
    public IReadOnlyList<TrackedFile> TrackedForRetention()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {TrackedColumns} FROM fetched_files WHERE state = 'ready' AND kept_utc IS NULL ORDER BY ready_utc, id";
            return ReadTracked(cmd);
        }
    }

    /// <summary>
    /// Every row in state <c>ready</c>, kept or not, oldest ready first: what the retention pass decides about. Unlike
    /// <see cref="TrackedForRetention"/> it includes rows already kept (dry-run keeps, promotions in progress, duplicates).
    /// </summary>
    public IReadOnlyList<TrackedFile> ReadyForRetention()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {TrackedColumns} FROM fetched_files WHERE state = 'ready' ORDER BY ready_utc, id";
            return ReadTracked(cmd);
        }
    }

    /// <summary>
    /// The paths of every row that is not failed (current path and, after a promotion, the old radio path): a file under the
    /// radio folder that none of these names is untracked.
    /// </summary>
    public IReadOnlyList<string> NonFailedPaths()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT path FROM fetched_files WHERE state <> 'failed' AND path IS NOT NULL
                UNION
                SELECT radio_path FROM fetched_files WHERE state <> 'failed' AND radio_path IS NOT NULL
                """;
            var paths = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                paths.Add(r.GetString(0));
            }

            return paths;
        }
    }

    /// <summary>One <c>fetched_files</c> row as retention sees it, or null when the id does not exist.</summary>
    public TrackedFile? GetTracked(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {TrackedColumns} FROM fetched_files WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return ReadTracked(cmd).FirstOrDefault();
        }
    }

    /// <summary>Marks a ready row as kept for good (<c>kept_reason</c> and <c>kept_utc</c>). Returns false when the row is not in state ready.</summary>
    public bool MarkKept(long id, string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE fetched_files SET kept_reason = $reason, kept_utc = $now WHERE id = $id AND state = 'ready'";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Marks a ready row deleted with the reason (age | uploads). Returns false when the row is not in state ready.</summary>
    public bool MarkDeleted(long id, string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE fetched_files SET state = 'deleted', delete_reason = $reason, deleted_utc = $now WHERE id = $id AND state = 'ready'";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Marks a ready row promoted: the old radio path moves to <c>radio_path</c>, and the library path, Jellyfin path and new item id
    /// replace the current ones. Returns false when the row is not in state ready.
    /// </summary>
    public bool MarkPromoted(long id, string targetPath, string? targetJellyfinPath, string? newItemId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE fetched_files SET
                  state = 'promoted', radio_path = path, path = $path,
                  jellyfin_path = COALESCE($jpath, jellyfin_path), jellyfin_item_id = COALESCE($jitem, jellyfin_item_id),
                  promoted_utc = $now
                WHERE id = $id AND state = 'ready'
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$path", targetPath);
            cmd.Parameters.AddWithValue("$jpath", Db(targetJellyfinPath));
            cmd.Parameters.AddWithValue("$jitem", Db(newItemId));
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Sets the <c>uploads</c> and <c>play_throughs</c> counters of one row (the counts the retention rules read). Returns false when the id does not exist.</summary>
    public bool SetCounters(long id, int uploads, int playThroughs)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE fetched_files SET uploads = $uploads, play_throughs = $plays WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$uploads", uploads);
            cmd.Parameters.AddWithValue("$plays", playThroughs);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }

    private const string TrackedColumns = """
        id, track_key, artist, title, state, path, jellyfin_path, jellyfin_item_id, requested, kept_reason, kept_utc,
        promoted_utc, radio_path, delete_reason, uploads, play_throughs, ready_utc, user_id, peer, remote_path
        """;

    private static List<TrackedFile> ReadTracked(SqliteCommand cmd)
    {
        var rows = new List<TrackedFile>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new TrackedFile(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                Str(r, 5), Str(r, 6), Str(r, 7), r.GetInt32(8) != 0, Str(r, 9), Time(r, 10),
                Time(r, 11), Str(r, 12), Str(r, 13), r.GetInt32(14), r.GetInt32(15), Time(r, 16),
                r.GetString(17), Str(r, 18), Str(r, 19)));
        }

        return rows;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? Time(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static object Db(int? value) => value is { } v ? v : DBNull.Value;

    private static object Db(long? value) => value is { } v ? v : DBNull.Value;

    private static object Db(double? value) => value is { } v ? v : DBNull.Value;

    private static object Db(DateTimeOffset? value) => value is { } v ? EncoreStore.FormatUtc(v) : DBNull.Value;
}
