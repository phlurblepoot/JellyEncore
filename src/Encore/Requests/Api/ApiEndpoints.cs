using System.Net;
using System.Text.Json;
using Encore.Config;
using Encore.Requests.Auth;
using Encore.Requests.Search;
using Encore.Requests.Songs;
using Encore.Store;

namespace Encore.Requests.Api;

/// <summary>The id format used for a Jellyfin user in the requests table: 32 lowercase hex digits (Jellyfin's own "N" form).</summary>
public static class RequestUserKey
{
    public static string Of(Guid id) => id.ToString("N");
}

/// <summary>
/// The /api/v1 routes of T84: <c>/config</c>, <c>/me</c> and the auth routes (design 3.3 and 4). Later request tasks add
/// their routes to the same group. Every client IP is the one the forwarded-headers middleware worked out
/// (<c>Connection.RemoteIpAddress</c>), never a header read here.
/// </summary>
public sealed class ApiEndpoints
{
    private readonly EncoreOptions _options;
    private readonly string _version;
    private readonly UserAuth _auth;
    private readonly RequestUsers _users;
    private readonly RequestStore _store;
    private readonly JellyfinServerInfo _serverInfo;
    private readonly SearchService _search;
    private readonly TimeProvider _time;
    private readonly SearchRateLimiter _searchRate;
    private readonly object _quickGate = new();
    private (bool? Value, DateTimeOffset At)? _quickConnect;

    public ApiEndpoints(
        EncoreOptions options,
        UserAuth auth,
        RequestUsers users,
        RequestStore store,
        JellyfinServerInfo serverInfo,
        SearchService search,
        TimeProvider time,
        string version)
    {
        _search = search;
        _options = options;
        _auth = auth;
        _users = users;
        _store = store;
        _serverInfo = serverInfo;
        _time = time;
        _searchRate = new SearchRateLimiter(time);
        _version = version;
    }

    /// <summary>The route group, so later tasks can map more routes onto the same filters and error shape.</summary>
    public RouteGroupBuilder Map(IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1");
        api.MapGet("/config", (Delegate)ConfigAsync);
        api.MapGet("/me", (Delegate)MeAsync);
        api.MapGet("/search", (Delegate)SearchAsync);
        api.MapPost("/auth/login", (Delegate)LoginAsync);
        api.MapPost("/auth/quickconnect", (Delegate)QuickConnectStartAsync);
        api.MapPost("/auth/quickconnect/poll", (Delegate)QuickConnectPollAsync);
        api.MapPost("/auth/logout", (Delegate)LogoutAsync);
        return api;
    }

    /// <summary>Checks the Bearer token of a request. Returns the user, or the error result to send back.</summary>
    public async Task<(RequestUser? User, IResult? Failure)> AuthenticateAsync(HttpContext context)
    {
        var outcome = await _auth.ValidateAsync(context.Request.Headers.Authorization.ToString(), ClientIp(context), context.RequestAborted).ConfigureAwait(false);
        return outcome.Ok ? (outcome.Value, null) : (null, ApiErrors.Result(outcome.Error!));
    }

    public static IPAddress? ClientIp(HttpContext context) => context.Connection.RemoteIpAddress;

    private async Task<IResult> ConfigAsync(HttpContext context)
    {
        var quick = await QuickConnectOnAsync(context.RequestAborted).ConfigureAwait(false);
        return Results.Json(new
        {
            version = _version,
            songRequests = _options.FetchMode != FetchMode.Off,
            albumRequests = _options.LidarrUrl is not null,
            quickConnect = quick,
            jellyfinUrl = _options.JellyfinPublicUrl?.AbsoluteUri.TrimEnd('/'),
            jellyfinServerId = _serverInfo.ServerId,
        }, ApiJson.Options);
    }

    private async Task<bool> QuickConnectOnAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_quickGate)
        {
            if (_quickConnect is { } cached && now - cached.At < RequestLimits.QuickConnectStatusTtl)
            {
                return cached.Value == true;
            }
        }

        var enabled = await _auth.QuickConnectEnabledAsync(ct).ConfigureAwait(false);
        lock (_quickGate)
        {
            _quickConnect = (enabled, now);
        }

        return enabled == true;
    }

    private async Task<IResult> MeAsync(HttpContext context)
    {
        var (user, failure) = await AuthenticateAsync(context).ConfigureAwait(false);
        if (user is null)
        {
            return failure!;
        }

        var canRequest = _users.CanRequest(user);
        return Results.Json(new
        {
            id = RequestUserKey.Of(user.Id),
            name = user.Name,
            isAdmin = user.IsAdmin,
            canRequest,
            songsLeftToday = Left(user, canRequest, "song", _options.RequestSongsPerDay),
            albumsLeftToday = Left(user, canRequest, "album", _options.RequestAlbumsPerDay),
        }, ApiJson.Options);
    }

    /// <summary>GET /search?q=&amp;kinds=song,album: any signed-in user (design 4 and 5). A bad q or kinds is 400; a failing source is a warning, never an error.</summary>
    private async Task<IResult> SearchAsync(HttpContext context)
    {
        var (user, failure) = await AuthenticateAsync(context).ConfigureAwait(false);
        if (user is null)
        {
            return failure!;
        }

        if (!SearchService.TryNormalize(context.Request.Query["q"].ToString(), out var query)
            || SearchService.ParseKinds(context.Request.Query["kinds"].ToString()) is not { } kinds)
        {
            return ApiErrors.Result(400, AuthCodes.BadRequest);
        }

        if (_searchRate.TryAcquire(RequestUserKey.Of(user.Id)) is { } wait)
        {
            return ApiErrors.Result(StatusCodes.Status429TooManyRequests, RequestCodes.SearchLimited, wait);
        }

        var response = await _search.SearchAsync(user.Id, query, kinds, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(response, ApiJson.Options);
    }

    /// <summary>Requests still allowed in the rolling 24 h: null = unlimited (admins), 0 when the person may not request.</summary>
    private int? Left(RequestUser user, bool canRequest, string kind, int perDay)
    {
        if (user.IsAdmin)
        {
            return null;
        }

        if (!canRequest)
        {
            return 0;
        }

        var used = _store.CountCreatedSince(RequestUserKey.Of(user.Id), kind, _time.GetUtcNow().AddHours(-24));
        return Math.Max(0, perDay - used);
    }

    private sealed record LoginBody(string? Username, string? Password, string? DeviceId);

    private sealed record QuickConnectBody(string? Secret, string? DeviceId);

    private async Task<IResult> LoginAsync(HttpContext context)
    {
        if (await ReadBodyAsync<LoginBody>(context).ConfigureAwait(false) is not { } body)
        {
            return ApiErrors.Result(400, AuthCodes.BadRequest);
        }

        var outcome = await _auth.LoginAsync(body.Username, body.Password, body.DeviceId, ClientIp(context), context.RequestAborted).ConfigureAwait(false);
        return outcome.Ok ? Results.Json(SignedIn(outcome.Value!), ApiJson.Options) : ApiErrors.Result(outcome.Error!);
    }

    private async Task<IResult> QuickConnectStartAsync(HttpContext context)
    {
        if (await ReadBodyAsync<QuickConnectBody>(context).ConfigureAwait(false) is not { } body)
        {
            return ApiErrors.Result(400, AuthCodes.BadRequest);
        }

        var outcome = await _auth.QuickConnectInitiateAsync(body.DeviceId, ClientIp(context), context.RequestAborted).ConfigureAwait(false);
        return outcome.Ok ? Results.Json(new { code = outcome.Value!.Code, secret = outcome.Value.Secret }, ApiJson.Options) : ApiErrors.Result(outcome.Error!);
    }

    private async Task<IResult> QuickConnectPollAsync(HttpContext context)
    {
        if (await ReadBodyAsync<QuickConnectBody>(context).ConfigureAwait(false) is not { } body)
        {
            return ApiErrors.Result(400, AuthCodes.BadRequest);
        }

        var outcome = await _auth.QuickConnectPollAsync(body.Secret, body.DeviceId, ClientIp(context), context.RequestAborted).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return ApiErrors.Result(outcome.Error!);
        }

        return outcome.Value!.Login is { } login
            ? Results.Json(new { authenticated = true, token = login.Token, user = UserJson(login.User) }, ApiJson.Options)
            : Results.Json(new { authenticated = false }, ApiJson.Options);
    }

    private async Task<IResult> LogoutAsync(HttpContext context)
    {
        var outcome = await _auth.LogoutAsync(context.Request.Headers.Authorization.ToString(), ClientIp(context), context.RequestAborted).ConfigureAwait(false);
        return outcome.Ok ? Results.NoContent() : ApiErrors.Result(outcome.Error!);
    }

    private object SignedIn(LoginResult login) => new { token = login.Token, user = UserJson(login.User) };

    private object UserJson(RequestUser user) => new
    {
        id = RequestUserKey.Of(user.Id),
        name = user.Name,
        isAdmin = user.IsAdmin,
        canRequest = _users.CanRequest(user),
    };

    /// <summary>
    /// Reads a small JSON body. Null when it is missing, too big, not sent as <c>application/json</c>, not JSON or not an
    /// object: the caller answers 400. The content type rule makes every cross-origin POST a CORS preflight, so a page
    /// on another site cannot use its visitors' browsers to try passwords (a text/plain form post needs no preflight).
    /// The body is never logged (it can hold a password).
    /// </summary>
    internal static async Task<T?> ReadBodyAsync<T>(HttpContext context) where T : class
    {
        var request = context.Request;
        if (request.ContentLength is > RequestLimits.ApiBodyMaxBytes || !IsJson(request.ContentType))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > RequestLimits.ApiBodyMaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), ApiJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Is the Content-Type <c>application/json</c> (any parameters, any case)?</summary>
    internal static bool IsJson(string? contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var media)
        && string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);
}
