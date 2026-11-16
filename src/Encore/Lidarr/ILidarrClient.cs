namespace Encore.Lidarr;

/// <summary>
/// Lidarr 3.1.6 API v1 client (see .knowledge/encore/lidarr-api.md). Failures throw <see cref="LidarrException"/> subclasses.
/// There is no POST artist: albums are added through <see cref="AddAlbumAsync"/> (v03 section 7).
/// </summary>
public interface ILidarrClient
{
    /// <summary>GET /api/v1/system/status.</summary>
    Task<LidarrStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>GET /api/v1/rootfolder.</summary>
    Task<IReadOnlyList<LidarrRootFolder>> GetRootFoldersAsync(CancellationToken ct);

    /// <summary>GET /api/v1/album/lookup?term= (plain text, or lidarr:/lidarrid:/mbid: plus a release-group GUID). No statistics.</summary>
    Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct);

    /// <summary>GET /api/v1/artist?mbId=; null when Lidarr does not have the artist.</summary>
    Task<LidarrArtist?> FindArtistByForeignIdAsync(string foreignArtistId, CancellationToken ct);

    /// <summary>GET /api/v1/artist/{id}; null on 404.</summary>
    Task<LidarrArtist?> GetArtistAsync(int id, CancellationToken ct);

    /// <summary>
    /// GET /api/v1/artist/{id}, then PUT it back unchanged except <c>monitored</c>. Returns the artist as Lidarr answered the PUT
    /// (or the sent one when the answer has no body); null when the artist is gone.
    /// </summary>
    Task<LidarrArtist?> SetArtistMonitoredAsync(int id, bool monitored, CancellationToken ct);

    /// <summary>GET /api/v1/album?artistId= (all of the artist's albums, unmonitored included, with statistics).</summary>
    Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken ct);

    /// <summary>GET /api/v1/album?foreignAlbumId= ; empty when unknown (not 404). With <paramref name="includeAllArtistAlbums"/> it returns that artist's albums.</summary>
    Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByForeignIdAsync(string foreignAlbumId, bool includeAllArtistAlbums, CancellationToken ct);

    /// <summary>GET /api/v1/album?albumIds=a&amp;albumIds=b (one batched call, with statistics). No call for an empty list.</summary>
    Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);

    /// <summary>GET /api/v1/album/{id} (full artist object, statistics); null on 404.</summary>
    Task<LidarrAlbum?> GetAlbumAsync(int id, CancellationToken ct);

    /// <summary>
    /// GET /api/v1/trackfile?albumId= : how many track files Lidarr has for the album, read straight from its database.
    /// Album <c>statistics</c> come from a per-artist cache in Lidarr that can miss an import; this count cannot.
    /// A 404 (album gone) gives 0.
    /// </summary>
    Task<int> CountTrackFilesAsync(int albumId, CancellationToken ct);

    /// <summary>POST /api/v1/album with monitored true, anyReleaseOk true and addType "manual", no search. 400 "already been added" is a <see cref="LidarrValidationException"/>.</summary>
    Task<LidarrAlbum> AddAlbumAsync(LidarrAddAlbum request, CancellationToken ct);

    /// <summary>PUT /api/v1/album/monitor {albumIds, monitored}. Returns the updated albums.</summary>
    Task<IReadOnlyList<LidarrAlbum>> SetAlbumsMonitoredAsync(IReadOnlyCollection<int> albumIds, bool monitored, CancellationToken ct);
}
