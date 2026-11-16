using System.Collections.Concurrent;
using System.Globalization;
using Encore.Lidarr;
using Encore.Requests.Search;
using Encore.Store;

namespace Encore.Requests.Albums;

/// <summary>
/// One album from Lidarr's catalog (design 5 item 3). <see cref="Owned"/> rows (complete in Lidarr, or a matching album in
/// the person's Jellyfin library) are not requestable; <see cref="ItemId"/> is set when the Jellyfin album is known.
/// <see cref="Released"/> false rows (release date in the future or not known yet) are still requestable: the page shows an
/// Upcoming badge with <see cref="ReleaseDate"/> (YYYY-MM-DD, null when unknown) next to the Request button.
/// </summary>
public sealed record AlbumRow(
    string ForeignAlbumId,
    string Title,
    string Artist,
    string? ForeignArtistId,
    int? Year,
    string? AlbumType,
    IReadOnlyList<string> SecondaryTypes,
    int? TrackCount,
    string? CoverUrl,
    bool Released,
    string? ReleaseDate,
    bool Owned,
    string? ItemId,
    bool Requestable,
    ActiveRequestRef? Request);

/// <summary>A text lookup and the statistics of the rows Lidarr already has (by album id).</summary>
public sealed record AlbumLookup(IReadOnlyList<LidarrAlbum> Albums, IReadOnlyDictionary<int, LidarrAlbumStatistics?> Statistics);

/// <summary>
/// The albums part of the search: <c>GET /api/v1/album/lookup?term=</c> cached for 1 h per folded query (empty answers are not cached), plus one batched
/// <c>GET /api/v1/album?albumIds=</c> per search for the rows already in Lidarr (lookup rows carry no statistics). Reads only.
/// </summary>
public sealed class AlbumSearch(ILidarrClient lidarr, ILogger<AlbumSearch> logger, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<LidarrAlbum> Albums)> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The lookup (cached) and the live statistics of rows with an id. Lookup failures throw; a statistics failure only drops the statistics.</summary>
    public async Task<AlbumLookup> LookupAsync(string query, CancellationToken ct)
    {
        var key = query.Trim().ToLowerInvariant();
        var now = _time.GetUtcNow();
        IReadOnlyList<LidarrAlbum> albums;
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > now)
        {
            albums = hit.Albums;
        }
        else
        {
            var found = await lidarr.LookupAlbumsAsync(query.Trim(), ct).ConfigureAwait(false);
            albums = found.Where(a => !string.IsNullOrWhiteSpace(a.ForeignAlbumId)).Take(RequestLimits.SearchAlbumLimit).ToList();
            // An empty answer is not remembered: a retry of the same words asks Lidarr again (it may have just been slow or cold).
            if (albums.Count > 0 && _cache.Count >= RequestLimits.LidarrLookupCacheMax)
            {
                foreach (var old in _cache.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
                {
                    _cache.TryRemove(old, out _);
                }

                if (_cache.Count >= RequestLimits.LidarrLookupCacheMax)
                {
                    _cache.Clear();
                }
            }

            if (albums.Count > 0)
            {
                _cache[key] = (now + RequestLimits.LidarrLookupCacheTtl, albums);
            }
        }

        var ids = albums.Where(a => a.Id > 0).Select(a => a.Id).Distinct().ToList();
        var stats = new Dictionary<int, LidarrAlbumStatistics?>();
        if (ids.Count > 0)
        {
            try
            {
                foreach (var album in await lidarr.GetAlbumsByIdsAsync(ids, ct).ConfigureAwait(false))
                {
                    stats[album.Id] = album.Statistics;
                }
            }
            catch (LidarrException ex)
            {
                // The rows still show; they just cannot say "complete in Lidarr".
                logger.LidarrRequestFailed(ex.Operation, ex.StatusCode, ex.Message);
            }
        }

        return new AlbumLookup(albums, stats);
    }

    /// <summary>The rows of one search: owned by Lidarr statistics or a library album with the same folded artist and title; active requests attached.</summary>
    public static IReadOnlyList<AlbumRow> BuildRows(
        AlbumLookup lookup,
        IReadOnlyList<LibraryAlbum> library,
        IReadOnlyList<RequestRow> activeRequests,
        string userKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var rows = new List<AlbumRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in lookup.Albums)
        {
            if (!seen.Add(album.ForeignAlbumId))
            {
                continue;
            }

            var artist = album.Artist?.ArtistName?.Trim() ?? string.Empty;
            var title = album.Title?.Trim() ?? string.Empty;
            var libraryHit = library.FirstOrDefault(l => AlbumMatch.Same(l.Artist, l.Title, artist, title));
            var complete = album.Id > 0 && lookup.Statistics.TryGetValue(album.Id, out var s) && IsComplete(s);
            var owned = complete || libraryHit is not null;
            var released = IsReleased(album, now);
            ActiveRequestRef? request = null;
            if (!owned && activeRequests.FirstOrDefault(r => r.Kind == "album"
                    && string.Equals(r.ForeignAlbumId, album.ForeignAlbumId, StringComparison.OrdinalIgnoreCase)) is { } active)
            {
                request = new ActiveRequestRef(active.Id, active.State, active.Detail, active.UserId == userKey, active.NextTryUtc);
            }

            rows.Add(new AlbumRow(
                album.ForeignAlbumId,
                title,
                artist,
                album.Artist?.ForeignArtistId,
                album.ReleaseDate?.Year,
                album.AlbumType,
                album.SecondaryTypes,
                TrackCount(album),
                CoverOf(album),
                released,
                album.ReleaseDate?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                owned,
                libraryHit?.ItemId,
                !owned,
                request));
        }

        return rows;
    }

    /// <summary>Released when the date is known and is today or earlier (UTC days). Lidarr never lists an album without one as wanted.</summary>
    public static bool IsReleased(LidarrAlbum album, DateTimeOffset now) =>
        album.ReleaseDate is { } date && date.UtcDateTime.Date <= now.UtcDateTime.Date;

    /// <summary>Complete in Lidarr: tracks expected and all of them have a file (design 7.1; trackCount 0 is never complete).</summary>
    public static bool IsComplete(LidarrAlbumStatistics? s) => s is { TrackCount: > 0 } && s.TrackFileCount >= s.TrackCount;

    /// <summary>The largest release's track count; null when unknown (an id lookup may return no releases).</summary>
    public static int? TrackCount(LidarrAlbum album) =>
        album.Releases.Count == 0 ? null : album.Releases.Max(r => r.TrackCount) is > 0 and var n ? n : null;

    /// <summary><c>remoteCover</c>, else the first image's <c>remoteUrl</c>; https only (the page's CSP allows https images).</summary>
    public static string? CoverOf(LidarrAlbum album)
    {
        var url = album.RemoteCover;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = album.Images.FirstOrDefault(i => string.Equals(i.CoverType, "cover", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.RemoteUrl))?.RemoteUrl
                ?? album.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.RemoteUrl))?.RemoteUrl;
        }

        return url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
    }
}
