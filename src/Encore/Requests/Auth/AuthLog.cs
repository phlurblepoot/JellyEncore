namespace Encore.Requests.Auth;

/// <summary>
/// Auth log events 9000-9050 (design section 13). Never pass a token, a password, the Authorization header or a
/// response body here.
/// </summary>
public static partial class AuthLog
{
    [LoggerMessage(EventId = 9000, EventName = "auth.validated", Level = LogLevel.Debug,
        Message = "Token validated for {UserName} ({UserId}), admin {IsAdmin}, cached {Cached}")]
    public static partial void Validated(this ILogger logger, string userName, Guid userId, bool isAdmin, bool cached);

    [LoggerMessage(EventId = 9010, EventName = "auth.rejected", Level = LogLevel.Information,
        Message = "Auth rejected ({Reason}) from {RemoteIp}")]
    public static partial void Rejected(this ILogger logger, string reason, string remoteIp);

    [LoggerMessage(EventId = 9020, EventName = "auth.login", Level = LogLevel.Information,
        Message = "Login {Method} for {UserName}: ok {Ok}")]
    public static partial void Login(this ILogger logger, string userName, string method, bool ok);

    [LoggerMessage(EventId = 9030, EventName = "auth.login_limited", Level = LogLevel.Warning,
        Message = "Login limited ({Scope}) for {RemoteIp}, name {NameHash}, until {UntilUtc}")]
    public static partial void LoginLimited(this ILogger logger, string scope, string remoteIp, string nameHash, DateTimeOffset untilUtc);

    [LoggerMessage(EventId = 9040, EventName = "auth.jellyfin_failed", Level = LogLevel.Warning,
        Message = "Jellyfin call {Operation} failed (status {Status}): {Error}")]
    public static partial void JellyfinFailed(this ILogger logger, string operation, int status, string error);

    [LoggerMessage(EventId = 9050, EventName = "auth.lockout_policies", Level = LogLevel.Information,
        Message = "Jellyfin lockout policies: {UsersWithLimit} users with a limit, lowest limit {LowestLimit}")]
    public static partial void LockoutPolicies(this ILogger logger, int usersWithLimit, int lowestLimit);

    // 9060 is api.error (ApiLog).

    [LoggerMessage(EventId = 9070, EventName = "auth.page_tokens_revoked", Level = LogLevel.Information,
        Message = "Request page tokens removed ({Reason}): {Count}")]
    public static partial void PageTokensRevoked(this ILogger logger, string reason, int count);

    [LoggerMessage(EventId = 9075, EventName = "auth.page_token_store_failed", Level = LogLevel.Warning,
        Message = "Request page tokens could not be cleaned up: {ExceptionType}")]
    public static partial void PageTokenStoreFailed(this ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 9080, EventName = "auth.jellyfin_session_freed", Level = LogLevel.Debug,
        Message = "Jellyfin sign-in session of the request page logged out ({Method}), ok {Ok}")]
    public static partial void JellyfinSessionFreed(this ILogger logger, string method, bool ok);

    [LoggerMessage(EventId = 9085, EventName = "auth.too_many_sessions", Level = LogLevel.Information,
        Message = "Login {Method} refused by Jellyfin: the account is at its session limit")]
    public static partial void TooManySessions(this ILogger logger, string method);
}
