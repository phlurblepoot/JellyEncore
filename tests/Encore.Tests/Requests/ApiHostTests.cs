using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Encore.Jellyfin;
using Encore.Requests;
using Encore.Requests.Api;
using Encore.Requests.Auth;
using Encore.Store;
using Encore.Tests.Watch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using IPNetwork = System.Net.IPNetwork;

namespace Encore.Tests.Requests;

/// <summary>Captures every log line of the host (formatted text plus structured fields), whatever the category.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    /// <summary>Every captured line as one string: message, event name and field values.</summary>
    public string Everything() => string.Join("\n", Entries.Select(e =>
        e.Message + " | " + e.EventId.Name + " | " + string.Join(", ", e.Fields.Select(f => f.Key + "=" + f.Value))));

    private sealed class Capture(CapturedLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            owner.Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), fields));
        }
    }
}

/// <summary>Test seam: a request header sets the connection's peer address, since TestServer has none.</summary>
internal sealed class PeerFilter : IStartupFilter
{
    public const string Header = "X-Test-Peer";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nxt) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var peer))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                context.Request.Headers.Remove(Header);
            }

            return nxt(context);
        });
        next(app);
    };
}

/// <summary>The real host with ENCORE_REQUESTS set, a fake Jellyfin behind UserAuth, and the environment put back afterwards.</summary>
internal sealed class ApiRig : IDisposable
{
    public const string NateToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string KimToken = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string LeeToken = "cccccccccccccccccccccccccccccccc";
    public const string Password = "good-Password-4-tests";
    public const string DeviceId = "device0123456789ab";
    public const string QuickSecret = "qcsecret0123456789";
    public static readonly Guid Nate = Guid.Parse("aaaa0000aaaa0000aaaa0000aaaa0000");
    public static readonly Guid Kim = Guid.Parse("bbbb0000bbbb0000bbbb0000bbbb0000");
    public static readonly Guid Lee = Guid.Parse("cccc0000cccc0000cccc0000cccc0000");

    private static readonly string[] EnvNames =
    [
        "ENCORE_REQUESTS", "ENCORE_REQUEST_USERS", "ENCORE_REQUEST_ORIGINS", "ENCORE_PATH_BASE", "ENCORE_TRUSTED_PROXIES",
        "ENCORE_PUBLIC_URL", "ENCORE_JELLYFIN_PUBLIC_URL", "ENCORE_REQUEST_SONGS_PER_DAY", "ENCORE_DATA_DIR", "LOG_LEVEL",
        "ENCORE_LIDARR_URL", "ENCORE_LIDARR_API_KEY",
    ];

    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "encore-api-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _root = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ApiRig(
        Dictionary<string, string?>? env = null,
        bool on = true,
        Encore.Config.FetchMode songMode = Encore.Config.FetchMode.Live,
        Action<IServiceCollection>? extraServices = null)
    {
        foreach (var name in EnvNames)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        Environment.SetEnvironmentVariable("ENCORE_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("LOG_LEVEL", "Debug");
        if (on)
        {
            Environment.SetEnvironmentVariable("ENCORE_REQUESTS", "on");
            Environment.SetEnvironmentVariable("ENCORE_REQUEST_USERS", "kim");
        }

        foreach (var (name, value) in env ?? [])
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Fake = new UsersClient
        {
            Users =
            [
                new UserDto { Id = Nate, Name = "nate", Policy = new UserPolicy { IsAdministrator = true } },
                new UserDto { Id = Kim, Name = "kim", Policy = new UserPolicy() },
                new UserDto { Id = Lee, Name = "lee", Policy = new UserPolicy() },
            ],
        };
        Jellyfin.On("GET /Users/Me", request => request.Headers.GetValues("Authorization").Single() switch
        {
            var h when h.Contains(NateToken) => AuthHandler.Reply(HttpStatusCode.OK, MeJson(Nate, "nate", true)),
            var h when h.Contains(KimToken) => AuthHandler.Reply(HttpStatusCode.OK, MeJson(Kim, "kim", false)),
            var h when h.Contains(LeeToken) => AuthHandler.Reply(HttpStatusCode.OK, MeJson(Lee, "lee", false)),
            _ => AuthHandler.Reply(HttpStatusCode.Unauthorized),
        });
        Jellyfin.On("POST /Users/AuthenticateByName", request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains(Password) && body.Contains("kim")
                ? AuthHandler.Reply(HttpStatusCode.OK, LoginJson(Kim, "kim", KimToken))
                : AuthHandler.Reply(HttpStatusCode.Unauthorized);
        });
        Jellyfin.On("GET /QuickConnect/Enabled", HttpStatusCode.OK, "true");
        Jellyfin.On("POST /QuickConnect/Initiate", HttpStatusCode.OK, "{\"Secret\":\"" + QuickSecret + "\",\"Code\":\"123456\",\"Authenticated\":false}");
        Jellyfin.On("GET /QuickConnect/Connect?secret=" + QuickSecret, HttpStatusCode.OK, "{\"Secret\":\"" + QuickSecret + "\",\"Code\":\"123456\",\"Authenticated\":true}");
        Jellyfin.On("POST /Users/AuthenticateWithQuickConnect", HttpStatusCode.OK, LoginJson(Kim, "kim", KimToken));
        Jellyfin.On("POST /Sessions/Logout", HttpStatusCode.NoContent);

        var logFactory = new LoggerFactory([Logs]);
        Users = new RequestUsers(Fake, ["kim"], false, logFactory.CreateLogger<RequestUsers>());
        Guard = new LoginGuard(TimeProvider.System, logFactory.CreateLogger<LoginGuard>(), Users.LockoutLimit);
        ServerInfo = new JellyfinServerInfo(Fake, logFactory.CreateLogger<JellyfinServerInfo>());

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddTransient<IStartupFilter, PeerFilter>();
            services.AddSingleton<ILoggerProvider>(Logs);
            if (!on)
            {
                return;
            }

            services.RemoveAll<RequestUsers>();
            services.RemoveAll<LoginGuard>();
            services.RemoveAll<UserAuth>();
            services.RemoveAll<JellyfinServerInfo>();
            services.AddSingleton(Users);
            services.AddSingleton(Guard);
            services.AddSingleton(sp => new UserAuth(
                new HttpClient(Jellyfin) { BaseAddress = new Uri("http://jellyfin.test:8096/") },
                Users, Guard, sp.GetRequiredService<PageTokens>(), logFactory.CreateLogger<UserAuth>(), TimeProvider.System, "0.3.0-test"));
            services.AddSingleton(ServerInfo);
            services.RemoveAll<Encore.Requests.Search.SearchService>();
            services.AddSingleton(sp => new Encore.Requests.Search.SearchService(
                Fake, Deezer, Owned, sp.GetRequiredService<RequestStore>(), sp.GetRequiredService<ILogger<Encore.Requests.Search.SearchService>>(), SearchTimeouts));
            // Song requests over the fakes; the worker runs only its start pass (tests drive the lifecycle through the service).
            services.RemoveAll<Encore.Requests.Songs.RequestService>();
            services.RemoveAll<Encore.Requests.Songs.RequestWorker>();
            services.AddSingleton(sp => new Encore.Requests.Songs.RequestService(
                sp.GetRequiredService<RequestStore>(), Fetch, Owned, Deezer, null,
                new Encore.Requests.Songs.SongRequestSettings(songMode, sp.GetRequiredService<Encore.Config.EncoreOptions>().RequestSongsPerDay),
                sp.GetRequiredService<ILogger<Encore.Requests.Songs.RequestService>>(), TimeProvider.System));
            services.AddSingleton(sp => new Encore.Requests.Songs.RequestWorker(
                sp.GetRequiredService<Encore.Requests.Songs.RequestService>(), Encore.Requests.Songs.NullRequestNotifier.Instance,
                sp.GetRequiredService<ILogger<Encore.Requests.Songs.RequestWorker>>(), TimeProvider.System, Timeout.InfiniteTimeSpan));
            extraServices?.Invoke(services);
        }));
        Client = _factory.CreateClient();
        NoRedirectClient = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (on)
        {
            Users.RefreshAsync().GetAwaiter().GetResult();
            ServerInfo.RefreshAsync().GetAwaiter().GetResult();
        }
    }

    public AuthHandler Jellyfin { get; } = new();

    /// <summary>The Deezer, owned index and search timeouts behind /api/v1/search (set before the first search).</summary>
    public Encore.Tests.Discovery.FakeDeezer Deezer { get; } = new();

    public Encore.Tests.Picks.TestOwned Owned { get; } = new();

    /// <summary>The fetch side behind song requests (accepts every request; readiness is set by the test).</summary>
    public Encore.Tests.Picks.FakeFetchRequests Fetch { get; } = new();

    public Encore.Requests.Search.SearchOptions SearchTimeouts { get; set; } = new();

    public UsersClient Fake { get; }

    public CapturedLogs Logs { get; } = new();

    public RequestUsers Users { get; }

    public LoginGuard Guard { get; }

    /// <summary>The host's UserAuth (built in the container, over the real request store's page tokens).</summary>
    public UserAuth Auth => Services.GetRequiredService<UserAuth>();

    public PageTokens PageTokens => Services.GetRequiredService<PageTokens>();

    public JellyfinServerInfo ServerInfo { get; }

    public HttpClient Client { get; }

    public HttpClient NoRedirectClient { get; }

    public IServiceProvider Services => _factory.Services;

    public static string MeJson(Guid id, string name, bool admin) =>
        "{\"Id\":\"" + id.ToString("N") + "\",\"Name\":\"" + name + "\",\"Policy\":{\"IsAdministrator\":" + (admin ? "true" : "false") + ",\"IsDisabled\":false}}";

    public static string LoginJson(Guid id, string name, string token) =>
        "{\"User\":" + MeJson(id, name, false) + ",\"AccessToken\":\"" + token + "\",\"ServerId\":\"s\"}";

    /// <summary>One request. <paramref name="peer"/> is the connection's address; the headers are the ones a proxy would add.</summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? peer = null, string? xff = null, string? proto = null,
        string? bearer = null, string? origin = null, string? json = null, Action<HttpRequestMessage>? tweak = null, bool follow = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (peer is not null)
        {
            request.Headers.Add(PeerFilter.Header, peer);
        }

        if (xff is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", xff);
        }

        if (proto is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", proto);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse("Bearer " + bearer);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        tweak?.Invoke(request);
        return (follow ? Client : NoRedirectClient).SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? peer = null, string? xff = null, string? proto = null, string? bearer = null, string? origin = null, bool follow = true) =>
        SendAsync(HttpMethod.Get, path, peer, xff, proto, bearer, origin, follow: follow);

    public Task<HttpResponseMessage> PostAsync(string path, object body, string? peer = null, string? xff = null, string? bearer = null) =>
        SendAsync(HttpMethod.Post, path, peer, xff, null, bearer, null, JsonSerializer.Serialize(body));

    public Task<HttpResponseMessage> DeleteAsync(string path, string? bearer = null) =>
        SendAsync(HttpMethod.Delete, path, bearer: bearer);

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>The client IP the host worked out for a request: read from the "Auth rejected ... from {ip}" line of GET /api/v1/me without a token.</summary>
    public async Task<string?> SeenIpAsync(string? peer, string? xff, string? proto = null, string path = "/api/v1/me")
    {
        var before = Logs.Entries.Count(e => e.EventId.Name == "auth.rejected");
        using var response = await GetAsync(path, peer, xff, proto);
        var entries = Logs.Entries.Where(e => e.EventId.Name == "auth.rejected").ToList();
        return entries.Count > before ? entries[^1].Fields["RemoteIp"]?.ToString() : null;
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _root.Dispose();
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>v03 section 15, T84: the HTTP API host (CORS, path base, forwarded headers, security headers, error shape, /config, /me, auth routes).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ApiHostTests
{
    private const string Origins = "http://192.168.86.50:8096,https://media.nateplex.com";
    private const string Proxies = "172.21.0.0/16,103.21.244.0/22";

    private static Dictionary<string, string?> WithOrigins() => new() { ["ENCORE_REQUEST_ORIGINS"] = Origins };

    private static Dictionary<string, string?> WithProxies() => new() { ["ENCORE_TRUSTED_PROXIES"] = Proxies };

    // ---- CORS ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AH01_only_the_listed_origins_get_cors_headers_exact_match_with_port()
    {
        using var rig = new ApiRig(WithOrigins());

        using var lan = await rig.GetAsync("/api/v1/config", origin: "http://192.168.86.50:8096");
        Assert.Equal("http://192.168.86.50:8096", lan.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Origin", lan.Headers.GetValues("Vary"));
        Assert.False(lan.Headers.Contains("Access-Control-Allow-Credentials"));

        using var web = await rig.GetAsync("/api/v1/config", origin: "https://media.nateplex.com");
        Assert.Equal("https://media.nateplex.com", web.Headers.GetValues("Access-Control-Allow-Origin").Single());

        foreach (var other in new[] { "http://192.168.86.50:8097", "http://192.168.86.50", "https://192.168.86.50:8096", "http://media.nateplex.com", "https://evil.example.com", "null" })
        {
            using var denied = await rig.GetAsync("/api/v1/config", origin: other);
            Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"), other);
        }
    }

    [Fact]
    public async Task AH02_a_preflight_is_answered_with_the_allowed_methods_headers_and_private_network()
    {
        using var rig = new ApiRig(WithOrigins());

        using var response = await rig.SendAsync(HttpMethod.Options, "/api/v1/auth/login", origin: "https://media.nateplex.com", tweak: r =>
        {
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization, content-type");
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Private-Network", "true");
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://media.nateplex.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET, POST, DELETE", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("Authorization, Content-Type", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("600", response.Headers.GetValues("Access-Control-Max-Age").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task AH03_a_preflight_without_the_private_network_ask_gets_no_such_answer_and_an_unlisted_origin_nothing()
    {
        using var rig = new ApiRig(WithOrigins());

        using var plain = await rig.SendAsync(HttpMethod.Options, "/api/v1/me", origin: "http://192.168.86.50:8096", tweak: r =>
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET"));
        Assert.Equal(HttpStatusCode.NoContent, plain.StatusCode);
        Assert.False(plain.Headers.Contains("Access-Control-Allow-Private-Network"));

        using var denied = await rig.SendAsync(HttpMethod.Options, "/api/v1/me", origin: "https://evil.example.com", tweak: r =>
        {
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Private-Network", "true");
        });
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Private-Network"));
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Methods"));

        using var badMethod = await rig.SendAsync(HttpMethod.Options, "/api/v1/me", origin: "http://192.168.86.50:8096", tweak: r =>
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "PUT"));
        Assert.False(badMethod.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.False(badMethod.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AH04_no_origins_configured_means_no_cors_at_all()
    {
        using var rig = new ApiRig();

        using var response = await rig.GetAsync("/api/v1/config", origin: "http://192.168.86.50:8096");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));

        using var preflight = await rig.SendAsync(HttpMethod.Options, "/api/v1/config", origin: "http://192.168.86.50:8096", tweak: r =>
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.NotEqual(HttpStatusCode.NoContent, preflight.StatusCode);
    }

    // ---- error shape --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AH05_every_error_has_the_error_and_message_shape()
    {
        using var rig = new ApiRig();

        using var missing = await rig.GetAsync("/api/v1/nothing-here");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var notFound = await ApiRig.JsonOf(missing);
        Assert.Equal("not_found", notFound.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(notFound.GetProperty("message").GetString()));

        using var wrongMethod = await rig.SendAsync(HttpMethod.Delete, "/api/v1/config");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("method_not_allowed", (await ApiRig.JsonOf(wrongMethod)).GetProperty("error").GetString());

        using var badBody = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/login", json: "not json");
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        Assert.Equal("bad_request", (await ApiRig.JsonOf(badBody)).GetProperty("error").GetString());

        using var emptyBody = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/login");
        Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);

        using var huge = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/login", json: "{\"username\":\"" + new string('a', 40_000) + "\"}");
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);

        using var noToken = await rig.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        var unauthorized = await ApiRig.JsonOf(noToken);
        Assert.Equal("missing_token", unauthorized.GetProperty("error").GetString());
        Assert.Equal("application/json", noToken.Content.Headers.ContentType!.MediaType);
    }

    // ---- security headers -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AH06_api_answers_carry_the_security_headers_and_no_store()
    {
        using var rig = new ApiRig(WithOrigins());

        using var response = await rig.GetAsync("/api/v1/config");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal(
            "default-src 'self'; img-src 'self' https: data:; connect-src 'self'; frame-ancestors http://192.168.86.50:8096 https://media.nateplex.com; base-uri 'none'; form-action 'self'",
            csp);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));

        using var error = await rig.GetAsync("/api/v1/me");
        Assert.Equal("no-store", error.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("nosniff", error.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task AH07_the_csp_frame_ancestors_is_none_without_origins()
    {
        using var rig = new ApiRig();

        using var response = await rig.GetAsync("/api/v1/config");
        Assert.Contains("frame-ancestors 'none';", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task AH08_hsts_only_on_https_seen_through_a_trusted_proxy()
    {
        using var rig = new ApiRig(WithProxies());

        using var https = await rig.GetAsync("/api/v1/config", peer: "172.21.0.5", proto: "https");
        Assert.Equal("max-age=31536000", https.Headers.GetValues("Strict-Transport-Security").Single());

        using var http = await rig.GetAsync("/api/v1/config", peer: "172.21.0.5", proto: "http");
        Assert.False(http.Headers.Contains("Strict-Transport-Security"));

        using var direct = await rig.GetAsync("/api/v1/config");
        Assert.False(direct.Headers.Contains("Strict-Transport-Security"));

        // The header is believed only from a trusted proxy.
        using var spoofed = await rig.GetAsync("/api/v1/config", peer: "198.51.100.9", proto: "https");
        Assert.False(spoofed.Headers.Contains("Strict-Transport-Security"));
    }

    // ---- /config, /me ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AH09_config_is_anonymous_and_carries_the_server_id()
    {
        using var rig = new ApiRig(new Dictionary<string, string?> { ["ENCORE_JELLYFIN_PUBLIC_URL"] = "https://media.nateplex.com/" });

        using var response = await rig.GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await ApiRig.JsonOf(response);
        Assert.False(string.IsNullOrEmpty(config.GetProperty("version").GetString()));
        Assert.False(config.GetProperty("songRequests").GetBoolean());
        Assert.False(config.GetProperty("albumRequests").GetBoolean());
        Assert.True(config.GetProperty("quickConnect").GetBoolean());
        Assert.Equal("https://media.nateplex.com", config.GetProperty("jellyfinUrl").GetString());
        Assert.Equal("serverid1234567890", config.GetProperty("jellyfinServerId").GetString());
        Assert.Equal("Fake", config.GetProperty("jellyfinServerName").GetString());
        Assert.Equal(1, rig.Fake.PublicInfoCalls);
    }

    [Fact]
    public async Task AH10_config_has_null_ids_until_jellyfin_answers_and_quick_connect_off_when_unknown()
    {
        using var rig = new ApiRig();
        rig.Fake.PublicInfoDown = true;
        var fresh = new JellyfinServerInfo(rig.Fake, new LoggerFactory([rig.Logs]).CreateLogger<JellyfinServerInfo>());
        Assert.False(await fresh.RefreshAsync());
        Assert.Null(fresh.ServerId);
        Assert.Null(fresh.ServerName);
        rig.Fake.PublicInfoDown = false;
        Assert.True(await fresh.RefreshAsync());
        Assert.Equal("serverid1234567890", fresh.ServerId);
        Assert.Equal("Fake", fresh.ServerName);

        rig.Jellyfin.On("GET /QuickConnect/Enabled", HttpStatusCode.ServiceUnavailable);
        var config = await ApiRig.JsonOf(await rig.GetAsync("/api/v1/config"));
        Assert.False(config.GetProperty("quickConnect").GetBoolean());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("jellyfinUrl").ValueKind);
    }

    [Fact]
    public async Task AH11_me_needs_a_token_and_shows_the_daily_allowance()
    {
        using var rig = new ApiRig();
        var store = rig.Services.GetRequiredService<RequestStore>();
        var now = DateTimeOffset.UtcNow;
        store.Insert(new NewRequest("a1", "song", ApiRig.Kim.ToString("N"), "kim", "Artist", now.AddHours(-1), "T1"));
        store.Insert(new NewRequest("a2", "song", ApiRig.Kim.ToString("N"), "kim", "Artist", now.AddHours(-23), "T2"));
        store.Insert(new NewRequest("a3", "song", ApiRig.Kim.ToString("N"), "kim", "Artist", now.AddHours(-30), "T3"));
        store.Insert(new NewRequest("a4", "album", ApiRig.Kim.ToString("N"), "kim", "Artist", now.AddHours(-2), null, ForeignAlbumId: "mb1"));

        var kim = await ApiRig.JsonOf(await rig.GetAsync("/api/v1/me", bearer: ApiRig.KimToken));
        Assert.Equal(ApiRig.Kim.ToString("N"), kim.GetProperty("id").GetString());
        Assert.Equal("kim", kim.GetProperty("name").GetString());
        Assert.False(kim.GetProperty("isAdmin").GetBoolean());
        Assert.True(kim.GetProperty("canRequest").GetBoolean());
        Assert.Equal(23, kim.GetProperty("songsLeftToday").GetInt32());
        Assert.Equal(1, kim.GetProperty("albumsLeftToday").GetInt32());

        var nate = await ApiRig.JsonOf(await rig.GetAsync("/api/v1/me", bearer: ApiRig.NateToken));
        Assert.True(nate.GetProperty("isAdmin").GetBoolean());
        Assert.True(nate.GetProperty("canRequest").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nate.GetProperty("songsLeftToday").ValueKind);
        Assert.Equal(JsonValueKind.Null, nate.GetProperty("albumsLeftToday").ValueKind);

        var lee = await ApiRig.JsonOf(await rig.GetAsync("/api/v1/me", bearer: ApiRig.LeeToken));
        Assert.False(lee.GetProperty("canRequest").GetBoolean());
        Assert.Equal(0, lee.GetProperty("songsLeftToday").GetInt32());

        using var bad = await rig.GetAsync("/api/v1/me", bearer: "dddddddddddddddddddddddddddddddd");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("invalid_token", (await ApiRig.JsonOf(bad)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AH12_every_route_but_config_and_the_auth_routes_answers_401_without_a_token()
    {
        using var rig = new ApiRig();

        using var me = await rig.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        using var logout = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);

        using var config = await rig.GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
    }

    // ---- auth routes -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AH13_login_quick_connect_and_logout_are_passed_to_jellyfin()
    {
        using var rig = new ApiRig();

        using var login = await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = ApiRig.Password, deviceId = ApiRig.DeviceId });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var signedIn = await ApiRig.JsonOf(login);
        Assert.StartsWith("enc1_", signedIn.GetProperty("token").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, rig.Jellyfin.CountOf("POST /Sessions/Logout"));
        Assert.Equal("kim", signedIn.GetProperty("user").GetProperty("name").GetString());
        Assert.True(signedIn.GetProperty("user").GetProperty("canRequest").GetBoolean());

        using var wrong = await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = "nope", deviceId = ApiRig.DeviceId });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_credentials", (await ApiRig.JsonOf(wrong)).GetProperty("error").GetString());

        using var start = await rig.PostAsync("/api/v1/auth/quickconnect", new { deviceId = ApiRig.DeviceId });
        var started = await ApiRig.JsonOf(start);
        Assert.Equal("123456", started.GetProperty("code").GetString());
        Assert.Equal(ApiRig.QuickSecret, started.GetProperty("secret").GetString());

        using var poll = await rig.PostAsync("/api/v1/auth/quickconnect/poll", new { secret = ApiRig.QuickSecret, deviceId = ApiRig.DeviceId });
        var polled = await ApiRig.JsonOf(poll);
        Assert.True(polled.GetProperty("authenticated").GetBoolean());
        Assert.StartsWith("enc1_", polled.GetProperty("token").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, rig.Jellyfin.CountOf("POST /Sessions/Logout"));

        using var unknown = await rig.PostAsync("/api/v1/auth/quickconnect/poll", new { secret = "neverissued0123456", deviceId = ApiRig.DeviceId });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("unknown_secret", (await ApiRig.JsonOf(unknown)).GetProperty("error").GetString());

        // The page token: deleted by Encore, no Jellyfin call. A Jellyfin token (the web script's) is still signed out at Jellyfin.
        var pageToken = polled.GetProperty("token").GetString()!;
        using var logout = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", bearer: pageToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(2, rig.Jellyfin.CountOf("POST /Sessions/Logout"));
        using var gone = await rig.GetAsync("/api/v1/me", bearer: pageToken);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        using var jellyfinLogout = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.NoContent, jellyfinLogout.StatusCode);
        Assert.Equal(3, rig.Jellyfin.CountOf("POST /Sessions/Logout"));
    }

    [Fact]
    public async Task AH13b_a_body_that_is_not_sent_as_json_is_a_400_and_never_reaches_jellyfin()
    {
        // A text/plain POST needs no CORS preflight, so another site could make its visitors' browsers try passwords.
        using var rig = new ApiRig();
        var body = "{\"username\":\"kim\",\"password\":\"" + ApiRig.Password + "\",\"deviceId\":\"" + ApiRig.DeviceId + "\"}";
        using var plain = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/login", json: body,
            tweak: r => r.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal("bad_request", (await ApiRig.JsonOf(plain)).GetProperty("error").GetString());
        Assert.Equal(0, rig.Jellyfin.CountOf("POST /Users/AuthenticateByName"));

        using var json = await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/login", json: body,
            tweak: r => r.Content!.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("Application/JSON; charset=utf-8"));
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
    }

    [Fact]
    public async Task AH14_a_bad_device_id_is_a_400_and_a_down_jellyfin_a_503()
    {
        using var rig = new ApiRig();

        using var bad = await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = "x", deviceId = "no" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        rig.Jellyfin.On("GET /Users/Me", HttpStatusCode.ServiceUnavailable);
        using var down = await rig.GetAsync("/api/v1/me", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("jellyfin_unavailable", (await ApiRig.JsonOf(down)).GetProperty("error").GetString());
    }

    // ---- path base -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AH15_a_path_base_serves_under_the_prefix_and_not_without_it()
    {
        using var rig = new ApiRig(new Dictionary<string, string?> { ["ENCORE_PATH_BASE"] = "/encore", ["ENCORE_TRUSTED_PROXIES"] = Proxies });

        using var prefixed = await rig.GetAsync("/encore/api/v1/config");
        Assert.Equal(HttpStatusCode.OK, prefixed.StatusCode);

        using var bare = await rig.GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);

        // A proxy may keep the prefix or strip it: both work.
        using var kept = await rig.GetAsync("/encore/api/v1/config", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        using var stripped = await rig.GetAsync("/api/v1/config", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.OK, stripped.StatusCode);

        // /health is for the container's own probe: reachable directly, and the base is not needed for it.
        using var health = await rig.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var proxiedHealth = await rig.GetAsync("/encore/health", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.NotFound, proxiedHealth.StatusCode);
    }

    [Fact]
    public async Task AH15b_the_bare_root_redirects_to_the_page_direct_and_proxied()
    {
        using var rig = new ApiRig(new Dictionary<string, string?> { ["ENCORE_TRUSTED_PROXIES"] = Proxies });

        foreach (var peer in new string?[] { null, "172.21.0.5" })
        {
            using var get = await rig.GetAsync("/", peer: peer, follow: false);
            Assert.Equal(HttpStatusCode.Found, get.StatusCode);
            Assert.Equal("request", get.Headers.Location!.OriginalString);
            using var head = await rig.SendAsync(HttpMethod.Head, "/", peer: peer, follow: false);
            Assert.Equal(HttpStatusCode.Found, head.StatusCode);
            using var post = await rig.SendAsync(HttpMethod.Post, "/", peer: peer, follow: false);
            Assert.NotEqual(HttpStatusCode.Found, post.StatusCode);
        }

        // Nothing else is let through for a proxied request, and /health stays LAN-only.
        using var other = await rig.GetAsync("/other", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using var proxiedHealth = await rig.GetAsync("/health", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.NotFound, proxiedHealth.StatusCode);
    }

    [Fact]
    public async Task AH15c_the_root_redirect_with_a_path_base_kept_or_stripped()
    {
        using var rig = new ApiRig(new Dictionary<string, string?> { ["ENCORE_PATH_BASE"] = "/encore", ["ENCORE_TRUSTED_PROXIES"] = Proxies });

        foreach (var path in new[] { "/encore/", "/" })
        {
            using var proxied = await rig.GetAsync(path, peer: "172.21.0.5", follow: false);
            Assert.Equal(HttpStatusCode.Found, proxied.StatusCode);
            Assert.Equal("request", proxied.Headers.Location!.OriginalString);
        }

        using var direct = await rig.GetAsync("/encore/", follow: false);
        Assert.Equal(HttpStatusCode.Found, direct.StatusCode);
        using var bare = await rig.GetAsync("/", follow: false);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        using var proxiedHealth = await rig.GetAsync("/encore/health", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.NotFound, proxiedHealth.StatusCode);
    }

    [Fact]
    public async Task AH15d_with_requests_off_the_root_stays_a_404()
    {
        using var rig = new ApiRig(on: false);
        using var root = await rig.GetAsync("/", follow: false);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
    }

    [Fact]
    public async Task AH16_without_a_path_base_the_api_is_at_the_root()
    {
        using var rig = new ApiRig();

        using var root = await rig.GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        using var prefixed = await rig.GetAsync("/encore/api/v1/config");
        Assert.Equal(HttpStatusCode.NotFound, prefixed.StatusCode);
    }

    // ---- forwarded headers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AH17_a_forwarded_ip_is_used_only_from_a_trusted_proxy()
    {
        using var rig = new ApiRig(WithProxies());

        Assert.Equal("203.0.113.7", await rig.SeenIpAsync("172.21.0.5", "203.0.113.7"));
        Assert.Equal("198.51.100.9", await rig.SeenIpAsync("198.51.100.9", "203.0.113.7"));
    }

    [Fact]
    public async Task AH18_an_ipv4_proxy_seen_in_its_mapped_form_works()
    {
        using var rig = new ApiRig(WithProxies());

        Assert.Equal("203.0.113.7", await rig.SeenIpAsync("::ffff:172.21.0.5", "203.0.113.7"));
        Assert.Equal("203.0.113.7", await rig.SeenIpAsync("::ffff:103.21.244.9", "203.0.113.7"));
        Assert.Equal("198.51.100.9", await rig.SeenIpAsync("::ffff:198.51.100.9", "203.0.113.7"));
    }

    [Fact]
    public async Task AH19_the_forwarded_list_is_read_from_the_right_and_stops_at_an_untrusted_hop()
    {
        using var rig = new ApiRig(WithProxies());

        // client, untrusted: the untrusted one is the last hop we may believe.
        Assert.Equal("203.0.113.50", await rig.SeenIpAsync("172.21.0.5", "198.51.100.1, 203.0.113.50"));

        // client, trusted Cloudflare edge: the real client.
        Assert.Equal("198.51.100.1", await rig.SeenIpAsync("172.21.0.5", "198.51.100.1, 103.21.244.7"));

        // A spoofed first entry cannot get past the limit of two hops.
        Assert.Equal("198.51.100.1", await rig.SeenIpAsync("172.21.0.5", "6.6.6.6, 198.51.100.1, 103.21.244.7"));
    }

    [Fact]
    public async Task AH20_no_trusted_proxies_means_forwarded_headers_are_ignored_even_from_loopback_and_private_peers()
    {
        using var rig = new ApiRig();

        Assert.Equal("172.21.0.5", await rig.SeenIpAsync("172.21.0.5", "203.0.113.7"));
        Assert.Equal("127.0.0.1", await rig.SeenIpAsync("127.0.0.1", "203.0.113.7"));
    }

    [Fact]
    public async Task AH21_loopback_is_not_trusted_unless_listed()
    {
        using var rig = new ApiRig(WithProxies());

        Assert.Equal("127.0.0.1", await rig.SeenIpAsync("127.0.0.1", "203.0.113.7"));
    }

    [Fact]
    public void AH22_the_forwarded_options_are_for_for_proto_and_host_only_with_both_known_lists_filled_from_the_config()
    {
        var trust = new ProxyTrust([IPNetwork.Parse("172.21.0.0/16"), IPNetwork.Parse("2606:4700::/32")]);
        var options = trust.ForwardedOptions();

        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
            options.ForwardedHeaders);
        Assert.Equal(2, options.ForwardLimit);
        Assert.Empty(options.KnownProxies);
        Assert.Contains(options.KnownIPNetworks, n => n.ToString() == "172.21.0.0/16");
        Assert.Contains(options.KnownIPNetworks, n => n.ToString() == "2606:4700::/32");
        Assert.Contains(options.KnownIPNetworks, n => n.ToString() == "::ffff:172.21.0.0/112");
        Assert.DoesNotContain(options.KnownIPNetworks, n => n.Contains(IPAddress.Loopback));
        Assert.True(trust.IsTrusted(IPAddress.Parse("::ffff:172.21.9.9")));
        Assert.False(trust.IsTrusted(IPAddress.Parse("172.22.0.1")));
        Assert.False(trust.IsTrusted(null));
    }

    [Fact]
    public async Task AH23_x_forwarded_prefix_is_ignored()
    {
        using var rig = new ApiRig(new Dictionary<string, string?> { ["ENCORE_PATH_BASE"] = "/encore", ["ENCORE_TRUSTED_PROXIES"] = Proxies });

        // With Prefix honoured, PathBase would become /evil and the gate would see a different request; it must not.
        using var response = await rig.SendAsync(HttpMethod.Get, "/api/v1/config", peer: "172.21.0.5", tweak: r =>
            r.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/evil"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var other = await rig.SendAsync(HttpMethod.Get, "/evil/api/v1/config", peer: "172.21.0.5", tweak: r =>
            r.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/evil"));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    // ---- proxied requests reach only /request and /api/v1 --------------------------------------------------------

    [Fact]
    public async Task AH24_a_proxied_request_to_health_or_anything_else_is_a_404_but_a_direct_one_is_a_200()
    {
        using var rig = new ApiRig(WithProxies());

        using var direct = await rig.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);

        using var proxied = await rig.GetAsync("/health", peer: "172.21.0.5", xff: "203.0.113.7");
        Assert.Equal(HttpStatusCode.NotFound, proxied.StatusCode);

        using var other = await rig.GetAsync("/anything", peer: "172.21.0.5", xff: "203.0.113.7");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        using var api = await rig.GetAsync("/api/v1/config", peer: "172.21.0.5", xff: "203.0.113.7");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        // A peer that is not a trusted proxy is a direct request, whatever headers it sends.
        using var untrusted = await rig.GetAsync("/health", peer: "198.51.100.9", xff: "203.0.113.7");
        Assert.Equal(HttpStatusCode.OK, untrusted.StatusCode);
    }

    // ---- the guard sees the worked-out client IP -------------------------------------------------------------------

    [Fact]
    public async Task AH25_failed_token_checks_block_the_forwarded_ip_on_every_api_route_anonymous_ones_too()
    {
        using var rig = new ApiRig(WithProxies());

        var blocked = false;
        for (var i = 0; i < 40 && !blocked; i++)
        {
            using var response = await rig.GetAsync("/api/v1/me", peer: "172.21.0.5", xff: "203.0.113.7", bearer: "short");
            blocked = response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        Assert.True(blocked);

        using var config = await rig.GetAsync("/api/v1/config", peer: "172.21.0.5", xff: "203.0.113.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, config.StatusCode);
        Assert.True(config.Headers.Contains("Retry-After"));
        Assert.Equal("login_limited", (await ApiRig.JsonOf(config)).GetProperty("error").GetString());

        using var login = await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = "x", deviceId = ApiRig.DeviceId }, peer: "172.21.0.5", xff: "203.0.113.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, login.StatusCode);

        // Another client behind the same proxy is not affected.
        using var other = await rig.GetAsync("/api/v1/config", peer: "172.21.0.5", xff: "203.0.113.8");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task AH26_a_forged_forwarded_ip_from_an_untrusted_peer_does_not_dodge_the_block()
    {
        using var rig = new ApiRig(WithProxies());

        var blocked = false;
        for (var i = 0; i < 40 && !blocked; i++)
        {
            using var response = await rig.GetAsync("/api/v1/me", peer: "198.51.100.9", xff: $"203.0.113.{i + 1}", bearer: "short");
            blocked = response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        Assert.True(blocked);
        using var again = await rig.GetAsync("/api/v1/config", peer: "198.51.100.9", xff: "203.0.113.200");
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
    }

    [Fact]
    public async Task AH27_failed_logins_count_against_the_forwarded_ip()
    {
        using var rig = new ApiRig(WithProxies());

        for (var i = 0; i < 5; i++)
        {
            using var failed = await rig.PostAsync("/api/v1/auth/login", new { username = "user" + i, password = "wrong", deviceId = ApiRig.DeviceId }, peer: "172.21.0.5", xff: "203.0.113.7");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var limited = await rig.PostAsync("/api/v1/auth/login", new { username = "user9", password = "wrong", deviceId = ApiRig.DeviceId }, peer: "172.21.0.5", xff: "203.0.113.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal("login_limited", (await ApiRig.JsonOf(limited)).GetProperty("error").GetString());

        using var elsewhere = await rig.PostAsync("/api/v1/auth/login", new { username = "user9", password = "wrong", deviceId = ApiRig.DeviceId }, peer: "172.21.0.5", xff: "203.0.113.8");
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
    }

    // ---- secrets and registration ---------------------------------------------------------------------------------

    [Fact]
    public async Task AH28_no_token_password_or_secret_reaches_a_log_line()
    {
        using var rig = new ApiRig(WithProxies());

        (await rig.GetAsync("/api/v1/me", peer: "172.21.0.5", xff: "203.0.113.7", bearer: ApiRig.KimToken)).Dispose();
        (await rig.GetAsync("/api/v1/me", bearer: ApiRig.KimToken)).Dispose();
        (await rig.GetAsync("/api/v1/me", bearer: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")).Dispose();
        (await rig.GetAsync("/api/v1/me", bearer: "bad token!")).Dispose();
        (await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = ApiRig.Password, deviceId = ApiRig.DeviceId })).Dispose();
        (await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = "secret-wrong-password", deviceId = ApiRig.DeviceId })).Dispose();
        (await rig.PostAsync("/api/v1/auth/quickconnect", new { deviceId = ApiRig.DeviceId })).Dispose();
        (await rig.PostAsync("/api/v1/auth/quickconnect/poll", new { secret = ApiRig.QuickSecret, deviceId = ApiRig.DeviceId })).Dispose();
        (await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", bearer: ApiRig.KimToken)).Dispose();

        // T114: the page's own token through every route that sees it.
        using var signIn = await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = ApiRig.Password, deviceId = ApiRig.DeviceId });
        var page = (await ApiRig.JsonOf(signIn)).GetProperty("token").GetString()!;
        (await rig.GetAsync("/api/v1/me", bearer: page)).Dispose();
        (await rig.GetAsync("/api/v1/me", bearer: "enc1_" + new string('Q', 43))).Dispose();
        (await rig.SendAsync(HttpMethod.Delete, "/api/v1/admin/sessions", bearer: ApiRig.NateToken)).Dispose();
        (await rig.GetAsync("/api/v1/me", bearer: page)).Dispose();
        (await rig.SendAsync(HttpMethod.Post, "/api/v1/auth/logout", bearer: page)).Dispose();

        var logs = rig.Logs.Everything();
        Assert.Contains("auth.", logs);
        Assert.Contains("auth.page_tokens_revoked", logs);
        foreach (var secret in new[]
                 {
                     page, page[5..], PageTokens.Hash(page), "enc1_",
                     ApiRig.NateToken, ApiRig.KimToken, ApiRig.LeeToken, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "bad token!",
                     ApiRig.Password, "secret-wrong-password", ApiRig.QuickSecret, "Bearer ", "Authorization",
                 })
        {
            Assert.DoesNotContain(secret, logs);
        }
    }

    [Fact]
    public void AH29_the_user_auth_client_is_plain_with_the_jellyfin_base_address_and_a_10_second_timeout()
    {
        var options = Encore.Config.EncoreOptionsLoader.Load(new Dictionary<string, string?>
        {
            ["JELLYFIN_URL"] = "http://jellyfin:8096/sub",
            ["JELLYFIN_API_KEY"] = "JFKEY0123456789abcdef",
            ["ENCORE_ALLOWED_USERS"] = "nate",
            ["ENCORE_DATA_DIR"] = Path.GetTempPath(),
        }).Options!;

        using var client = RequestsApi.CreateUserAuthClient(options);

        Assert.Equal("http://jellyfin:8096/sub/", client.BaseAddress!.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    // ---- registration -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AH30_with_requests_off_nothing_is_registered_and_the_api_is_a_404()
    {
        using var rig = new ApiRig(on: false);

        using var config = await rig.GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.NotFound, config.StatusCode);
        using var me = await rig.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.NotFound, me.StatusCode);
        using var health = await rig.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.False(health.Headers.Contains("Content-Security-Policy"));

        Assert.Null(rig.Services.GetService<UserAuth>());
        Assert.Null(rig.Services.GetService<RequestStore>());
        Assert.Null(rig.Services.GetService<ApiEndpoints>());
        Assert.Null(rig.Services.GetService<RequestUsers>());
    }

    [Fact]
    public async Task AH31_with_requests_on_the_health_route_still_answers_directly()
    {
        using var rig = new ApiRig();

        using var health = await rig.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.NotNull(rig.Services.GetService<ApiEndpoints>());
    }
}
