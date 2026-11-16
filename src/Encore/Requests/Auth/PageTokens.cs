using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Encore.Requests.Api;
using Encore.Store;

namespace Encore.Requests.Auth;

/// <summary>
/// The request page's own sign-in tokens (T114). After a password or Quick Connect sign-in Encore logs the Jellyfin
/// session out (so the page holds no Jellyfin session slot) and hands the page one of these instead:
/// <c>enc1_</c> + 32 random bytes in base64url. Only the token's SHA-256 is stored, with the user id, when it was made and
/// when it was last used. A token stops working after 30 days unused, after 90 days in all, when it is signed out, when
/// an admin revokes it, and when its Jellyfin user is deleted or disabled (seen in <see cref="RequestUsers"/>' GET /Users,
/// which is never older than 10 minutes when a token is checked). Jellyfin 12.4 shows no password-change time
/// (UserDto has only LastLoginDate and LastActivityDate, which change on every sign-in anywhere), so a password change
/// alone does not end a page token. A token is never logged.
/// </summary>
public sealed partial class PageTokens
{
    public const string Prefix = "enc1_";

    private readonly IPageTokenStore _store;
    private readonly RequestUsers _users;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public PageTokens(IPageTokenStore store, RequestUsers users, ILogger<PageTokens> logger, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _users.Refreshed += OnUsersRefreshed;
    }

    /// <summary>enc1_ and 43 base64url characters (32 bytes, no padding).</summary>
    [GeneratedRegex("^enc1_[A-Za-z0-9_-]{43}$")]
    private static partial Regex Pattern();

    /// <summary>Does this text start like a page token? (Then it is checked as one and never sent to Jellyfin.)</summary>
    public static bool HasPrefix(string token) => token.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Is this a well-formed page token?</summary>
    public static bool IsWellFormed(string token) => Pattern().IsMatch(token);

    /// <summary>A new token for the user. Expired tokens of everyone are cleared at the same time.</summary>
    public string Issue(Guid userId)
    {
        var now = _time.GetUtcNow();
        var expired = _store.DeleteExpiredPageTokens(now - RequestLimits.PageTokenIdle, now - RequestLimits.PageTokenMaxAge);
        if (expired > 0)
        {
            _logger.PageTokensRevoked("expired", expired);
        }

        var token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        _store.InsertPageToken(Hash(token), RequestUserKey.Of(userId), now);
        return token;
    }

    /// <summary>
    /// Checks a well-formed page token. Returns the user (name and admin flag from the last GET /Users), or 401
    /// invalid_token (unknown, expired, user deleted), 403 user_disabled, or 503 when the user data could not be
    /// brought up to date. A token that is rejected for good is deleted. <paramref name="known"/> is false only for an
    /// unknown token (the caller counts it as a guess).
    /// </summary>
    public async Task<(AuthOutcome<RequestUser> Outcome, bool Known)> CheckAsync(string token, CancellationToken ct = default)
    {
        var hash = Hash(token);
        var row = _store.GetPageToken(hash);
        if (row is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.TokenHash), Encoding.ASCII.GetBytes(hash)))
        {
            return (AuthOutcome<RequestUser>.Fail(401, AuthCodes.InvalidToken), false);
        }

        var now = _time.GetUtcNow();
        if (now - row.LastUsedUtc > RequestLimits.PageTokenIdle || now - row.CreatedUtc > RequestLimits.PageTokenMaxAge)
        {
            _store.DeletePageToken(hash);
            _logger.PageTokensRevoked("expired", 1);
            return (AuthOutcome<RequestUser>.Fail(401, AuthCodes.InvalidToken), true);
        }

        if (!Guid.TryParseExact(row.UserId, "N", out var userId))
        {
            _store.DeletePageToken(hash);
            return (AuthOutcome<RequestUser>.Fail(401, AuthCodes.InvalidToken), true);
        }

        if (!await _users.EnsureFreshAsync(RequestLimits.PageTokenUserMaxAge, null, ct).ConfigureAwait(false))
        {
            return (AuthOutcome<RequestUser>.Fail(503, AuthCodes.JellyfinUnavailable), true);
        }

        var user = _users.Find(userId);

        // A user made after the last GET /Users is not in it yet: ask Jellyfin again before calling the user deleted.
        if (user is null && _users.LastRefreshUtc < row.CreatedUtc)
        {
            if (!await _users.EnsureFreshAsync(RequestLimits.PageTokenUserMaxAge, row.CreatedUtc, ct).ConfigureAwait(false))
            {
                return (AuthOutcome<RequestUser>.Fail(503, AuthCodes.JellyfinUnavailable), true);
            }

            user = _users.Find(userId);
        }

        if (user is null)
        {
            var gone = _store.DeletePageTokens(row.UserId);
            if (gone > 0)
            {
                _logger.PageTokensRevoked("user_deleted", gone);
            }

            return (AuthOutcome<RequestUser>.Fail(401, AuthCodes.InvalidToken), true);
        }

        if (user.IsDisabled)
        {
            var gone = _store.DeletePageTokens(row.UserId);
            if (gone > 0)
            {
                _logger.PageTokensRevoked("user_disabled", gone);
            }

            return (AuthOutcome<RequestUser>.Fail(403, AuthCodes.Disabled), true);
        }

        if (now - row.LastUsedUtc >= RequestLimits.PageTokenTouch)
        {
            _store.TouchPageToken(hash, now);
        }

        return (AuthOutcome<RequestUser>.Success(user), true);
    }

    /// <summary>Signs one token out. True when it existed.</summary>
    public bool Revoke(string token) => _store.DeletePageToken(Hash(token));

    /// <summary>An admin's revoke: every token of one user, or of everyone when <paramref name="userId"/> is null. Logs the count only.</summary>
    public int RevokeAll(Guid? userId)
    {
        var count = _store.DeletePageTokens(userId is { } id ? RequestUserKey.Of(id) : null);
        _logger.PageTokensRevoked(userId is null ? "admin_all" : "admin_user", count);
        return count;
    }

    /// <summary>After each GET /Users: tokens of users that are gone or disabled are deleted.</summary>
    private void OnUsersRefreshed(IReadOnlyDictionary<Guid, RequestUser> users)
    {
        // An empty list is never the real answer (an admin always exists); keep every token rather than wipe them.
        if (users.Count == 0)
        {
            return;
        }

        try
        {
            var keep = users.Values.Where(u => !u.IsDisabled).Select(u => RequestUserKey.Of(u.Id)).ToHashSet(StringComparer.Ordinal);
            var gone = _store.DeletePageTokensExcept(keep);
            if (gone > 0)
            {
                _logger.PageTokensRevoked("user_deleted_or_disabled", gone);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.PageTokenStoreFailed(ex.GetType().Name);
        }
    }

    /// <summary>SHA-256 of the token as 64 uppercase hex digits.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
