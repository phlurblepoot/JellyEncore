namespace Encore.Requests.Auth;

/// <summary>A Jellyfin user as validated from a token. Never holds the token.</summary>
public sealed record RequestUser(Guid Id, string Name, bool IsAdmin, bool IsDisabled);

/// <summary>An auth failure: the HTTP status, the error code for the JSON body, and Retry-After for a 429.</summary>
public sealed record AuthError(int Status, string Code, TimeSpan? RetryAfter = null);

/// <summary>A value or an <see cref="AuthError"/>.</summary>
public sealed record AuthOutcome<T>
{
    private AuthOutcome(T? value, AuthError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public AuthError? Error { get; }

    public bool Ok => Error is null;

    public static AuthOutcome<T> Success(T value) => new(value, null);

    public static AuthOutcome<T> Fail(AuthError error) => new(default, error);

    public static AuthOutcome<T> Fail(int status, string code, TimeSpan? retryAfter = null) => new(default, new AuthError(status, code, retryAfter));
}

/// <summary>The token and the user a login returns. The token is passed on to the page and not kept.</summary>
public sealed record LoginResult(string Token, RequestUser User);

/// <summary>Quick Connect start: the code the person approves in Jellyfin and the secret the page polls with.</summary>
public sealed record QuickConnectStart(string Code, string Secret);

/// <summary>One Quick Connect poll: not approved yet (Login null) or approved and signed in.</summary>
public sealed record QuickConnectPoll(bool Authenticated, LoginResult? Login);

/// <summary>Error codes of the auth layer (the JSON error field).</summary>
public static class AuthCodes
{
    public const string MissingToken = "missing_token";
    public const string MalformedToken = "malformed_token";
    public const string InvalidToken = "invalid_token";
    public const string Disabled = "user_disabled";
    public const string NotAllowed = "not_allowed";
    public const string JellyfinUnavailable = "jellyfin_unavailable";
    public const string LoginLimited = "login_limited";
    public const string InvalidCredentials = "invalid_credentials";
    public const string BadRequest = "bad_request";
    public const string UnknownSecret = "unknown_secret";
    public const string QuickConnectOff = "quickconnect_disabled";
}
