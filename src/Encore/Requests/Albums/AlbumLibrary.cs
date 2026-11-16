using Encore.Config;
using Encore.Discovery;
using Encore.Jellyfin;

namespace Encore.Requests.Albums;

/// <summary>What album requests need from Jellyfin: find an album in a person's library, and the one Media/Updated nudge.</summary>
public interface IAlbumLibrary
{
    /// <summary>
    /// A MusicAlbum in the user's library whose folded album artist and name match (v03 7.3); null when there is none.
    /// Throws on a Jellyfin failure.
    /// </summary>
    Task<Guid?> FindAlbumAsync(Guid userId, string artist, string title, CancellationToken ct);

    /// <summary>POST /Library/Media/Updated for one folder as Jellyfin sees it. Throws on a Jellyfin failure.</summary>
    Task NudgeAsync(string jellyfinPath, CancellationToken ct);
}

/// <summary>Album matching rules shared by the search rows and the request lifecycle.</summary>
public static class AlbumMatch
{
    /// <summary>Same album when the folded artist and the folded title (version notes dropped) are equal and not empty.</summary>
    public static bool Same(string? artistA, string? titleA, string? artistB, string? titleB)
    {
        var titleKey = KeyText.TitleKey(titleA);
        var artistKey = KeyText.ArtistKey(artistA);
        return titleKey.Length > 0 && artistKey.Length > 0
            && titleKey == KeyText.TitleKey(titleB)
            && artistKey == KeyText.ArtistKey(artistB);
    }

    /// <summary>
    /// A Lidarr path as Jellyfin sees it (ENCORE_LIDARR_PATH_MAP). A path outside the Lidarr prefix is returned as is.
    /// Prefixes match on whole folder names only.
    /// </summary>
    public static string MapPath(string lidarrPath, PathMapping map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var path = lidarrPath.TrimEnd('/');
        var from = map.LidarrPrefix.TrimEnd('/');
        var to = map.JellyfinPrefix.TrimEnd('/');
        if (path == from)
        {
            return to.Length == 0 ? "/" : to;
        }

        return from.Length > 0 && path.StartsWith(from + "/", StringComparison.Ordinal)
            ? to + path[from.Length..]
            : path;
    }
}

/// <summary>The Jellyfin side of album requests, over the shared <see cref="IJellyfinClient"/> (Encore's key).</summary>
public sealed class JellyfinAlbumLibrary(IJellyfinClient jellyfin) : IAlbumLibrary
{
    /// <summary>Rows asked for per album search (songs with the same words come back too, so keep some room).</summary>
    public const int SearchLimit = 50;

    public async Task<Guid?> FindAlbumAsync(Guid userId, string artist, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // The user id keeps the requester's library access rules, as in the search (design 5 item 1).
        var result = await jellyfin.SearchItemsAsync(title.Trim(), userId, SearchLimit, ct).ConfigureAwait(false);
        foreach (var item in result.Items)
        {
            if (item.Id == Guid.Empty || !string.Equals(item.Type, "MusicAlbum", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var itemArtist = item.AlbumArtist ?? item.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name))?.Name;
            if (AlbumMatch.Same(itemArtist, item.Name, artist, title))
            {
                return item.Id;
            }
        }

        return null;
    }

    public Task NudgeAsync(string jellyfinPath, CancellationToken ct) => jellyfin.ReportMediaUpdatedAsync([jellyfinPath], ct);
}
