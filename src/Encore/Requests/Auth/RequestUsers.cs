using Encore.Jellyfin;
using Encore.Logging;
using Microsoft.Extensions.Hosting;

namespace Encore.Requests.Auth;

/// <summary>
/// Who may create requests (design 3.2, C2): admins always, plus ENCORE_REQUEST_USERS (names, ids or *)
/// resolved with the v0.1 <see cref="AllowList"/>. Also holds each user's Jellyfin lockout limit
/// (Policy.LoginAttemptsBeforeLockout) for the login guard, and (T114) every user's name, admin and disabled flags and
/// session limit for the request page's own tokens. All come from one GET /Users at start and every 10 minutes; a
/// failed refresh keeps the previous answer.
/// </summary>
public sealed class RequestUsers : BackgroundService
{
    private sealed record Snapshot(
        IReadOnlySet<Guid> Ids,
        IReadOnlyDictionary<string, int> Limits,
        IReadOnlySet<string> Names,
        IReadOnlyDictionary<Guid, RequestUser> Users,
        IReadOnlyDictionary<string, (bool Disabled, int SessionLimit)> ByName);

    private static readonly Snapshot Empty = new(
        new HashSet<Guid>(), new Dictionary<string, int>(), new HashSet<string>(),
        new Dictionary<Guid, RequestUser>(), new Dictionary<string, (bool, int)>());

    private readonly IJellyfinClient _jellyfin;
    private readonly IReadOnlyList<string> _entries;
    private readonly bool _all;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile Snapshot _snapshot = Empty;
    private int _loggedUsersWithLimit = -1;
    private int _loggedLowest = -1;
    private DateTimeOffset _lastOnDemand = DateTimeOffset.MinValue;

    public RequestUsers(IJellyfinClient jellyfin, IReadOnlyList<string> entries, bool all, ILogger<RequestUsers> logger, TimeProvider? time = null)
    {
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _all = all;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True once one refresh has succeeded.</summary>
    public bool Loaded { get; private set; }

    /// <summary>When the last successful refresh finished (MinValue before the first).</summary>
    public DateTimeOffset LastRefreshUtc { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Raised after every successful refresh with every Jellyfin user by id (T114: page tokens of deleted or disabled users go).</summary>
    public event Action<IReadOnlyDictionary<Guid, RequestUser>>? Refreshed;

    /// <summary>May this validated user create requests? Admins always; others when listed or when the list is *.</summary>
    public bool CanRequest(RequestUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsAdmin || _all || _snapshot.Ids.Contains(user.Id);
    }

    /// <summary>The Jellyfin lockout limit of a user (above 0), or null when the user has none or is unknown. The name is folded here.</summary>
    public int? LockoutLimit(string name) =>
        _snapshot.Limits.TryGetValue(FoldName(name), out var limit) ? limit : null;

    /// <summary>Does a Jellyfin user with this name exist (as of the last refresh)? Keeps typed junk out of logs.</summary>
    public bool IsKnownName(string name) => _snapshot.Names.Contains(FoldName(name));

    /// <summary>The user with this id as of the last refresh (name, admin, disabled), or null when Jellyfin has no such user.</summary>
    public RequestUser? Find(Guid id) => _snapshot.Users.TryGetValue(id, out var user) ? user : null;

    /// <summary>
    /// Is the user with this name known, not disabled, and limited to a number of Jellyfin sessions (MaxActiveSessions
    /// of 1 or more)? Only then can Jellyfin's 403 at sign-in mean "at the session limit" (T114).
    /// </summary>
    public bool HasSessionLimit(string name) =>
        _snapshot.ByName.TryGetValue(FoldName(name), out var info) && !info.Disabled && info.SessionLimit >= 1;

    /// <summary>Case-folded, trimmed name: the key for the per-name guard and for known-name checks.</summary>
    public static string FoldName(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Makes sure the user data is at most <paramref name="maxAge"/> old (and, when <paramref name="notBefore"/> is set, from a refresh at or after it):
    /// otherwise it refreshes now, at most once every 30 seconds.
    /// Returns whether the data is fresh enough afterwards.
    /// </summary>
    public async Task<bool> EnsureFreshAsync(TimeSpan maxAge, DateTimeOffset? notBefore = null, CancellationToken ct = default)
    {
        bool Fresh() => _time.GetUtcNow() - LastRefreshUtc <= maxAge && (notBefore is null || LastRefreshUtc >= notBefore);
        if (Fresh())
        {
            return true;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh())
            {
                return true;
            }

            var now = _time.GetUtcNow();
            if (now - _lastOnDemand < RequestLimits.UserRefreshOnDemandGap)
            {
                return false;
            }

            _lastOnDemand = now;
            await RefreshCoreAsync(ct).ConfigureAwait(false);
            return Fresh();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>One GET /Users with Encore's key. Returns false (and keeps the old data) when Jellyfin fails.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken ct)
    {
        IReadOnlyList<UserDto> users;
        try
        {
            users = await _jellyfin.GetUsersAsync(ct).ConfigureAwait(false);
        }
        catch (JellyfinException ex)
        {
            _logger.JellyfinFailed("GetUsers", ex.StatusCode ?? 0, ex.Message);
            return false;
        }

        var resolution = AllowList.Resolve(_all ? [] : _entries, users);
        if (resolution.Problems.Count > 0)
        {
            _logger.AllowListUnresolved(string.Join(", ", resolution.Problems.Select(p => p.Entry)));
        }

        var limits = new Dictionary<string, int>();
        var byName = new Dictionary<string, (bool, int)>();
        var byId = new Dictionary<Guid, RequestUser>();
        foreach (var user in users)
        {
            var limit = user.Policy?.LoginAttemptsBeforeLockout;
            if (user.Name is { Length: > 0 } name)
            {
                if (limit is > 0)
                {
                    limits[FoldName(name)] = limit.Value;
                }

                byName[FoldName(name)] = (user.Policy?.IsDisabled ?? false, user.Policy?.MaxActiveSessions ?? 0);
                if (user.Id != Guid.Empty)
                {
                    byId[user.Id] = new RequestUser(user.Id, name, user.Policy?.IsAdministrator ?? false, user.Policy?.IsDisabled ?? false);
                }
            }
        }

        var names = users.Where(u => !string.IsNullOrWhiteSpace(u.Name)).Select(u => FoldName(u.Name!)).ToHashSet();
        _snapshot = new Snapshot(resolution.UserIds, limits, names, byId, byName);
        LastRefreshUtc = _time.GetUtcNow();
        Loaded = true;

        // 9050: at start and when the numbers change; counts only, never names.
        var lowest = limits.Count == 0 ? 0 : limits.Values.Min();
        if (limits.Count != _loggedUsersWithLimit || lowest != _loggedLowest)
        {
            _loggedUsersWithLimit = limits.Count;
            _loggedLowest = lowest;
            _logger.LockoutPolicies(limits.Count, lowest);
        }

        Refreshed?.Invoke(byId);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(RequestLimits.UserRefresh, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        _refreshGate.Dispose();
        base.Dispose();
    }
}
