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
}
