namespace Encore.Requests;

/// <summary>Fixed v0.3 request limits (design section 11 and 3.5). Later request tasks add their own constants here.</summary>
public static class RequestLimits
{
    public static readonly TimeSpan TokenCacheTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TokenNegativeTtl = TimeSpan.FromSeconds(10);
    public const int TokenCacheMax = 1000;
    public const int TokenMinLength = 16;
    public const int TokenMaxLength = 128;

    public static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan JellyfinTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan UserRefresh = TimeSpan.FromMinutes(10);

    /// <summary>Wait before the next try at reading the Jellyfin server id (GET /System/Info/Public) after a failure.</summary>
    public static readonly TimeSpan ServerInfoRetry = TimeSpan.FromMinutes(1);

    /// <summary>The page asks Jellyfin "is Quick Connect on?" at most this often; the answer is cached.</summary>
    public static readonly TimeSpan QuickConnectStatusTtl = TimeSpan.FromSeconds(30);

    /// <summary>Largest JSON body the auth routes read.</summary>
    public const int ApiBodyMaxBytes = 16 * 1024;

    public const int LoginFailuresPerIp = 5;
    public static readonly TimeSpan LoginIpWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LoginIpBlock = TimeSpan.FromMinutes(15);

    public const int LoginFailuresPerName = 10;
    public static readonly TimeSpan LoginNameWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan LoginNameLockWindow = TimeSpan.FromHours(24);

    public const int LoginAttemptsPerMinute = 30;
    public const int QuickConnectStartsPerIp = 10;
    public static readonly TimeSpan QuickConnectIpWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan QuickConnectSecretTtl = TimeSpan.FromMinutes(6);

    public const int TokenFailuresPerIp = 30;
    public static readonly TimeSpan TokenFailureWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TokenBlock = TimeSpan.FromMinutes(15);

    /// <summary>Search (design 5): query length, rows asked for from each source, and each source's own timeout.</summary>
    public const int SearchQueryMin = 2;
    public const int SearchQueryMax = 100;
    public const int SearchLibraryLimit = 20;
    public const int SearchDeezerLimit = 25;
    public static readonly TimeSpan SearchJellyfinTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SearchDeezerTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan SearchLidarrTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Album search rows shown at most (Lidarr's text lookup can return many).</summary>
    public const int SearchAlbumLimit = 25;

    /// <summary>Album requests (design 7 and 11): text lookups cached 1 h; each active album polled every 2 min.</summary>
    public static readonly TimeSpan LidarrLookupCacheTtl = TimeSpan.FromHours(1);
    public const int LidarrLookupCacheMax = 200;
    public static readonly TimeSpan AlbumPoll = TimeSpan.FromMinutes(2);

    /// <summary>An upcoming album (not released yet) re-reads its release date from Lidarr this often.</summary>
    public static readonly TimeSpan AlbumUpcomingPoll = TimeSpan.FromHours(6);

    /// <summary>Still searching with no file this long after searching began (the release day for an upcoming album): failed <c>not_found</c>, album unmonitored.</summary>
    public static readonly TimeSpan AlbumNoFileTimeout = TimeSpan.FromDays(7);

    /// <summary>Partly downloaded and unchanged this long: failed <c>incomplete</c>, album left as it is.</summary>
    public static readonly TimeSpan AlbumStalledTimeout = TimeSpan.FromDays(3);

    /// <summary>In Lidarr but not in Jellyfin this long: counted for the /health warning (the request stays importing).</summary>
    public static readonly TimeSpan AlbumJellyfinWait = TimeSpan.FromHours(2);
}
