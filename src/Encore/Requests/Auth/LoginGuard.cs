using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Encore.Requests.Auth;

/// <summary>The guard's answer: allowed, or refused with the scope that refused and how long to wait.</summary>
public sealed record GuardVerdict(bool Allowed, string Scope, TimeSpan RetryAfter)
{
    public static readonly GuardVerdict Allow = new(true, string.Empty, TimeSpan.Zero);
}

/// <summary>Counts for /health: IPs blocked now (login or token), names locked now, failed logins in the last hour.</summary>
public sealed record GuardSnapshot(int BlockedIps, int LockedNames, int FailedLoginsLastHour);

/// <summary>Scope names used in <see cref="GuardVerdict"/> and in log 9030.</summary>
public static class GuardScopes
{
    public const string Ip = "ip";
    public const string Name = "name";
    public const string Global = "global";
    public const string Token = "token";
}

/// <summary>
/// In-memory brute-force guard (design 3.5). Per IP: 5 failed logins in 15 min block that IP for 15 min. Per name
/// (folded): 10 failures in 1 h lock the name for 1 h from every IP, or, for a user with a Jellyfin lockout limit,
/// max(1, limit - 1) failures in 24 h lock it for 24 h. Global: 30 login attempts per minute. Quick Connect start:
/// 10 per IP per 15 min. Failed token checks: 30 per IP per 5 min block that IP on every /api/v1 route for 15 min.
/// A successful login clears that IP's and that name's failure counts. Logs 9030 once per block.
/// The IP is the client IP the middleware worked out (design 3.6); this class never reads headers.
/// </summary>
public sealed class LoginGuard
{
    private const int PruneAbove = 10_000;

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<string, int?> _lockoutLimit;
    private readonly object _gate = new();

    private readonly Dictionary<string, Queue<DateTimeOffset>> _ipFails = [];
    private readonly Dictionary<string, DateTimeOffset> _ipBlocked = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _nameFails = [];
    private readonly Dictionary<string, DateTimeOffset> _nameLocked = [];
    private readonly Queue<DateTimeOffset> _attempts = new();
    private readonly Queue<DateTimeOffset> _failureLog = new();
    private bool _globalLogged;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _quickConnect = [];
    private readonly HashSet<string> _quickConnectLogged = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _tokenFails = [];
    private readonly Dictionary<string, DateTimeOffset> _tokenBlocked = [];

    /// <param name="lockoutLimit">Folded name to the user's Jellyfin lockout limit (above 0), or null for none.</param>
    public LoginGuard(TimeProvider time, ILogger<LoginGuard> logger, Func<string, int?>? lockoutLimit = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lockoutLimit = lockoutLimit ?? (static _ => null);
    }

    /// <summary>The failure count and window that lock a name, given the user's Jellyfin limit (null or 0 = none).</summary>
    public static (int Failures, TimeSpan Window) NameRule(int? jellyfinLimit) =>
        jellyfinLimit is > 0
            ? (Math.Max(1, jellyfinLimit.Value - 1), RequestLimits.LoginNameLockWindow)
            : (RequestLimits.LoginFailuresPerName, RequestLimits.LoginNameWindow);

    /// <summary>Called before a login reaches Jellyfin. Counts the attempt towards the global cap when it is let through.</summary>
    public GuardVerdict CheckLogin(IPAddress? ip, string name)
    {
        var ipKey = Key(ip);
        var nameKey = RequestUsers.FoldName(name);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (BlockedUntil(_tokenBlocked, ipKey, now) is { } token)
            {
                return Refuse(GuardScopes.Token, token, now);
            }

            if (BlockedUntil(_ipBlocked, ipKey, now) is { } ipUntil)
            {
                return Refuse(GuardScopes.Ip, ipUntil, now);
            }

            if (BlockedUntil(_nameLocked, nameKey, now) is { } nameUntil)
            {
                return Refuse(GuardScopes.Name, nameUntil, now);
            }

            return CountGlobal(now);
        }
    }

    /// <summary>Called for a Quick Connect start: 10 per IP per 15 min, plus the global cap.</summary>
    public GuardVerdict CheckQuickConnectStart(IPAddress? ip)
    {
        var ipKey = Key(ip);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (BlockedUntil(_tokenBlocked, ipKey, now) is { } token)
            {
                return Refuse(GuardScopes.Token, token, now);
            }

            if (BlockedUntil(_ipBlocked, ipKey, now) is { } ipUntil)
            {
                return Refuse(GuardScopes.Ip, ipUntil, now);
            }

            var starts = Window(_quickConnect, ipKey, RequestLimits.QuickConnectIpWindow, now);
            if (starts.Count >= RequestLimits.QuickConnectStartsPerIp)
            {
                var until = starts.Peek() + RequestLimits.QuickConnectIpWindow;
                if (_quickConnectLogged.Add(ipKey))
                {
                    _logger.LoginLimited(GuardScopes.Ip, ipKey, string.Empty, until);
                }

                return Refuse(GuardScopes.Ip, until, now);
            }

            _quickConnectLogged.Remove(ipKey);
            var global = CountGlobal(now);
            if (global.Allowed)
            {
                starts.Enqueue(now);
                Prune(_quickConnect, RequestLimits.QuickConnectIpWindow, now);
            }

            return global;
        }
    }

    /// <summary>Is this IP blocked on all /api/v1 routes because of too many failed token checks?</summary>
    public GuardVerdict CheckTokenBlock(IPAddress? ip)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return BlockedUntil(_tokenBlocked, Key(ip), now) is { } until ? Refuse(GuardScopes.Token, until, now) : GuardVerdict.Allow;
        }
    }

    /// <summary>A login failed at Jellyfin (wrong name or password): counts for the IP and for the name.</summary>
    public void RecordLoginFailure(IPAddress? ip, string name)
    {
        var ipKey = Key(ip);
        var nameKey = RequestUsers.FoldName(name);
        var now = _time.GetUtcNow();
        var (failures, window) = NameRule(_lockoutLimit(nameKey));
        lock (_gate)
        {
            _failureLog.Enqueue(now);
            TrimFailureLog(now);

            var ipQueue = Window(_ipFails, ipKey, RequestLimits.LoginIpWindow, now);
            ipQueue.Enqueue(now);
            if (ipQueue.Count >= RequestLimits.LoginFailuresPerIp && !_ipBlocked.ContainsKey(ipKey))
            {
                var until = now + RequestLimits.LoginIpBlock;
                _ipBlocked[ipKey] = until;
                _logger.LoginLimited(GuardScopes.Ip, ipKey, string.Empty, until);
            }

            var nameQueue = Window(_nameFails, nameKey, window, now);
            nameQueue.Enqueue(now);
            if (nameQueue.Count >= failures && !_nameLocked.ContainsKey(nameKey))
            {
                var until = now + window;
                _nameLocked[nameKey] = until;
                _logger.LoginLimited(GuardScopes.Name, ipKey, NameHash(nameKey), until);
            }

            Prune(_ipFails, RequestLimits.LoginIpWindow, now);
            Prune(_nameFails, RequestLimits.LoginNameLockWindow, now);
        }
    }

    /// <summary>A login worked: clears that IP's and that name's failure counts (and any block that was running).</summary>
    public void RecordLoginSuccess(IPAddress? ip, string name)
    {
        var ipKey = Key(ip);
        var nameKey = RequestUsers.FoldName(name);
        lock (_gate)
        {
            _ipFails.Remove(ipKey);
            _ipBlocked.Remove(ipKey);
            _nameFails.Remove(nameKey);
            _nameLocked.Remove(nameKey);
        }
    }

    /// <summary>A Bearer token check failed (401): 30 per IP per 5 min block that IP for 15 min.</summary>
    public void RecordTokenFailure(IPAddress? ip)
    {
        var ipKey = Key(ip);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var queue = Window(_tokenFails, ipKey, RequestLimits.TokenFailureWindow, now);
            queue.Enqueue(now);
            if (queue.Count >= RequestLimits.TokenFailuresPerIp && !_tokenBlocked.ContainsKey(ipKey))
            {
                var until = now + RequestLimits.TokenBlock;
                _tokenBlocked[ipKey] = until;
                _tokenFails.Remove(ipKey);
                _logger.LoginLimited(GuardScopes.Token, ipKey, string.Empty, until);
            }

            Prune(_tokenFails, RequestLimits.TokenFailureWindow, now);
        }
    }

    /// <summary>What /health shows: counts only, never an IP, a name or a token.</summary>
    public GuardSnapshot Snapshot()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            TrimFailureLog(now);
            return new GuardSnapshot(
                _ipBlocked.Count(p => p.Value > now) + _tokenBlocked.Count(p => p.Value > now),
                _nameLocked.Count(p => p.Value > now),
                _failureLog.Count);
        }
    }

    private void TrimFailureLog(DateTimeOffset now)
    {
        while (_failureLog.Count > 0 && now - _failureLog.Peek() >= TimeSpan.FromHours(1))
        {
            _failureLog.Dequeue();
        }
    }

    /// <summary>First 12 hex of SHA-256 over the folded name: what the logs show instead of the name.</summary>
    public static string NameHash(string foldedName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(foldedName)))[..12].ToLowerInvariant();

    private GuardVerdict CountGlobal(DateTimeOffset now)
    {
        while (_attempts.Count > 0 && now - _attempts.Peek() >= TimeSpan.FromMinutes(1))
        {
            _attempts.Dequeue();
        }

        if (_attempts.Count >= RequestLimits.LoginAttemptsPerMinute)
        {
            var until = _attempts.Peek() + TimeSpan.FromMinutes(1);
            if (!_globalLogged)
            {
                _globalLogged = true;
                _logger.LoginLimited(GuardScopes.Global, string.Empty, string.Empty, until);
            }

            return Refuse(GuardScopes.Global, until, now);
        }

        _globalLogged = false;
        _attempts.Enqueue(now);
        return GuardVerdict.Allow;
    }

    private static GuardVerdict Refuse(string scope, DateTimeOffset until, DateTimeOffset now)
    {
        var wait = until - now;
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        return new GuardVerdict(false, scope, TimeSpan.FromSeconds(seconds));
    }

    private static DateTimeOffset? BlockedUntil(Dictionary<string, DateTimeOffset> blocks, string key, DateTimeOffset now)
    {
        if (!blocks.TryGetValue(key, out var until))
        {
            return null;
        }

        if (until > now)
        {
            return until;
        }

        blocks.Remove(key);
        return null;
    }

    /// <summary>The key's queue with entries older than the window dropped (created when missing).</summary>
    private static Queue<DateTimeOffset> Window(Dictionary<string, Queue<DateTimeOffset>> map, string key, TimeSpan window, DateTimeOffset now)
    {
        if (!map.TryGetValue(key, out var queue))
        {
            queue = new Queue<DateTimeOffset>();
            map[key] = queue;
        }

        while (queue.Count > 0 && now - queue.Peek() >= window)
        {
            queue.Dequeue();
        }

        return queue;
    }

    /// <summary>Keeps the maps small: names and IPs are attacker-chosen, so drop empty or expired entries once there are many.</summary>
    private static void Prune(Dictionary<string, Queue<DateTimeOffset>> map, TimeSpan window, DateTimeOffset now)
    {
        if (map.Count <= PruneAbove)
        {
            return;
        }

        foreach (var key in map.Where(p => p.Value.Count == 0 || now - p.Value.Last() >= window).Select(p => p.Key).ToList())
        {
            map.Remove(key);
        }
    }

    private static string Key(IPAddress? ip)
    {
        if (ip is null)
        {
            return "unknown";
        }

        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
