using Encore.Jellyfin;
using Encore.Logging;
using Microsoft.Extensions.Hosting;

namespace Encore.Requests.Auth;

/// <summary>
/// Who may create requests (design 3.2, C2): admins always, plus ENCORE_REQUEST_USERS (names, ids or *)
/// resolved with the v0.1 <see cref="AllowList"/>. Also holds each user's Jellyfin lockout limit
/// (Policy.LoginAttemptsBeforeLockout) for the login guard. Both come from one GET /Users at start and every
/// 10 minutes; a failed refresh keeps the previous answer.
/// </summary>
public sealed class RequestUsers : BackgroundService
{
    private sealed record Snapshot(IReadOnlySet<Guid> Ids, IReadOnlyDictionary<string, int> Limits, IReadOnlySet<string> Names);

    private static readonly Snapshot Empty = new(new HashSet<Guid>(), new Dictionary<string, int>(), new HashSet<string>());

    private readonly IJellyfinClient _jellyfin;
    private readonly IReadOnlyList<string> _entries;
    private readonly bool _all;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private volatile Snapshot _snapshot = Empty;
    private int _loggedUsersWithLimit = -1;
    private int _loggedLowest = -1;

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

    /// <summary>Case-folded, trimmed name: the key for the per-name guard and for known-name checks.</summary>
    public static string FoldName(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>One GET /Users with Encore's key. Returns false (and keeps the old data) when Jellyfin fails.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
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
        foreach (var user in users)
        {
            var limit = user.Policy?.LoginAttemptsBeforeLockout;
            if (user.Name is { Length: > 0 } name && limit is > 0)
            {
                limits[FoldName(name)] = limit.Value;
            }
        }

        var names = users.Where(u => !string.IsNullOrWhiteSpace(u.Name)).Select(u => FoldName(u.Name!)).ToHashSet();
        _snapshot = new Snapshot(resolution.UserIds, limits, names);
        Loaded = true;

        // 9050: at start and when the numbers change; counts only, never names.
        var lowest = limits.Count == 0 ? 0 : limits.Values.Min();
        if (limits.Count != _loggedUsersWithLimit || lowest != _loggedLowest)
        {
            _loggedUsersWithLimit = limits.Count;
            _loggedLowest = lowest;
            _logger.LockoutPolicies(limits.Count, lowest);
        }

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
}
