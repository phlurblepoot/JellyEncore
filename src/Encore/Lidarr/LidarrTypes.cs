using System.Text.Json.Nodes;

namespace Encore.Lidarr;

/// <summary>GET system/status: the two fields Encore uses.</summary>
public sealed record LidarrStatus(string AppName, string Version);

/// <summary>GET rootfolder (lidarr-api.md section 4). <see cref="Path"/> has no trailing slash.</summary>
public sealed record LidarrRootFolder(int Id, string Path, int DefaultQualityProfileId, int DefaultMetadataProfileId, bool Accessible);

/// <summary>An album's <c>statistics</c> (lidarr-api.md section 6). The whole object is null when the album has no tracks on a monitored release.</summary>
public sealed record LidarrAlbumStatistics(int TrackFileCount, int TrackCount, int TotalTrackCount, long SizeOnDisk, double PercentOfTracks);

public sealed record LidarrImage(string? CoverType, string? Url, string? RemoteUrl);

public sealed record LidarrRelease(string? ForeignReleaseId, string? Title, int TrackCount, string? Format, bool Monitored);

/// <summary>The artist inside an album resource (lookup rows carry only the id, name and foreign id).</summary>
public sealed record LidarrArtistRef(int Id, string? ArtistName, string? ForeignArtistId, bool Monitored);

/// <summary>An album resource from lookup, list or detail. <see cref="Id"/> is 0 for an album Lidarr does not know yet.</summary>
public sealed record LidarrAlbum(
    int Id,
    string? Title,
    string? Disambiguation,
    int ArtistId,
    string ForeignAlbumId,
    bool Monitored,
    string? AlbumType,
    IReadOnlyList<string> SecondaryTypes,
    DateTimeOffset? ReleaseDate,
    IReadOnlyList<LidarrImage> Images,
    string? RemoteCover,
    IReadOnlyList<LidarrRelease> Releases,
    LidarrArtistRef? Artist,
    LidarrAlbumStatistics? Statistics);

/// <summary>
/// An artist exactly as Lidarr returned it. <see cref="Raw"/> is the whole resource: a PUT sends it back with only
/// <c>monitored</c> changed, because Lidarr queues a move from the stored path to the body's <c>path</c>.
/// </summary>
public sealed record LidarrArtist(
    int Id,
    string? ArtistName,
    string? ForeignArtistId,
    bool Monitored,
    string? MonitorNewItems,
    JsonObject Raw);

/// <summary>Body of POST album. <see cref="Artist"/> is a full artist object: a <see cref="LidarrArtist.Raw"/> copy, or <see cref="LidarrArtistBodies.ForNewArtist"/>.</summary>
public sealed record LidarrAddAlbum(string ForeignAlbumId, string Title, JsonObject Artist);

public static class LidarrArtistBodies
{
    /// <summary>
    /// The artist object for POST album when Lidarr lacks the artist (v03 section 7.2): monitored, and
    /// <c>monitorNewItems: "none"</c> so no other album is monitored. No <c>addOptions</c>.
    /// </summary>
    public static JsonObject ForNewArtist(string foreignArtistId, string artistName, int qualityProfileId, int metadataProfileId, string rootFolderPath) =>
        new()
        {
            ["foreignArtistId"] = foreignArtistId,
            ["artistName"] = artistName,
            ["qualityProfileId"] = qualityProfileId,
            ["metadataProfileId"] = metadataProfileId,
            ["rootFolderPath"] = rootFolderPath,
            ["monitored"] = true,
            ["monitorNewItems"] = "none",
        };
}
