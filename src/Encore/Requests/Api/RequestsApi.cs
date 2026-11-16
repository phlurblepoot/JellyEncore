using System.Net;
using Encore.Config;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Requests.Search;
using Encore.Requests.Auth;
using Encore.Requests.Page;
using Encore.Requests.Songs;
using Encore.Fetch;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Encore.Store;
using Microsoft.Net.Http.Headers;

namespace Encore.Requests.Api;

/// <summary>Log events of the HTTP API host. 9060 is new (the design table stops at 9050 for this range).</summary>
public static partial class ApiLog
{
    [LoggerMessage(EventId = 9060, EventName = "api.error", Level = LogLevel.Error,
        Message = "The API failed on {Method} {Path}: {ExceptionType}")]
    public static partial void ApiFailed(this ILogger logger, string method, string path, string exceptionType);
}

/// <summary>
/// The HTTP API host (design 3.4, 3.6, 4). Nothing here runs unless ENCORE_REQUESTS=on: Program calls
/// <see cref="AddRequestsApi"/>, <see cref="UseRequestsApi"/> and <see cref="MapRequestsApi"/> only then.
/// Middleware order: (1) trusted-connection flag, (2) forwarded headers, (3) path base, (4) path gate, (5) routing,
/// (6) CORS, (7) security headers, (8) error shape and the token-block guard, then the endpoints.
/// </summary>
public static class RequestsApi
{
    /// <summary><c>HttpContext.Items</c> key: true when the CONNECTION's own peer is a trusted proxy (recorded before forwarded headers change it).</summary>
    public const string ProxiedItem = "encore.proxied";

    private static readonly string[] AllowedMethods = ["GET", "POST", "DELETE"];

    /// <summary>The UserAuth HttpClient: a plain one (no Encore key, no default headers, no redirects), base address with a trailing slash, 10 s.</summary>
    public static HttpClient CreateUserAuthClient(EncoreOptions options)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(options.JellyfinUrl.AbsoluteUri.TrimEnd('/') + "/"),
            Timeout = RequestLimits.JellyfinTimeout,
        };
    }

    public static IServiceCollection AddRequestsApi(this IServiceCollection services, EncoreOptions options, RequestStore store, string version)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);

        services.AddSingleton(store);
        services.AddSingleton(new ProxyTrust(options.TrustedProxies));
        services.AddSingleton(sp => new RequestUsers(
            sp.GetRequiredService<IJellyfinClient>(), options.RequestUsers, options.RequestUsersAll,
            sp.GetRequiredService<ILogger<RequestUsers>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<RequestUsers>());
        services.AddSingleton(sp =>
        {
            var users = sp.GetRequiredService<RequestUsers>();
            return new LoginGuard(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<LoginGuard>>(), users.LockoutLimit);
        });
        services.AddSingleton(sp => new UserAuth(
            CreateUserAuthClient(options), sp.GetRequiredService<RequestUsers>(), sp.GetRequiredService<LoginGuard>(),
            sp.GetRequiredService<ILogger<UserAuth>>(), sp.GetRequiredService<TimeProvider>(), JellyfinAuth.VersionFromAssembly()));
        services.AddSingleton(sp => new JellyfinServerInfo(
            sp.GetRequiredService<IJellyfinClient>(), sp.GetRequiredService<ILogger<JellyfinServerInfo>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<JellyfinServerInfo>());
        if (!services.Any(d => d.ServiceType == typeof(IDeezerClient)))
        {
            // Fetch mode off registers no Discovery clients, but search still needs Deezer.
            services.AddDiscoveryClients();
        }

        services.AddSingleton(sp => new SearchService(
            sp.GetRequiredService<IJellyfinClient>(), sp.GetRequiredService<IDeezerClient>(), sp.GetService<IOwnedIndex>(),
            sp.GetRequiredService<RequestStore>(), sp.GetRequiredService<ILogger<SearchService>>()));
        services.AddSingleton(sp => new ApiEndpoints(
            options, sp.GetRequiredService<UserAuth>(), sp.GetRequiredService<RequestUsers>(), sp.GetRequiredService<RequestStore>(),
            sp.GetRequiredService<JellyfinServerInfo>(), sp.GetRequiredService<SearchService>(), sp.GetRequiredService<TimeProvider>(), version));

        // Song requests (T87). The notifier is a no-op until T88 registers the Jellyfin message one.
        services.TryAddSingleton<IRequestNotifier>(NullRequestNotifier.Instance);
        services.AddSingleton(sp => new RequestService(
            sp.GetRequiredService<RequestStore>(), sp.GetRequiredService<IFetchRequests>(), sp.GetService<IOwnedIndex>(),
            sp.GetRequiredService<IDeezerClient>(), sp.GetService<FetchStore>(), SongRequestSettings.From(options),
            sp.GetRequiredService<ILogger<RequestService>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RequestWorker(
            sp.GetRequiredService<RequestService>(), sp.GetRequiredService<IRequestNotifier>(), sp.GetRequiredService<ILogger<RequestWorker>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<RequestWorker>());
        services.AddSingleton<RequestEndpoints>();
        return services;
    }

    public static WebApplication UseRequestsApi(this WebApplication app, EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        var trust = app.Services.GetRequiredService<ProxyTrust>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Encore.Requests.Api");
        var guard = app.Services.GetRequiredService<LoginGuard>();
        var origins = new HashSet<string>(options.RequestOrigins, StringComparer.Ordinal);
        var frameAncestors = options.RequestOrigins.Count > 0 ? string.Join(' ', options.RequestOrigins) : "'none'";

        // (1) Is the connection itself from a trusted proxy? Recorded before the forwarded headers rewrite the peer.
        app.Use((context, next) =>
        {
            context.Items[ProxiedItem] = trust.IsTrusted(context.Connection.RemoteIpAddress);
            return next(context);
        });

        // (2) X-Forwarded-For/-Proto/-Host from the configured proxies only. With no proxies configured the middleware
        // is not added at all: with both known lists empty it would trust every sender.
        if (trust.Any)
        {
            app.UseForwardedHeaders(trust.ForwardedOptions());
        }

        // (3) The path base. UsePathBase strips the prefix only when it is there, so a proxy that keeps or strips it both work.
        if (options.PathBase.Length > 0)
        {
            app.UsePathBase(options.PathBase);
        }

        // (4) Path gate: a proxied request reaches only /request* and /api/v1/*; with a path base set, a direct request
        // to those paths must carry the prefix (the page and the script build every URL from the base).
        app.Use((context, next) =>
        {
            var path = context.Request.Path;
            var ours = IsPage(path) || IsApi(path);
            var proxied = context.Items[ProxiedItem] is true;
            if ((proxied && !ours) || (ours && !proxied && options.PathBase.Length > 0 && !context.Request.PathBase.HasValue))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });

        app.UseRouting();

        // (6) CORS: only for the configured origins, only on /api/v1.
        app.Use(async (context, next) =>
        {
            if (origins.Count == 0 || !IsApi(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.Headers.Append(HeaderNames.Vary, "Origin");
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length == 0 || !origins.Contains(origin))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var headers = context.Response.Headers;
            headers.AccessControlAllowOrigin = origin;
            headers.AccessControlExposeHeaders = "Retry-After";
            var requestedMethod = context.Request.Headers.AccessControlRequestMethod.ToString();
            if (HttpMethods.IsOptions(context.Request.Method) && requestedMethod.Length > 0)
            {
                // Preflight: answered here, never reaches the routes.
                if (AllowedMethods.Contains(requestedMethod, StringComparer.OrdinalIgnoreCase))
                {
                    headers.AccessControlAllowMethods = string.Join(", ", AllowedMethods);
                    headers.AccessControlAllowHeaders = "Authorization, Content-Type";
                    headers.AccessControlMaxAge = "600";
                    if (string.Equals(context.Request.Headers["Access-Control-Request-Private-Network"].ToString(), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        headers["Access-Control-Allow-Private-Network"] = "true";
                    }
                }
                else
                {
                    headers.Remove(HeaderNames.AccessControlAllowOrigin);
                }

                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        // (7) Security headers on the page and the API.
        app.Use((context, next) =>
        {
            var path = context.Request.Path;
            var api = IsApi(path);
            if (api || IsPage(path))
            {
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    headers.XContentTypeOptions = "nosniff";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers.ContentSecurityPolicy =
                        $"default-src 'self'; img-src 'self' https: data:; connect-src 'self'; frame-ancestors {frameAncestors}; base-uri 'none'; form-action 'self'";
                    if (context.Request.IsHttps)
                    {
                        headers.StrictTransportSecurity = "max-age=31536000";
                    }

                    if (api)
                    {
                        headers.CacheControl = "no-store";
                    }

                    return Task.CompletedTask;
                });
            }

            return next(context);
        });

        // (8) /api/v1: the token-guess block for every route (anonymous ones too), then the JSON error shape.
        app.Use(async (context, next) =>
        {
            if (!IsApi(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var verdict = guard.CheckTokenBlock(context.Connection.RemoteIpAddress);
            if (!verdict.Allowed)
            {
                await ApiErrors.WriteAsync(context, StatusCodes.Status429TooManyRequests, AuthCodes.LoginLimited, verdict.RetryAfter).ConfigureAwait(false);
                return;
            }

            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                logger.ApiFailed(context.Request.Method, context.Request.Path, ex.GetType().Name);
                await ApiErrors.WriteAsync(context, StatusCodes.Status500InternalServerError, ApiErrors.Internal).ConfigureAwait(false);
                return;
            }

            // An empty 404, 405 or 400 from the framework (no such route) gets the same JSON body as every other error.
            var response = context.Response;
            if (!response.HasStarted && response.StatusCode >= 400 && response.ContentLength is null && response.ContentType is null)
            {
                var code = response.StatusCode switch
                {
                    StatusCodes.Status404NotFound => ApiErrors.NotFound,
                    StatusCodes.Status405MethodNotAllowed => ApiErrors.MethodNotAllowed,
                    >= 500 => ApiErrors.Internal,
                    _ => AuthCodes.BadRequest,
                };
                await ApiErrors.WriteAsync(context, response.StatusCode, code).ConfigureAwait(false);
            }
        });

        return app;
    }

    public static WebApplication MapRequestsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.Services.GetRequiredService<ApiEndpoints>().Map(app);
        app.Services.GetRequiredService<RequestEndpoints>().Map(api);
        app.MapRequestPage();
        return app;
    }

    /// <summary>Is this path /api/v1 or below (after the path base was stripped)?</summary>
    internal static bool IsApi(PathString path) => path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is this path /request or below?</summary>
    internal static bool IsPage(PathString path) => path.StartsWithSegments("/request", StringComparison.OrdinalIgnoreCase);
}
