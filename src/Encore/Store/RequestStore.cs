using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Encore.Store;

/// <summary>States of a <c>requests</c> row (a CHECK constraint holds the same list).</summary>
public static class RequestState
{
    public const string Queued = "queued";

    /// <summary>An album sent to Lidarr whose release date is in the future or not known yet (polled every 6 h).</summary>
    public const string Upcoming = "upcoming";

    public const string Searching = "searching";
    public const string Downloading = "downloading";
    public const string Importing = "importing";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>A new request, written in state <c>queued</c>. <see cref="Kind"/> is song | album.</summary>
public sealed record NewRequest(
    string Id,
    string Kind,
    string UserId,
    string UserName,
    string Artist,
    DateTimeOffset CreatedUtc,
    string? Title = null,
    string? Album = null,
    int? Year = null,
    string? CoverUrl = null,
    string? TrackKey = null,
    long? DeezerTrackId = null,
    int? DurationSeconds = null,
    string? ForeignAlbumId = null,
    string? ForeignArtistId = null);

/// <summary>Fields to change on a request. A null field is left as it is; <see cref="ClearNextTry"/> empties <c>next_try_utc</c>, <see cref="ClearFailReason"/> empties <c>fail_reason</c>.</summary>
public sealed record RequestUpdate(
    string? Detail = null,
    string? FailReason = null,
    long? LidarrArtistId = null,
    long? LidarrAlbumId = null,
    int? TracksDone = null,
    int? TracksTotal = null,
    long? FetchedFileId = null,
    string? JellyfinItemId = null,
    int? Attempts = null,
    DateTimeOffset? NextTryUtc = null,
    bool ClearNextTry = false,
    string? CancelledBy = null,
    DateTimeOffset? ReadyUtc = null,
    DateTimeOffset? NotifiedUtc = null,
    bool ClearFailReason = false,
    int? Year = null,
    int? Retries = null,
    bool? AlbumWasMonitored = null,
    bool? ArtistWasMonitored = null);

/// <summary>One <c>requests</c> row.</summary>
public sealed record RequestRow(
    string Id,
    string Kind,
    string UserId,
    string UserName,
    string State,
    string? Detail,
    string? FailReason,
    string Artist,
    string? Title,
    string? Album,
    int? Year,
    string? CoverUrl,
    string? TrackKey,
    long? DeezerTrackId,
    int? DurationSeconds,
    string? ForeignAlbumId,
    string? ForeignArtistId,
    long? LidarrArtistId,
    long? LidarrAlbumId,
    int? TracksDone,
    int? TracksTotal,
    long? FetchedFileId,
    string? JellyfinItemId,
    int Attempts,
    DateTimeOffset? NextTryUtc,
    string? CancelledBy,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? ReadyUtc,
    DateTimeOffset? NotifiedUtc,
    int Retries = 0,
    bool? AlbumWasMonitored = null,
    bool? ArtistWasMonitored = null);

/// <summary>
/// The request feature's part of the database (v0.3 design section 8): its own connection to the same file as
/// <see cref="EncoreStore"/>, same pragmas, one lock. Autocommit statements only: no transaction is held across calls,
/// so it never holds a lock that EncoreStore's BEGIN IMMEDIATE blocks wait on. It never migrates.
/// </summary>
public sealed partial class RequestStore : IDisposable, IPageTokenStore
{
    private const string Columns = """
        id, kind, user_id, user_name, state, detail, fail_reason, artist, title, album, year, cover_url, track_key,
        deezer_track_id, duration_s, foreign_album_id, foreign_artist_id, lidarr_artist_id, lidarr_album_id,
        tracks_done, tracks_total, fetched_file_id, jellyfin_item_id, attempts, next_try_utc, cancelled_by,
        created_utc, updated_utc, ready_utc, notified_utc, retries, album_was_monitored, artist_was_monitored
        """;

    private const string ActiveClause = "state IN ('queued','upcoming','searching','downloading','importing')";

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    private RequestStore(SqliteConnection db) => _db = db;

    /// <summary>Opens the existing database. Throws <see cref="StoreException"/> unless user_version equals <see cref="EncoreStore.SchemaVersion"/>.</summary>
    public static RequestStore Open(string path)
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

            return new RequestStore(db);
        }
        catch (StoreException)
        {
            db.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            db.Dispose();
            throw new StoreException($"could not open the request store at {path}: {ex.GetType().Name}", ex);
        }
    }

    /// <summary>Inserts a request in state <c>queued</c>.</summary>
    public void Insert(NewRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO requests (id, kind, user_id, user_name, state, artist, title, album, year, cover_url, track_key,
                  deezer_track_id, duration_s, foreign_album_id, foreign_artist_id, created_utc, updated_utc)
                VALUES ($id, $kind, $user, $name, 'queued', $artist, $title, $album, $year, $cover, $key,
                  $deezer, $duration, $fAlbum, $fArtist, $at, $at)
                """;
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$kind", r.Kind);
            cmd.Parameters.AddWithValue("$user", r.UserId);
            cmd.Parameters.AddWithValue("$name", r.UserName);
            cmd.Parameters.AddWithValue("$artist", r.Artist);
            cmd.Parameters.AddWithValue("$title", Db(r.Title));
            cmd.Parameters.AddWithValue("$album", Db(r.Album));
            cmd.Parameters.AddWithValue("$year", Db(r.Year));
            cmd.Parameters.AddWithValue("$cover", Db(r.CoverUrl));
            cmd.Parameters.AddWithValue("$key", Db(r.TrackKey));
            cmd.Parameters.AddWithValue("$deezer", Db(r.DeezerTrackId));
            cmd.Parameters.AddWithValue("$duration", Db(r.DurationSeconds));
            cmd.Parameters.AddWithValue("$fAlbum", Db(r.ForeignAlbumId));
            cmd.Parameters.AddWithValue("$fArtist", Db(r.ForeignArtistId));
            cmd.Parameters.AddWithValue("$at", EncoreStore.FormatUtc(r.CreatedUtc));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the state, <c>updated_utc</c> and any non-null field of one request. Returns false when the id does not exist.</summary>
    public bool Update(string id, string state, DateTimeOffset now, RequestUpdate? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        fields ??= new RequestUpdate();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE requests SET
                  state = $state, updated_utc = $now,
                  detail = COALESCE($detail, detail), fail_reason = CASE WHEN $clearFail = 1 THEN NULL ELSE COALESCE($fail, fail_reason) END,
                  lidarr_artist_id = COALESCE($lArtist, lidarr_artist_id), lidarr_album_id = COALESCE($lAlbum, lidarr_album_id),
                  tracks_done = COALESCE($done, tracks_done), tracks_total = COALESCE($total, tracks_total),
                  fetched_file_id = COALESCE($file, fetched_file_id), jellyfin_item_id = COALESCE($jitem, jellyfin_item_id),
                  attempts = COALESCE($attempts, attempts),
                  next_try_utc = CASE WHEN $clearNext = 1 THEN NULL ELSE COALESCE($next, next_try_utc) END,
                  cancelled_by = COALESCE($cancelledBy, cancelled_by),
                  ready_utc = COALESCE($ready, ready_utc), notified_utc = COALESCE($notified, notified_utc),
                  year = COALESCE($year, year), retries = COALESCE($retries, retries),
                  album_was_monitored = COALESCE($albumWas, album_was_monitored),
                  artist_was_monitored = COALESCE($artistWas, artist_was_monitored)
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$now", EncoreStore.FormatUtc(now));
            cmd.Parameters.AddWithValue("$detail", Db(fields.Detail));
            cmd.Parameters.AddWithValue("$fail", Db(fields.FailReason));
            cmd.Parameters.AddWithValue("$clearFail", fields.ClearFailReason ? 1 : 0);
            cmd.Parameters.AddWithValue("$lArtist", Db(fields.LidarrArtistId));
            cmd.Parameters.AddWithValue("$lAlbum", Db(fields.LidarrAlbumId));
            cmd.Parameters.AddWithValue("$done", Db(fields.TracksDone));
            cmd.Parameters.AddWithValue("$total", Db(fields.TracksTotal));
            cmd.Parameters.AddWithValue("$file", Db(fields.FetchedFileId));
            cmd.Parameters.AddWithValue("$jitem", Db(fields.JellyfinItemId));
            cmd.Parameters.AddWithValue("$attempts", Db(fields.Attempts));
            cmd.Parameters.AddWithValue("$clearNext", fields.ClearNextTry ? 1 : 0);
            cmd.Parameters.AddWithValue("$next", Db(fields.NextTryUtc));
            cmd.Parameters.AddWithValue("$cancelledBy", Db(fields.CancelledBy));
            cmd.Parameters.AddWithValue("$ready", Db(fields.ReadyUtc));
            cmd.Parameters.AddWithValue("$notified", Db(fields.NotifiedUtc));
            cmd.Parameters.AddWithValue("$year", Db(fields.Year));
            cmd.Parameters.AddWithValue("$retries", Db(fields.Retries));
            cmd.Parameters.AddWithValue("$albumWas", Db(fields.AlbumWasMonitored));
            cmd.Parameters.AddWithValue("$artistWas", Db(fields.ArtistWasMonitored));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>One request, or null when the id does not exist.</summary>
    public RequestRow? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Query("WHERE id = $id", c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();
    }

    /// <summary>A user's requests, newest first. <paramref name="activeOnly"/> keeps the in-progress ones.</summary>
    public IReadOnlyList<RequestRow> ListByUser(string userId, bool activeOnly = false, int limit = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var filter = activeOnly ? $"WHERE user_id = $user AND {ActiveClause}" : "WHERE user_id = $user";
        return Query($"{filter} ORDER BY created_utc DESC, id LIMIT $limit", c =>
        {
            c.Parameters.AddWithValue("$user", userId);
            c.Parameters.AddWithValue("$limit", limit);
        });
    }

    /// <summary>Every request, newest first.</summary>
    public IReadOnlyList<RequestRow> ListAll(bool activeOnly = false, int limit = 500)
    {
        var filter = activeOnly ? $"WHERE {ActiveClause}" : string.Empty;
        return Query($"{filter} ORDER BY created_utc DESC, id LIMIT $limit", c => c.Parameters.AddWithValue("$limit", limit));
    }

    /// <summary>The in-progress requests (queued, searching, downloading, importing), oldest first.</summary>
    public IReadOnlyList<RequestRow> Active() => Query($"WHERE {ActiveClause} ORDER BY created_utc, id", null);

    /// <summary>How many requests of <paramref name="kind"/> the user created at or after <paramref name="since"/> (any state; the daily cap window).</summary>
    public int CountCreatedSince(string userId, string kind, DateTimeOffset since)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM requests WHERE user_id = $user AND kind = $kind AND created_utc >= $since";
            cmd.Parameters.AddWithValue("$user", userId);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$since", EncoreStore.FormatUtc(since));
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>How many requests are in each in-progress state (a state with none is absent).</summary>
    public IReadOnlyDictionary<string, int> CountActiveByState()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT state, COUNT(*) FROM requests WHERE {ActiveClause} GROUP BY state";
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                counts[r.GetString(0)] = r.GetInt32(1);
            }

            return counts;
        }
    }

    /// <summary>Requests created, made ready and failed at or after <paramref name="since"/> (for /health).</summary>
    public (int Created, int Ready, int Failed) CountsSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM requests WHERE created_utc >= $since),
                  (SELECT COUNT(*) FROM requests WHERE state = 'ready' AND ready_utc >= $since),
                  (SELECT COUNT(*) FROM requests WHERE state = 'failed' AND updated_utc >= $since)
                """;
            cmd.Parameters.AddWithValue("$since", EncoreStore.FormatUtc(since));
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
    }

    /// <summary>The in-progress requests for one track key, oldest first (duplicate detection).</summary>
    public IReadOnlyList<RequestRow> ActiveForKey(string trackKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackKey);
        return Query($"WHERE track_key = $key AND {ActiveClause} ORDER BY created_utc, id", c => c.Parameters.AddWithValue("$key", trackKey));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }

    private List<RequestRow> Query(string tail, Action<SqliteCommand>? bind)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM requests {tail}";
            bind?.Invoke(cmd);
            var rows = new List<RequestRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new RequestRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    Str(r, 5), Str(r, 6), r.GetString(7), Str(r, 8), Str(r, 9), Int(r, 10), Str(r, 11), Str(r, 12),
                    Long(r, 13), Int(r, 14), Str(r, 15), Str(r, 16), Long(r, 17), Long(r, 18), Int(r, 19), Int(r, 20),
                    Long(r, 21), Str(r, 22), r.GetInt32(23), Time(r, 24), Str(r, 25),
                    ParseUtc(r.GetString(26)), ParseUtc(r.GetString(27)), Time(r, 28), Time(r, 29),
                    r.GetInt32(30), Flag(r, 31), Flag(r, 32)));
            }

            return rows;
        }
    }

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static bool? Flag(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i) != 0;

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static DateTimeOffset? Time(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseUtc(r.GetString(i));

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static object Db(int? value) => value is { } v ? v : DBNull.Value;

    private static object Db(bool? value) => value is { } v ? (v ? 1 : 0) : DBNull.Value;

    private static object Db(long? value) => value is { } v ? v : DBNull.Value;

    private static object Db(DateTimeOffset? value) => value is { } v ? EncoreStore.FormatUtc(v) : DBNull.Value;
}
