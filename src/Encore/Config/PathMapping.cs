namespace Encore.Config;

/// <summary>A folder prefix as Lidarr sees it and the same folder as Jellyfin sees it (ENCORE_LIDARR_PATH_MAP).</summary>
public sealed record PathMapping(string LidarrPrefix, string JellyfinPrefix)
{
    public override string ToString() => $"{LidarrPrefix}={JellyfinPrefix}";
}
