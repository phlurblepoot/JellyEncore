using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encore.Jellyfin;

namespace Encore.Requests.Auth;

/// <summary>
/// Checks a person's Jellyfin token and passes sign-ins through (design 3.2, 3.3, 3.5). Invariants it keeps:
/// the token is checked with a header that holds only the token (no Client, Device, DeviceId or Version, so Jellyfin
/// does not rename the person's device); Encore's own API key is never sent here (the HttpClient has no
/// <see cref="JellyfinAuthHandler"/>); a password or token is never logged, stored or put in an error; only a
/// SHA-256 of a token is cached.
/// </summary>
public sealed partial class UserAuth
{
    private const string LoginClient = "Encore Requests";

    private readonly HttpClient _http;
    private readonly RequestUsers _users;
    private readonly LoginGuard _guard;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _version;
    private readonly TokenCache _cache;
    private readonly object _secretGate = new();
    private readonly Dictionary<string, DateTimeOffset> _secrets = [];

    /// <param name="http">A client for Jellyfin's base URL with NO Encore auth handler and no default headers.</param>
    public UserAuth(HttpClient http, RequestUsers users, LoginGuard guard, ILogger<UserAuth> logger, TimeProvider? time = null, string? version = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _version = version ?? JellyfinAuth.VersionFromAssembly();
        _cache = new TokenCache(_time);
        if (_http.BaseAddress is null)
        {
            throw new ArgumentException("HttpClient.BaseAddress must be the Jellyfin base URL (with a trailing slash)", nameof(http));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9]{16,128}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex DeviceIdPattern();


    /// <summary>
    /// Checks the Authorization header of a request: "Bearer token". Returns the user, or 401 (missing, malformed,
    /// invalid token), 403 (user disabled), 429 (this IP is blocked for token guessing) or 503 (Jellyfin down).
    /// Does not check the request-user rule: use <see cref="RequestUsers.CanRequest"/>.
    /// </summary>
    public async Task<AuthOutcome<RequestUser>> ValidateAsync(string? authorization, IPAddress? remoteIp, CancellationToken ct = default)
    {
        var ip = IpText(remoteIp);
        var block = _guard.CheckTokenBlock(remoteIp);
        if (!block.Allowed)
        {
            return AuthOutcome<RequestUser>.Fail(429, AuthCodes.LoginLimited, block.RetryAfter);
        }

        var token = ParseBearer(authorization, out var problem);
        if (token is null)
        {
            _logger.Rejected(problem!, ip);
            if (problem == AuthCodes.MalformedToken)
            {
                _guard.RecordTokenFailure(remoteIp);
            }

            return AuthOutcome<RequestUser>.Fail(401, problem!);
        }

        var key = Hash(token);
        var hit = _cache.Get(key);
        if (hit is not null)
        {
            if (hit.User is null)
            {
                _logger.Rejected(AuthCodes.InvalidToken, ip);
                _guard.RecordTokenFailure(remoteIp);
                return AuthOutcome<RequestUser>.Fail(401, AuthCodes.InvalidToken);
            }

            return Accept(hit.User, cached: true, ip);
        }

        var reply = await SendAsync(HttpMethod.Get, "Users/Me", TokenOnlyHeader(token), null, "GetCurrentUser", ct).ConfigureAwait(false);
        if (reply.Status == 200)
        {
            var user = ParseUser(reply.Body);
            if (user is null)
            {
                _logger.JellyfinFailed("GetCurrentUser", 200, "unreadable body");
                return AuthOutcome<RequestUser>.Fail(503, AuthCodes.JellyfinUnavailable);
            }

            _cache.PutUser(key, user);
            return Accept(user, cached: false, ip);
        }

        // 400 is what Jellyfin answers for an API key (not a user token); 401 and 403 are a bad or expired token.
        if (reply.Status is 400 or 401 or 403)
        {
            _cache.PutRejected(key);
            _guard.RecordTokenFailure(remoteIp);
            _logger.Rejected(AuthCodes.InvalidToken, ip);
            return AuthOutcome<RequestUser>.Fail(401, AuthCodes.InvalidToken);
        }

        return Unavailable("GetCurrentUser", reply);
    }

    /// <summary>
    /// Sign in with a name and password: POST /Users/AuthenticateByName with no token and the page's device id.
    /// The password is held for this call only. Failures count in the guard; 429 when it refuses.
    /// </summary>
    public async Task<AuthOutcome<LoginResult>> LoginAsync(string? username, string? password, string? deviceId, IPAddress? remoteIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || password is null || password.Length > 1024
            || deviceId is null || !DeviceIdPattern().IsMatch(deviceId))
        {
            return AuthOutcome<LoginResult>.Fail(400, AuthCodes.BadRequest);
        }

        var verdict = _guard.CheckLogin(remoteIp, username);
        if (!verdict.Allowed)
        {
            _logger.Rejected("login_limited_" + verdict.Scope, IpText(remoteIp));
            return AuthOutcome<LoginResult>.Fail(429, AuthCodes.LoginLimited, verdict.RetryAfter);
        }

        var body = JsonSerializer.Serialize(new { Username = username, Pw = password });
        var reply = await SendAsync(HttpMethod.Post, "Users/AuthenticateByName", LoginHeader(deviceId), body, "AuthenticateByName", ct).ConfigureAwait(false);
        return FinishLogin(reply, "AuthenticateByName", "password", username, remoteIp);
    }

    /// <summary>Is Quick Connect switched on in Jellyfin? Null when Jellyfin could not be asked.</summary>
    public async Task<bool?> QuickConnectEnabledAsync(CancellationToken ct = default)
    {
        var reply = await SendAsync(HttpMethod.Get, "QuickConnect/Enabled", null, null, "QuickConnectEnabled", ct).ConfigureAwait(false);
        if (reply.Status == 200 && reply.Body is not null)
        {
            var text = reply.Body.Trim();
            if (bool.TryParse(text, out var enabled))
            {
                return enabled;
            }
        }

        if (reply.Status != 200)
        {
            LogFailure("QuickConnectEnabled", reply);
        }

        return null;
    }

    /// <summary>Starts a Quick Connect sign-in: POST /QuickConnect/Initiate. The secret is remembered (hashed) so polls can be checked.</summary>
    public async Task<AuthOutcome<QuickConnectStart>> QuickConnectInitiateAsync(string? deviceId, IPAddress? remoteIp, CancellationToken ct = default)
    {
        if (deviceId is null || !DeviceIdPattern().IsMatch(deviceId))
        {
            return AuthOutcome<QuickConnectStart>.Fail(400, AuthCodes.BadRequest);
        }

        var verdict = _guard.CheckQuickConnectStart(remoteIp);
        if (!verdict.Allowed)
        {
            _logger.Rejected("login_limited_" + verdict.Scope, IpText(remoteIp));
            return AuthOutcome<QuickConnectStart>.Fail(429, AuthCodes.LoginLimited, verdict.RetryAfter);
        }

        var reply = await SendAsync(HttpMethod.Post, "QuickConnect/Initiate", LoginHeader(deviceId), null, "QuickConnectInitiate", ct).ConfigureAwait(false);
        if (reply.Status == 401)
        {
            return AuthOutcome<QuickConnectStart>.Fail(409, AuthCodes.QuickConnectOff);
        }

        if (reply.Status != 200)
        {
            return Unavailable<QuickConnectStart>("QuickConnectInitiate", reply);
        }

        var result = Parse<QuickConnectDto>(reply.Body);
        if (result is null || string.IsNullOrEmpty(result.Secret) || string.IsNullOrEmpty(result.Code))
        {
            _logger.JellyfinFailed("QuickConnectInitiate", 200, "unreadable body");
            return AuthOutcome<QuickConnectStart>.Fail(503, AuthCodes.JellyfinUnavailable);
        }

        RememberSecret(result.Secret);
        return AuthOutcome<QuickConnectStart>.Success(new QuickConnectStart(result.Code, result.Secret));
    }

    /// <summary>
    /// One poll: GET /QuickConnect/Connect, and once approved POST /Users/AuthenticateWithQuickConnect. Only a secret
    /// Encore itself handed out is passed on (404 unknown_secret otherwise).
    /// </summary>
    public async Task<AuthOutcome<QuickConnectPoll>> QuickConnectPollAsync(string? secret, string? deviceId, IPAddress? remoteIp, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 256 || deviceId is null || !DeviceIdPattern().IsMatch(deviceId))
        {
            return AuthOutcome<QuickConnectPoll>.Fail(400, AuthCodes.BadRequest);
        }

        var block = _guard.CheckTokenBlock(remoteIp);
        if (!block.Allowed)
        {
            return AuthOutcome<QuickConnectPoll>.Fail(429, AuthCodes.LoginLimited, block.RetryAfter);
        }

        if (!IsKnownSecret(secret))
        {
            return AuthOutcome<QuickConnectPoll>.Fail(404, AuthCodes.UnknownSecret);
        }

        var header = LoginHeader(deviceId);
        var state = await SendAsync(HttpMethod.Get, "QuickConnect/Connect?secret=" + Uri.EscapeDataString(secret), header, null, "QuickConnectConnect", ct).ConfigureAwait(false);
        if (state.Status == 404)
        {
            ForgetSecret(secret);
            return AuthOutcome<QuickConnectPoll>.Fail(404, AuthCodes.UnknownSecret);
        }

        if (state.Status != 200)
        {
            return Unavailable<QuickConnectPoll>("QuickConnectConnect", state);
        }

        var dto = Parse<QuickConnectDto>(state.Body);
        if (dto is null)
        {
            _logger.JellyfinFailed("QuickConnectConnect", 200, "unreadable body");
            return AuthOutcome<QuickConnectPoll>.Fail(503, AuthCodes.JellyfinUnavailable);
        }

        if (!dto.Authenticated)
        {
            return AuthOutcome<QuickConnectPoll>.Success(new QuickConnectPoll(false, null));
        }

        var body = JsonSerializer.Serialize(new { Secret = secret });
        var login = await SendAsync(HttpMethod.Post, "Users/AuthenticateWithQuickConnect", header, body, "AuthenticateWithQuickConnect", ct).ConfigureAwait(false);
        var finished = FinishLogin(login, "AuthenticateWithQuickConnect", "quick_connect", null, remoteIp);
        if (!finished.Ok)
        {
            return AuthOutcome<QuickConnectPoll>.Fail(finished.Error!);
        }

        ForgetSecret(secret);
        return AuthOutcome<QuickConnectPoll>.Success(new QuickConnectPoll(true, finished.Value));
    }

    /// <summary>
    /// Signs the token out at Jellyfin (POST /Sessions/Logout with the token) and drops it from the cache. The cache
    /// entry goes even when Jellyfin cannot be reached. Returns 503 only when Jellyfin could not be asked.
    /// </summary>
    public async Task<AuthOutcome<bool>> LogoutAsync(string? authorization, IPAddress? remoteIp, CancellationToken ct = default)
    {
        var token = ParseBearer(authorization, out var problem);
        if (token is null)
        {
            _logger.Rejected(problem!, IpText(remoteIp));
            return AuthOutcome<bool>.Fail(401, problem!);
        }

        _cache.Remove(Hash(token));
        var reply = await SendAsync(HttpMethod.Post, "Sessions/Logout", TokenOnlyHeader(token), null, "SessionsLogout", ct).ConfigureAwait(false);
        _cache.Remove(Hash(token));

        // 401 means Jellyfin already forgot the token, which is what logout wants.
        if (reply.Status is >= 200 and < 300 or 401 or 403)
        {
            return AuthOutcome<bool>.Success(true);
        }

        return Unavailable<bool>("SessionsLogout", reply);
    }

    private AuthOutcome<LoginResult> FinishLogin(HttpReply reply, string operation, string method, string? typedName, IPAddress? remoteIp)
    {
        if (reply.Status == 200)
        {
            var result = Parse<AuthenticationResultDto>(reply.Body);
            var user = result?.User is { Name: { Length: > 0 } name } dto && !string.IsNullOrEmpty(result.AccessToken)
                ? new RequestUser(dto.Id, name, dto.Policy?.IsAdministrator ?? false, dto.Policy?.IsDisabled ?? false)
                : null;
            if (user is null || result is null)
            {
                _logger.JellyfinFailed(operation, 200, "unreadable body");
                return AuthOutcome<LoginResult>.Fail(503, AuthCodes.JellyfinUnavailable);
            }

            _guard.RecordLoginSuccess(remoteIp, typedName ?? user.Name);
            _logger.Login(user.Name, method, true);
            return AuthOutcome<LoginResult>.Success(new LoginResult(result.AccessToken!, user));
        }

        if (reply.Status is 401 or 403)
        {
            if (typedName is not null)
            {
                _guard.RecordLoginFailure(remoteIp, typedName);

                // A name typed into the wrong field could be a password: log it only when it is a real user.
                _logger.Login(_users.IsKnownName(typedName) ? typedName : "(unknown)", method, false);
            }
            else
            {
                _logger.Login("(quick connect)", method, false);
            }

            return AuthOutcome<LoginResult>.Fail(401, AuthCodes.InvalidCredentials);
        }

        if (reply.Status == 400)
        {
            LogFailure(operation, reply);
            return AuthOutcome<LoginResult>.Fail(400, AuthCodes.BadRequest);
        }

        return Unavailable<LoginResult>(operation, reply);
    }

    private AuthOutcome<RequestUser> Accept(RequestUser user, bool cached, string ip)
    {
        if (user.IsDisabled)
        {
            _logger.Rejected(AuthCodes.Disabled, ip);
            return AuthOutcome<RequestUser>.Fail(403, AuthCodes.Disabled);
        }

        _logger.Validated(user.Name, user.Id, user.IsAdmin, cached);
        return AuthOutcome<RequestUser>.Success(user);
    }

    private AuthOutcome<RequestUser> Unavailable(string operation, HttpReply reply) => Unavailable<RequestUser>(operation, reply);

    private AuthOutcome<T> Unavailable<T>(string operation, HttpReply reply)
    {
        LogFailure(operation, reply);
        return AuthOutcome<T>.Fail(503, AuthCodes.JellyfinUnavailable);
    }

    private void LogFailure(string operation, HttpReply reply) =>
        _logger.JellyfinFailed(operation, reply.Status ?? 0, reply.Error ?? "unexpected status");

    /// <summary>Pulls the token out of "Bearer token"; null with the problem name when it is missing or malformed.</summary>
    private static string? ParseBearer(string? authorization, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(authorization))
        {
            problem = AuthCodes.MissingToken;
            return null;
        }

        const string scheme = "Bearer ";
        if (!authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            problem = AuthCodes.MalformedToken;
            return null;
        }

        var token = authorization[scheme.Length..].Trim();
        if (!TokenPattern().IsMatch(token))
        {
            problem = AuthCodes.MalformedToken;
            return null;
        }

        return token;
    }

    private static string TokenOnlyHeader(string token) => $"{JellyfinAuth.Scheme} Token=\"{token}\"";

    private string LoginHeader(string deviceId) =>
        $"{JellyfinAuth.Scheme} Client=\"{LoginClient}\", Device=\"{LoginClient}\", DeviceId=\"{deviceId}\", Version=\"{_version.Replace("\"", string.Empty)}\"";

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string IpText(IPAddress? ip) => ip is null ? "unknown" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    private static RequestUser? ParseUser(string? json)
    {
        var dto = Parse<UserDto>(json);
        if (dto is null || dto.Id == Guid.Empty || string.IsNullOrWhiteSpace(dto.Name))
        {
            return null;
        }

        return new RequestUser(dto.Id, dto.Name, dto.Policy?.IsAdministrator ?? false, dto.Policy?.IsDisabled ?? false);
    }

    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JellyfinJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void RememberSecret(string secret)
    {
        var now = _time.GetUtcNow();
        lock (_secretGate)
        {
            foreach (var old in _secrets.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            {
                _secrets.Remove(old);
            }

            if (_secrets.Count >= RequestLimits.TokenCacheMax)
            {
                _secrets.Remove(_secrets.OrderBy(p => p.Value).First().Key);
            }

            _secrets[Hash(secret)] = now + RequestLimits.QuickConnectSecretTtl;
        }
    }

    private bool IsKnownSecret(string secret)
    {
        lock (_secretGate)
        {
            return _secrets.TryGetValue(Hash(secret), out var until) && until > _time.GetUtcNow();
        }
    }

    private void ForgetSecret(string secret)
    {
        lock (_secretGate)
        {
            _secrets.Remove(Hash(secret));
        }
    }

    private sealed record HttpReply(int? Status, string? Body, string? Error);

    /// <summary>One call. A timeout or transport error gives Status null; the caller's own cancellation is rethrown.</summary>
    private async Task<HttpReply> SendAsync(HttpMethod method, string path, string? authorization, string? jsonBody, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestLimits.JellyfinTimeout);
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = status == 200 ? await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false) : null;
            return new HttpReply(status, body, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HttpReply(null, null, operation + " timed out");
        }
        catch (HttpRequestException ex)
        {
            // The type name only: the message could name the URL.
            return new HttpReply(null, null, ex.GetType().Name);
        }
    }

    private sealed record QuickConnectDto
    {
        public bool Authenticated { get; init; }

        public string? Secret { get; init; }

        public string? Code { get; init; }
    }

    private sealed record AuthenticationResultDto
    {
        public UserDto? User { get; init; }

        public string? AccessToken { get; init; }
    }
}
