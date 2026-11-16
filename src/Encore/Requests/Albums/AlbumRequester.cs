using System.Globalization;
using System.Text.Json.Nodes;
using Encore.Config;
using Encore.Lidarr;
using Encore.Requests.Api;
using Encore.Requests.Auth;
using Encore.Requests.Songs;
using Encore.Store;

namespace Encore.Requests.Albums;

/// <summary>The settings album requests need: the per-person daily cap, the Lidarr root folder and the path map for the nudge.</summary>
public sealed record AlbumRequestSettings(int AlbumsPerDay, string? RootFolder, PathMapping PathMap)
{
    public static AlbumRequestSettings From(EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AlbumRequestSettings(options.RequestAlbumsPerDay, options.LidarrRootFolder, options.LidarrPathMap);
    }
}

/// <summary>Fail reasons of album requests (the request's <c>fail_reason</c>).</summary>
public static class AlbumReasons
{
    /// <summary>Still no file after 7 days; the album is unmonitored.</summary>
    public const string NotFound = "not_found";

    /// <summary>Partly downloaded and unchanged for 3 days; the album is left as it is.</summary>
    public const string Incomplete = "incomplete";

    /// <summary>Lidarr refused the add, or the album or its artist is not monitored afterwards.</summary>
    public const string LidarrRejected = "lidarr_rejected";

    /// <summary>The album disappeared from Lidarr while the request was active.</summary>
    public const string LidarrRemoved = "lidarr_removed";

    /// <summary>The album id is no longer in Lidarr's catalog (seen when sending again after a restart or a retry).</summary>
    public const string LidarrUnknown = "lidarr_unknown";

    /// <summary>No usable root folder for a new artist (several and none configured, or the configured one is missing).</summary>
    public const string RootFolder = "lidarr_root_folder";
}

/// <summary>
/// Album requests through Lidarr (v0.3 design 7). Create (daily cap, id lookup, owned -> ready, join an active request
/// for the same album), send to Lidarr (monitor an existing album, or one POST album that also adds a missing
/// artist with monitorNewItems "none"; switch the artist on; check both flags), poll progress every 2 min from the album's
/// statistics, find the album in Jellyfin (one Media/Updated nudge with the artist folder), time out after 7 days with no
/// file or 3 days stalled, cancel and retry. Lidarr writes are only those of invariant 7: never POST artist, never another
/// album, never monitorNewItems, profiles or paths. One async lock serialises every state change; Lidarr being down keeps
/// the state and the next poll tries again. An album whose release date is in the future or not known yet is sent the same
/// way and waits as <c>upcoming</c> (release date re-read every 6 h); it moves to <c>searching</c> on its release day, and only
/// then does the 7-day <c>not_found</c> timer start.
/// </summary>
public sealed class AlbumRequester
{
    public static class Details
    {
        public const string Sending = "Sending to Lidarr";
        public const string Searching = "Waiting for Soulseek (Soularr checks every few minutes)";
        public const string Importing = "In Lidarr, waiting for Jellyfin";
        public const string LidarrDown = "Waiting: Lidarr is not reachable";
        public const string LidarrKey = "Waiting: Lidarr rejected Encore's key";

        /// <summary>"Upcoming: releases YYYY-MM-DD", or "Upcoming: release date not known yet" without a date.</summary>
        public static string Upcoming(DateTimeOffset? release) => release is { } date
            ? "Upcoming: releases " + date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "Upcoming: release date not known yet";

        public static string Downloading(int files, int total) =>
            string.Create(CultureInfo.InvariantCulture, $"{files} of {total} tracks");

        public static string Failure(string reason) => reason switch
        {
            AlbumReasons.NotFound => "Not found on Soulseek within 7 days",
            AlbumReasons.Incomplete => "Only part of the album arrived",
            AlbumReasons.LidarrRejected => "Lidarr did not accept the request",
            AlbumReasons.LidarrRemoved => "The album was removed from Lidarr",
            AlbumReasons.LidarrUnknown => "Lidarr no longer knows this album",
            AlbumReasons.RootFolder => "Lidarr's root folder is not set up for Encore (ENCORE_LIDARR_ROOT_FOLDER)",
            _ => "Failed (" + reason + ")",
        };
    }

    private readonly RequestStore _store;
    private readonly ILidarrClient _lidarr;
    private readonly IAlbumLibrary _library;
    private readonly AlbumRequestSettings _settings;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // When each active request is looked at next (empty after a start, so the first pass polls everything).
    private readonly Dictionary<string, DateTimeOffset> _due = new(StringComparer.Ordinal);

    // Lidarr album ids that got their one Media/Updated nudge in this process.
    private readonly HashSet<long> _nudged = [];

    public AlbumRequester(
        RequestStore store,
        ILidarrClient lidarr,
        IAlbumLibrary library,
        AlbumRequestSettings settings,
        ILogger<AlbumRequester> logger,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lidarr = lidarr ?? throw new ArgumentNullException(nameof(lidarr));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Album requests that Lidarr has complete but Jellyfin has not listed for 2 h or more (a /health warning).</summary>
    public int WaitingForJellyfin { get; private set; }

    /// <summary>
    /// POST /requests {kind:"album", foreignAlbumId} (design 7.1). 201 new; 200 with the caller's own active request for it;
    /// 400 for an id that is not a GUID; 429 at the daily cap; 404 when Lidarr's catalog lacks it; 503 <c>albums_unavailable</c> when Lidarr cannot be reached or rejects the key.
    /// </summary>
    public async Task<(RequestOutcome Outcome, IReadOnlyList<RequestRow> Notify)> CreateAsync(RequestUser user, string foreignAlbumId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!Guid.TryParse(foreignAlbumId?.Trim(), out var parsed))
        {
            return (RequestOutcome.Fail(StatusCodes.Status400BadRequest, AuthCodes.BadRequest), []);
        }

        var fid = parsed.ToString("D");
        var userKey = RequestUserKey.Of(user.Id);
        if (MineActive(userKey, fid) is { } same)
        {
            return (new RequestOutcome(StatusCodes.Status200OK, same), []);
        }

        if (OverCap(user, userKey))
        {
            return (RequestOutcome.Fail(StatusCodes.Status429TooManyRequests, RequestCodes.DailyLimit), []);
        }

        LidarrAlbum? album;
        try
        {
            album = await LookupAsync(fid, ct).ConfigureAwait(false);
        }
        catch (LidarrException ex) when (!ct.IsCancellationRequested)
        {
            LogLidarr(ex);
            return (RequestOutcome.Fail(StatusCodes.Status503ServiceUnavailable, RequestCodes.AlbumsUnavailable), []);
        }

        var artist = album?.Artist?.ArtistName?.Trim();
        var title = album?.Title?.Trim();
        if (album is null || string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title))
        {
            return (RequestOutcome.Fail(StatusCodes.Status404NotFound, RequestCodes.NotFound), []);
        }

        var (owned, itemId) = await OwnedAsync(user.Id, album, artist, title, ct).ConfigureAwait(false);

        var notify = new List<RequestRow>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (MineActive(userKey, fid) is { } mine)
            {
                return (new RequestOutcome(StatusCodes.Status200OK, mine), []);
            }

            // Checked again under the lock, so two quick posts cannot both pass the last free slot.
            if (OverCap(user, userKey))
            {
                return (RequestOutcome.Fail(StatusCodes.Status429TooManyRequests, RequestCodes.DailyLimit), []);
            }

            var now = _time.GetUtcNow();
            var id = Guid.NewGuid().ToString("N");
            _store.Insert(new NewRequest(
                id, "album", userKey, user.Name, artist, now,
                Title: title,
                Year: album.ReleaseDate?.Year,
                CoverUrl: AlbumSearch.CoverOf(album),
                ForeignAlbumId: fid,
                ForeignArtistId: album.Artist?.ForeignArtistId));
            var row = _store.Get(id)!;

            if (owned)
            {
                _log.Created(id, "album", userKey, artist, title, RequestState.Ready);
                row = MarkReadyLocked(row, itemId, now, notify);
            }
            else if (ActiveSibling(row) is { } sibling)
            {
                // One Lidarr album serves both requests: no second write.
                row = JoinLocked(row, sibling, now);
                _log.Created(id, "album", userKey, artist, title, row.State);
                _log.Joined(id, "album:" + fid, "request");
            }
            else
            {
                _log.Created(id, "album", userKey, artist, title, RequestState.Queued);

                // Not cancelled with the HTTP call: a half-sent request would leave Lidarr in between.
                await SendLockedAsync(row, album, notify, CancellationToken.None).ConfigureAwait(false);
                row = _store.Get(id) ?? row;
            }

            return (new RequestOutcome(StatusCodes.Status201Created, row), notify);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>DELETE for an album: owner while queued, upcoming or searching, admin in any active state. The album is unmonitored unless another active request holds it; files stay.</summary>
    public async Task<RequestOutcome> CancelAsync(RequestUser user, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = string.IsNullOrWhiteSpace(id) ? null : _store.Get(id);
            if (row is null || row.Kind != "album" || !MayAccess(user, row))
            {
                return RequestOutcome.Fail(StatusCodes.Status404NotFound, RequestCodes.NotFound);
            }

            var early = row.State is RequestState.Queued or RequestState.Upcoming or RequestState.Searching;
            if (!RequestService.IsActive(row.State) || (!user.IsAdmin && !early))
            {
                return RequestOutcome.Fail(StatusCodes.Status409Conflict, RequestCodes.Conflict);
            }

            var by = RequestUserKey.Of(user.Id);
            var now = _time.GetUtcNow();
            _store.Update(row.Id, RequestState.Cancelled, now, new RequestUpdate(Detail: "Cancelled", CancelledBy: by, ClearNextTry: true));
            _due.Remove(row.Id);
            _log.StateChanged(row.Id, row.State, RequestState.Cancelled, "Cancelled");
            _log.Cancelled(row.Id, by);
            if (row.LidarrAlbumId is { } albumId)
            {
                await UnmonitorIfFreeAsync(row.Id, albumId, "cancelled").ConfigureAwait(false);
            }

            return new RequestOutcome(StatusCodes.Status200OK, _store.Get(row.Id));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>POST /requests/{id}/retry for a failed album: back to queued, attempts reset, sent to Lidarr again (counts toward the cap for non-admins).</summary>
    public async Task<(RequestOutcome Outcome, IReadOnlyList<RequestRow> Notify)> RetryAsync(RequestUser user, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var notify = new List<RequestRow>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = string.IsNullOrWhiteSpace(id) ? null : _store.Get(id);
            if (row is null || row.Kind != "album" || !MayAccess(user, row))
            {
                return (RequestOutcome.Fail(StatusCodes.Status404NotFound, RequestCodes.NotFound), []);
            }

            if (row.State != RequestState.Failed)
            {
                return (RequestOutcome.Fail(StatusCodes.Status409Conflict, RequestCodes.Conflict), []);
            }

            if (OverCap(user, RequestUserKey.Of(user.Id)))
            {
                return (RequestOutcome.Fail(StatusCodes.Status429TooManyRequests, RequestCodes.DailyLimit), []);
            }

            var now = _time.GetUtcNow();
            _store.Update(row.Id, RequestState.Queued, now, new RequestUpdate(
                Detail: Details.Sending, Attempts: 0, ClearNextTry: true, ClearFailReason: true));
            _log.StateChanged(row.Id, row.State, RequestState.Queued, "retry");
            row = _store.Get(row.Id)!;
            if (ActiveSibling(row) is { } sibling)
            {
                row = JoinLocked(row, sibling, now);
            }
            else
            {
                await SendLockedAsync(row, null, notify, CancellationToken.None).ConfigureAwait(false);
                row = _store.Get(row.Id) ?? row;
            }

            return (new RequestOutcome(StatusCodes.Status200OK, row), notify);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One lifecycle pass (the worker calls it every 2 s): every active album request whose poll is due (2 min, 6 h while
    /// upcoming) is sent to Lidarr (queued) or polled (one GET album per Lidarr album per pass). Returns the requests to notify. One bad row
    /// never stops the pass.
    /// </summary>
    public async Task<IReadOnlyList<RequestRow>> TickAsync(CancellationToken ct)
    {
        var notify = new List<RequestRow>();
        IReadOnlyList<RequestRow> active;
        try
        {
            active = _store.Active().Where(r => r.Kind == "album").ToList();
        }
        catch (StoreException ex)
        {
            _log.WorkerError("album_list", ex.Message);
            return notify;
        }

        var albums = new Dictionary<long, LidarrAlbum?>();
        foreach (var listed in active)
        {
            var now = _time.GetUtcNow();
            if (_due.TryGetValue(listed.Id, out var due) && due > now)
            {
                continue;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var row = _store.Get(listed.Id);
                if (row is null || row.Kind != "album" || !RequestService.IsActive(row.State))
                {
                    continue;
                }

                _due[row.Id] = now + (row.State == RequestState.Upcoming ? RequestLimits.AlbumUpcomingPoll : RequestLimits.AlbumPoll);
                if (row.State == RequestState.Queued || row.LidarrAlbumId is null)
                {
                    if (ActiveSibling(row) is { } sibling)
                    {
                        JoinLocked(row, sibling, now);
                    }
                    else
                    {
                        await SendLockedAsync(row, null, notify, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await PollLockedAsync(row, albums, notify, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.WorkerError("album:" + listed.Id, ex.GetType().Name);
            }
            finally
            {
                _gate.Release();
            }
        }

        try
        {
            var now = _time.GetUtcNow();
            var still = _store.Active().Where(r => r.Kind == "album").ToList();
            WaitingForJellyfin = still.Count(r => r.State == RequestState.Importing && now - r.UpdatedUtc >= RequestLimits.AlbumJellyfinWait);
            var ids = still.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _due.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _due.Remove(gone);
            }
        }
        catch (StoreException ex)
        {
            _log.WorkerError("album_list", ex.Message);
        }

        return notify;
    }

    // ---- send (design 7.2) -------------------------------------------------------------------------------------

    /// <summary>
    /// Monitors the album and its artist in Lidarr. Success: <c>searching</c> with the Lidarr ids, or <c>upcoming</c> when the
    /// album's release date is in the future or not known yet. Lidarr unreachable or the
    /// key rejected: stays queued (the next poll tries again). Refused or not monitored afterwards: failed.
    /// </summary>
    private async Task SendLockedAsync(RequestRow row, LidarrAlbum? lookup, List<RequestRow> notify, CancellationToken ct)
    {
        var fid = row.ForeignAlbumId ?? string.Empty;
        try
        {
            lookup ??= await LookupAsync(fid, ct).ConfigureAwait(false);
            if (lookup is null)
            {
                await FailLockedAsync(row, AlbumReasons.LidarrUnknown, notify, unmonitor: false).ConfigureAwait(false);
                return;
            }

            int albumId;
            string via;
            if (lookup.Id > 0)
            {
                await _lidarr.SetAlbumsMonitoredAsync([lookup.Id], true, ct).ConfigureAwait(false);
                albumId = lookup.Id;
                via = "existing";
            }
            else
            {
                var added = await AddAlbumAsync(row, lookup, notify, ct).ConfigureAwait(false);
                if (added is null)
                {
                    return;
                }

                (albumId, via) = added.Value;
            }

            // Artist on (design 7.2 step 4): only `monitored` changes; other albums stay as they are.
            var album = await _lidarr.GetAlbumAsync(albumId, ct).ConfigureAwait(false);
            if (album is { Artist.Monitored: false, ArtistId: > 0 })
            {
                var now0 = _time.GetUtcNow();
                var others = await _lidarr.GetAlbumsByArtistAsync(album.ArtistId, ct).ConfigureAwait(false);
                var wanted = others.Count(o => o.Id != albumId && o.Monitored && AlbumSearch.IsReleased(o, now0)
                    && o.Statistics is { } st && st.TrackFileCount < st.TrackCount);
                if (wanted > 0)
                {
                    _log.ArtistOtherWanted(row.Id, album.ArtistId, wanted);
                }

                await _lidarr.SetArtistMonitoredAsync(album.ArtistId, true, ct).ConfigureAwait(false);
                album = await _lidarr.GetAlbumAsync(albumId, ct).ConfigureAwait(false);
            }

            var now = _time.GetUtcNow();
            if (album is null || !album.Monitored || album.Artist?.Monitored != true)
            {
                // The after-check (design 7.2 step 5): Soularr only sees an album whose album AND artist are monitored.
                _log.LidarrRequestFailed("after_check", null,
                    $"album monitored {album?.Monitored.ToString() ?? "missing"}, artist monitored {album?.Artist?.Monitored.ToString() ?? "missing"}");
                _store.Update(row.Id, row.State, now, new RequestUpdate(LidarrAlbumId: albumId, LidarrArtistId: album is { ArtistId: > 0 } ? album.ArtistId : (long?)null));
                await FailLockedAsync(_store.Get(row.Id) ?? row, AlbumReasons.LidarrRejected, notify, unmonitor: true).ConfigureAwait(false);
                return;
            }

            var upcoming = IsUpcoming(album, now);
            var (state, detail) = upcoming
                ? (RequestState.Upcoming, Details.Upcoming(album.ReleaseDate))
                : (RequestState.Searching, Details.Searching);
            _store.Update(row.Id, state, now, new RequestUpdate(
                Detail: detail, LidarrAlbumId: albumId, LidarrArtistId: album.ArtistId, ClearNextTry: true, Year: album.ReleaseDate?.Year));
            _log.AlbumMonitored(row.Id, albumId, true, via);
            if (row.State != state)
            {
                _log.StateChanged(row.Id, row.State, state, detail);
            }

            _due[row.Id] = now + (upcoming ? RequestLimits.AlbumUpcomingPoll : RequestLimits.AlbumPoll);
        }
        catch (LidarrValidationException ex)
        {
            LogLidarr(ex);
            await FailLockedAsync(_store.Get(row.Id) ?? row, AlbumReasons.LidarrRejected, notify, unmonitor: true).ConfigureAwait(false);
        }
        catch (LidarrException ex) when (!ct.IsCancellationRequested)
        {
            LogLidarr(ex);
            var detail = ex is LidarrAuthException ? Details.LidarrKey : Details.LidarrDown;
            var current = _store.Get(row.Id) ?? row;
            if (current.State != RequestState.Queued || current.Detail != detail)
            {
                _store.Update(row.Id, RequestState.Queued, _time.GetUtcNow(), new RequestUpdate(Detail: detail));
                if (current.State != RequestState.Queued)
                {
                    _log.StateChanged(row.Id, current.State, RequestState.Queued, detail);
                }
            }
        }
    }

    /// <summary>The album is not in Lidarr: one POST album with the artist object (as GET returned it, or a new monitored one). Null when the request failed.</summary>
    private async Task<(int AlbumId, string Via)?> AddAlbumAsync(RequestRow row, LidarrAlbum lookup, List<RequestRow> notify, CancellationToken ct)
    {
        var foreignArtistId = lookup.Artist?.ForeignArtistId;
        var artistName = lookup.Artist?.ArtistName ?? row.Artist;
        if (string.IsNullOrWhiteSpace(foreignArtistId))
        {
            _log.LidarrRequestFailed("add_album", null, "the lookup has no artist id");
            await FailLockedAsync(row, AlbumReasons.LidarrRejected, notify, unmonitor: false).ConfigureAwait(false);
            return null;
        }

        var existing = await _lidarr.FindArtistByForeignIdAsync(foreignArtistId, ct).ConfigureAwait(false);
        JsonObject artistBody;
        if (existing is not null)
        {
            artistBody = (JsonObject)existing.Raw.DeepClone();
        }
        else
        {
            var root = await RootFolderAsync(ct).ConfigureAwait(false);
            if (root is null)
            {
                _log.LidarrRequestFailed("root_folder", null, _settings.RootFolder is null
                    ? "Lidarr has no single root folder; set ENCORE_LIDARR_ROOT_FOLDER"
                    : "the configured ENCORE_LIDARR_ROOT_FOLDER is not a Lidarr root folder");
                await FailLockedAsync(row, AlbumReasons.RootFolder, notify, unmonitor: false).ConfigureAwait(false);
                return null;
            }

            artistBody = LidarrArtistBodies.ForNewArtist(
                foreignArtistId, artistName, root.DefaultQualityProfileId, root.DefaultMetadataProfileId, root.Path);
        }

        try
        {
            var added = await _lidarr.AddAlbumAsync(new LidarrAddAlbum(lookup.ForeignAlbumId, lookup.Title ?? row.Title ?? string.Empty, artistBody), ct).ConfigureAwait(false);
            if (existing is null)
            {
                _log.ArtistAdded(row.Id, added.ArtistId, artistName);
            }

            return (added.Id, "add_album");
        }
        catch (LidarrValidationException ex) when (ex.IsAlreadyAdded)
        {
            // A race with another request or a hand edit: the album is in Lidarr now, so monitor it (design 7.2 step 3).
            var found = (await _lidarr.GetAlbumsByForeignIdAsync(lookup.ForeignAlbumId, false, ct).ConfigureAwait(false)).FirstOrDefault();
            if (found is null || found.Id <= 0)
            {
                throw;
            }

            await _lidarr.SetAlbumsMonitoredAsync([found.Id], true, ct).ConfigureAwait(false);
            return (found.Id, "existing");
        }
    }

    private async Task<LidarrRootFolder?> RootFolderAsync(CancellationToken ct)
    {
        var folders = await _lidarr.GetRootFoldersAsync(ct).ConfigureAwait(false);
        if (_settings.RootFolder is { } wanted)
        {
            var clean = wanted.TrimEnd('/');
            return folders.FirstOrDefault(f => string.Equals(f.Path.TrimEnd('/'), clean, StringComparison.Ordinal));
        }

        return folders.Count == 1 ? folders[0] : null;
    }

    // ---- progress (design 7.3) ---------------------------------------------------------------------------------

    private async Task PollLockedAsync(RequestRow row, Dictionary<long, LidarrAlbum?> albums, List<RequestRow> notify, CancellationToken ct)
    {
        var albumId = row.LidarrAlbumId!.Value;
        if (!albums.TryGetValue(albumId, out var album))
        {
            try
            {
                album = await _lidarr.GetAlbumAsync((int)albumId, ct).ConfigureAwait(false);
            }
            catch (LidarrException ex) when (!ct.IsCancellationRequested)
            {
                // Lidarr unreachable: keep the state, the next poll tries again.
                LogLidarr(ex);
                return;
            }

            albums[albumId] = album;
        }

        if (album is null)
        {
            await FailLockedAsync(row, AlbumReasons.LidarrRemoved, notify, unmonitor: false).ConfigureAwait(false);
            return;
        }

        var now = _time.GetUtcNow();
        if (row.State == RequestState.Upcoming)
        {
            if (IsUpcoming(album, now))
            {
                // Still before the release day: keep waiting, with the date Lidarr has now (it may have moved).
                var waiting = Details.Upcoming(album.ReleaseDate);
                if (row.Detail != waiting)
                {
                    _store.Update(row.Id, RequestState.Upcoming, now, new RequestUpdate(Detail: waiting, Year: album.ReleaseDate?.Year));
                }

                _due[row.Id] = now + RequestLimits.AlbumUpcomingPoll;
                return;
            }

            // Release day: searching from now on, so the 7-day not_found timer starts here (updated_utc).
            _store.Update(row.Id, RequestState.Searching, now, new RequestUpdate(Detail: Details.Searching, Year: album.ReleaseDate?.Year));
            _log.StateChanged(row.Id, RequestState.Upcoming, RequestState.Searching, Details.Searching);
            row = _store.Get(row.Id) ?? row;
            _due[row.Id] = now + RequestLimits.AlbumPoll;
        }

        var s = album.Statistics;
        var files = s?.TrackFileCount ?? 0;
        var expected = s?.TrackCount ?? 0;
        var total = s is { TotalTrackCount: > 0 } ? s.TotalTrackCount : Math.Max(expected, files);
        var (state, detail) = (s is null || expected == 0 || files == 0)
            ? (RequestState.Searching, Details.Searching)
            : files < expected
                ? (RequestState.Downloading, Details.Downloading(files, total))
                : (RequestState.Importing, Details.Importing);

        if (state == RequestState.Searching && row.State == RequestState.Searching && now - row.UpdatedUtc >= RequestLimits.AlbumNoFileTimeout)
        {
            await FailLockedAsync(row, AlbumReasons.NotFound, notify, unmonitor: true).ConfigureAwait(false);
            return;
        }

        if (state == RequestState.Downloading && row.State == RequestState.Downloading && row.TracksDone == files
            && now - row.UpdatedUtc >= RequestLimits.AlbumStalledTimeout)
        {
            await FailLockedAsync(row, AlbumReasons.Incomplete, notify, unmonitor: false).ConfigureAwait(false);
            return;
        }

        var tracksChanged = files > 0 && (row.TracksDone != files || row.TracksTotal != total);
        if (row.State != state || row.Detail != detail || tracksChanged)
        {
            _store.Update(row.Id, state, now, new RequestUpdate(
                Detail: detail, TracksDone: files > 0 ? files : null, TracksTotal: files > 0 ? total : null));
            if (row.State != state)
            {
                _log.StateChanged(row.Id, row.State, state, detail);
            }

            if (files != (row.TracksDone ?? 0))
            {
                _log.AlbumProgress(row.Id, files, total);
            }
        }

        if (files > 0 && !_nudged.Contains(albumId))
        {
            await NudgeAsync(row, album, ct).ConfigureAwait(false);
        }

        if (state == RequestState.Importing)
        {
            Guid? item = null;
            try
            {
                item = await _library.FindAlbumAsync(ParseUser(row.UserId), row.Artist, row.Title ?? string.Empty, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _log.WorkerError("album_library", ex.GetType().Name);
            }

            if (item is { } found)
            {
                MarkReadyLocked(_store.Get(row.Id) ?? row, found, now, notify);
            }
        }
    }

    /// <summary>The one Media/Updated with the artist folder (Jellyfin refreshes the nearest existing parent). Retried at the next poll when it fails.</summary>
    private async Task NudgeAsync(RequestRow row, LidarrAlbum album, CancellationToken ct)
    {
        try
        {
            var artistId = album.ArtistId > 0 ? album.ArtistId : (int)(row.LidarrArtistId ?? 0);
            var artist = artistId > 0 ? await _lidarr.GetArtistAsync(artistId, ct).ConfigureAwait(false) : null;
            if (artist?.Raw["path"] is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
            {
                await _library.NudgeAsync(AlbumMatch.MapPath(path, _settings.PathMap), ct).ConfigureAwait(false);
                _nudged.Add(row.LidarrAlbumId!.Value);
            }
        }
        catch (LidarrException ex) when (!ct.IsCancellationRequested)
        {
            LogLidarr(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.WorkerError("album_nudge", ex.GetType().Name);
        }
    }

    // ---- shared steps --------------------------------------------------------------------------------------------

    private async Task FailLockedAsync(RequestRow row, string reason, List<RequestRow> notify, bool unmonitor)
    {
        var now = _time.GetUtcNow();
        var detail = Details.Failure(reason);
        var attempts = row.Attempts + 1;
        _store.Update(row.Id, RequestState.Failed, now, new RequestUpdate(Detail: detail, FailReason: reason, Attempts: attempts, ClearNextTry: true));
        _due.Remove(row.Id);
        _log.StateChanged(row.Id, row.State, RequestState.Failed, detail);
        _log.Failed(row.Id, reason, attempts);
        if (unmonitor && row.LidarrAlbumId is { } albumId)
        {
            await UnmonitorIfFreeAsync(row.Id, albumId, reason).ConfigureAwait(false);
        }

        if (_store.Get(row.Id) is { } failed)
        {
            notify.Add(failed);
        }
    }

    /// <summary>PUT album/monitor false unless another active request holds the same Lidarr album. A Lidarr failure is logged only.</summary>
    private async Task UnmonitorIfFreeAsync(string requestId, long albumId, string reason)
    {
        if (_store.Active().Any(r => r.Id != requestId && r.Kind == "album" && r.LidarrAlbumId == albumId))
        {
            return;
        }

        try
        {
            await _lidarr.SetAlbumsMonitoredAsync([(int)albumId], false, CancellationToken.None).ConfigureAwait(false);
            _log.AlbumUnmonitored(requestId, reason);
        }
        catch (LidarrException ex)
        {
            LogLidarr(ex);
        }
    }

    private RequestRow MarkReadyLocked(RequestRow row, Guid? itemId, DateTimeOffset now, List<RequestRow> notify)
    {
        _store.Update(row.Id, RequestState.Ready, now, new RequestUpdate(
            Detail: "Ready", JellyfinItemId: itemId is { } item ? RequestUserKey.Of(item) : null, ReadyUtc: now,
            ClearNextTry: true, ClearFailReason: true));
        _due.Remove(row.Id);
        _log.StateChanged(row.Id, row.State, RequestState.Ready, "Ready");
        _log.Ready(row.Id, itemId is { } i ? RequestUserKey.Of(i) : string.Empty, (long)Math.Max(0, (now - row.CreatedUtc).TotalMilliseconds));
        var ready = _store.Get(row.Id) ?? row;
        notify.Add(ready);
        return ready;
    }

    /// <summary>Copies the Lidarr ids, state and progress of an active request for the same album that Lidarr already has.</summary>
    private RequestRow JoinLocked(RequestRow row, RequestRow sibling, DateTimeOffset now)
    {
        _store.Update(row.Id, sibling.State, now, new RequestUpdate(
            Detail: sibling.Detail, LidarrAlbumId: sibling.LidarrAlbumId, LidarrArtistId: sibling.LidarrArtistId,
            TracksDone: sibling.TracksDone, TracksTotal: sibling.TracksTotal, ClearNextTry: true));
        if (row.State != sibling.State)
        {
            _log.StateChanged(row.Id, row.State, sibling.State, sibling.Detail);
        }

        _due[row.Id] = now + RequestLimits.AlbumPoll;
        return _store.Get(row.Id) ?? row;
    }

    /// <summary>Another active request for the same album that Lidarr is already working on (ids known, past queued).</summary>
    private RequestRow? ActiveSibling(RequestRow row) =>
        _store.Active().FirstOrDefault(o => o.Id != row.Id && o.Kind == "album" && o.LidarrAlbumId is not null
            && o.State != RequestState.Queued
            && string.Equals(o.ForeignAlbumId, row.ForeignAlbumId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Not out yet: the release date is after today or not known, and Lidarr has no file for it.</summary>
    private static bool IsUpcoming(LidarrAlbum album, DateTimeOffset now) =>
        !AlbumSearch.IsReleased(album, now) && (album.Statistics?.TrackFileCount ?? 0) == 0;

    private RequestRow? MineActive(string userKey, string fid) =>
        _store.ListByUser(userKey, activeOnly: true).FirstOrDefault(r => r.Kind == "album"
            && string.Equals(r.ForeignAlbumId, fid, StringComparison.OrdinalIgnoreCase));

    /// <summary>The id lookup (<c>lidarr:</c> + release-group id). Null when Lidarr's catalog has no such album.</summary>
    private async Task<LidarrAlbum?> LookupAsync(string fid, CancellationToken ct)
    {
        var found = await _lidarr.LookupAlbumsAsync("lidarr:" + fid, ct).ConfigureAwait(false);
        return found.FirstOrDefault(a => string.Equals(a.ForeignAlbumId, fid, StringComparison.OrdinalIgnoreCase))
            ?? (found.Count == 1 ? found[0] : null);
    }

    /// <summary>Already in the library: a matching Jellyfin album, or complete in Lidarr. Failures only mean "not known to be owned".</summary>
    private async Task<(bool Owned, Guid? ItemId)> OwnedAsync(Guid userId, LidarrAlbum album, string artist, string title, CancellationToken ct)
    {
        try
        {
            if (await _library.FindAlbumAsync(userId, artist, title, ct).ConfigureAwait(false) is { } item)
            {
                return (true, item);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.WorkerError("album_library", ex.GetType().Name);
        }

        if (album.Id > 0)
        {
            try
            {
                var detail = await _lidarr.GetAlbumAsync(album.Id, ct).ConfigureAwait(false);
                if (AlbumSearch.IsComplete(detail?.Statistics))
                {
                    return (true, null);
                }
            }
            catch (LidarrException ex) when (!ct.IsCancellationRequested)
            {
                LogLidarr(ex);
            }
        }

        return (false, null);
    }

    private bool OverCap(RequestUser user, string userKey)
    {
        if (user.IsAdmin)
        {
            return false;
        }

        var used = _store.CountCreatedSince(userKey, "album", _time.GetUtcNow() - TimeSpan.FromHours(24));
        if (used < _settings.AlbumsPerDay)
        {
            return false;
        }

        _log.Limit(userKey, "album", _settings.AlbumsPerDay);
        return true;
    }

    private void LogLidarr(LidarrException ex)
    {
        if (ex is LidarrAuthException)
        {
            _log.LidarrAuthFailed(ex.Operation, ex.StatusCode ?? 0);
        }
        else
        {
            _log.LidarrRequestFailed(ex.Operation, ex.StatusCode, ex.Message);
        }
    }

    private static bool MayAccess(RequestUser user, RequestRow row) => user.IsAdmin || row.UserId == RequestUserKey.Of(user.Id);

    private static Guid ParseUser(string userId) => Guid.TryParseExact(userId, "N", out var id) ? id : Guid.Empty;
}
