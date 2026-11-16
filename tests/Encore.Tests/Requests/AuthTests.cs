using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Encore.Jellyfin;
using Encore.Requests;
using Encore.Requests.Auth;
using Encore.Tests.Discovery;
using Encore.Tests.Watch;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Requests;

/// <summary>Fake Jellyfin for the user-token HttpClient: routes by "METHOD path", records every request with all its headers.</summary>
internal sealed class AuthHandler : HttpMessageHandler
{
    internal sealed record Call(string Method, string PathAndQuery, string? Authorization, IReadOnlyList<string> HeaderNames, string? Body);

    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];

    public List<Call> Calls { get; } = [];

    public AuthHandler On(string route, HttpStatusCode status, string? json = null) =>
        On(route, _ => Reply(status, json));

    public AuthHandler On(string route, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes[route] = answer;
        return this;
    }

    public int CountOf(string route) => Calls.Count(c => $"{c.Method} {c.PathAndQuery}" == route);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = $"{request.Method.Method} {request.RequestUri!.PathAndQuery}";
        Calls.Add(new Call(
            request.Method.Method,
            request.RequestUri.PathAndQuery,
            request.Headers.Contains("Authorization") ? string.Join("|", request.Headers.GetValues("Authorization")) : null,
            request.Headers.Select(h => h.Key).ToList(),
            request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));
        return Task.FromResult(_routes.TryGetValue(route, out var answer) ? answer(request) : Reply(HttpStatusCode.NotFound));
    }

    public static HttpResponseMessage Reply(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return response;
    }
}

/// <summary>Jellyfin for RequestUsers: GET /Users from a list, or a 503.</summary>
internal sealed class UsersClient : IJellyfinClient
{
    public List<UserDto> Users { get; set; } = [];

    public bool Down { get; set; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        Calls++;
        return Down ? throw new JellyfinRequestException("GetUsers", 503) : Task.FromResult<IReadOnlyList<UserDto>>(Users);
    }

    public Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default) => throw new NotSupportedException();

    public Task ReportMediaUpdatedAsync(IReadOnlyCollection<string> jellyfinPaths, CancellationToken ct = default) => throw new NotSupportedException();

    public PublicSystemInfo PublicInfo { get; set; } = new() { Id = "serverid1234567890", ServerName = "Fake", Version = "10.11.0" };

    public bool PublicInfoDown { get; set; }

    public int PublicInfoCalls { get; private set; }

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default)
    {
        PublicInfoCalls++;
        return PublicInfoDown ? throw new JellyfinRequestException("GetPublicSystemInfo", 503) : Task.FromResult(PublicInfo);
    }

    public Task<IReadOnlyList<VirtualFolderInfo>> GetVirtualFoldersAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> GetPhysicalPathsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>What <see cref="SearchItemsAsync"/> returns; <see cref="SearchFailure"/> makes it throw, <see cref="SearchHang"/> makes it wait for the caller to cancel.</summary>
    public List<BaseItemDto> SearchResult { get; set; } = [];

    public Exception? SearchFailure { get; set; }

    public bool SearchHang { get; set; }

    public List<(string Term, Guid UserId, int Limit)> SearchCalls { get; } = [];

    public async Task<QueryResult<BaseItemDto>> SearchItemsAsync(string searchTerm, Guid userId, int limit, CancellationToken ct = default)
    {
        SearchCalls.Add((searchTerm, userId, limit));
        if (SearchHang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        return SearchFailure is { } failure
            ? throw failure
            : new QueryResult<BaseItemDto> { Items = SearchResult, TotalRecordCount = SearchResult.Count };
    }

    public Task<IReadOnlyList<BaseItemDto>> GetRecentAudioAsync(Guid parentId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class AuthTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string OtherToken = "fedcba9876543210fedcba9876543210";
    private const string Password = "hunter2-Secret-Password";
    private const string DeviceId = "aabbccddeeff00112233445566778899";
    private const string ApiKey = "ENCOREAPIKEY1234567890abcdef12345678";
    private static readonly Guid Nate = Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f");
    private static readonly Guid Kim = Guid.Parse("11111111222233334444555555555555");

    private static string MeJson(Guid id, string name, bool admin = false, bool disabled = false) =>
        "{\"Id\":\"" + id.ToString("N") + "\",\"Name\":\"" + name + "\",\"Policy\":{\"IsAdministrator\":" + (admin ? "true" : "false") + ",\"IsDisabled\":" + (disabled ? "true" : "false") + "}}";
    private static string LoginJson(Guid id, string name, string token, bool admin = false) =>
        $$"""{"User":{{MeJson(id, name, admin)}},"AccessToken":"{{token}}","ServerId":"s"}""";

    private sealed class Rig
    {
        public AuthHandler Handler { get; } = new();
        public ManualClock Clock { get; } = new();
        public ListLogger<UserAuth> AuthLogger { get; } = new();
        public ListLogger<LoginGuard> GuardLogger { get; } = new();
        public ListLogger<RequestUsers> UsersLogger { get; } = new();
        public UsersClient Jellyfin { get; } = new();
        public RequestUsers Users { get; }
        public LoginGuard Guard { get; }
        public UserAuth Auth { get; }

        public Rig(IReadOnlyList<string>? entries = null, bool all = false)
        {
            Users = new RequestUsers(Jellyfin, entries ?? [], all, UsersLogger, Clock);
            Guard = new LoginGuard(Clock, GuardLogger, Users.LockoutLimit);
            var http = new HttpClient(Handler) { BaseAddress = new Uri("http://jellyfin.test:8096/") };
            Auth = new UserAuth(http, Users, Guard, AuthLogger, Clock, "0.3.0-test");
            Handler.On("GET /Users/Me", HttpStatusCode.OK, MeJson(Nate, "nate"));
        }

        public IEnumerable<LogEntry> AllLogs => AuthLogger.Entries.Concat(GuardLogger.Entries).Concat(UsersLogger.Entries);

        public string Bearer(string token = Token) => "Bearer " + token;

        public static IPAddress Ip(int n) => IPAddress.Parse($"10.0.{n / 250}.{n % 250 + 1}");
    }

    private static async Task<AuthOutcome<LoginResult>> Login(Rig rig, string name, IPAddress ip, string? password = null) =>
        await rig.Auth.LoginAsync(name, password ?? Password, DeviceId, ip);

    // ---- token validation -------------------------------------------------------------------------------------

    [Fact]
    public async Task UA01_users_me_gets_a_token_only_header()
    {
        var rig = new Rig();
        var result = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));

        Assert.True(result.Ok);
        Assert.Equal(new RequestUser(Nate, "nate", false, false), result.Value);
        var call = Assert.Single(rig.Handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/Users/Me", call.PathAndQuery);
        Assert.Equal($"MediaBrowser Token=\"{Token}\"", call.Authorization);
        Assert.Equal(["Authorization"], call.HeaderNames);
        foreach (var banned in new[] { "Client", "Device", "DeviceId", "Version", ApiKey })
        {
            Assert.DoesNotContain(banned, call.Authorization!);
        }
    }

    [Fact]
    public async Task UA02_a_good_answer_is_cached_for_60_seconds()
    {
        var rig = new Rig();
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        var hit = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));

        Assert.True(hit.Ok);
        Assert.Equal(1, rig.Handler.CountOf("GET /Users/Me"));

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        Assert.Equal(2, rig.Handler.CountOf("GET /Users/Me"));
    }

    [Fact]
    public async Task UA03_the_cache_holds_a_hash_never_the_token()
    {
        var rig = new Rig();
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));

        var cache = typeof(UserAuth).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig.Auth)!;
        var strings = new List<string>();
        Collect(cache, strings, []);
        Assert.NotEmpty(strings);
        Assert.DoesNotContain(strings, s => s.Contains(Token, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(strings, s => s.Length == 64);
    }

    private static void Collect(object? node, List<string> strings, HashSet<object> seen)
    {
        if (node is null)
        {
            return;
        }

        if (node is string s)
        {
            strings.Add(s);
            return;
        }

        var type = node.GetType();
        if (type.IsPrimitive || type.IsEnum || node is Guid || node is DateTimeOffset || node is TimeSpan || node is TimeProvider)
        {
            return;
        }

        if (!type.IsValueType && !seen.Add(node))
        {
            return;
        }

        if (node is IEnumerable items)
        {
            foreach (var item in items)
            {
                Collect(item, strings, seen);
            }
        }

        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                Collect(field.GetValue(node), strings, seen);
            }
        }
    }

    [Fact]
    public async Task UA04_status_400_means_an_api_key_and_is_a_401_cached_for_10_seconds()
    {
        var rig = new Rig();
        rig.Handler.On("GET /Users/Me", HttpStatusCode.BadRequest);

        var first = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        Assert.Equal(401, first.Error!.Status);
        Assert.Equal("invalid_token", first.Error.Code);

        rig.Clock.Advance(TimeSpan.FromSeconds(9));
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        Assert.Equal(1, rig.Handler.CountOf("GET /Users/Me"));

        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        Assert.Equal(2, rig.Handler.CountOf("GET /Users/Me"));
    }

    [Fact]
    public async Task UA05_status_401_is_invalid_token()
    {
        var rig = new Rig();
        rig.Handler.On("GET /Users/Me", HttpStatusCode.Unauthorized);

        var result = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));

        Assert.Equal(401, result.Error!.Status);
        Assert.Equal("invalid_token", result.Error.Code);
    }

    [Fact]
    public async Task UA06_a_disabled_user_is_403()
    {
        var rig = new Rig();
        rig.Handler.On("GET /Users/Me", HttpStatusCode.OK, MeJson(Nate, "nate", disabled: true));

        var result = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));

        Assert.Equal(403, result.Error!.Status);
        Assert.Equal("user_disabled", result.Error.Code);
    }

    [Fact]
    public async Task UA07_jellyfin_down_is_503_and_not_cached()
    {
        var rig = new Rig();
        rig.Handler.On("GET /Users/Me", HttpStatusCode.InternalServerError);

        var first = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        Assert.Equal(503, first.Error!.Status);
        Assert.Equal("jellyfin_unavailable", first.Error.Code);

        rig.Handler.On("GET /Users/Me", HttpStatusCode.OK, MeJson(Nate, "nate"));
        var second = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        Assert.True(second.Ok);
        Assert.Equal(2, rig.Handler.CountOf("GET /Users/Me"));
    }

    [Fact]
    public async Task UA07b_a_transport_error_is_503()
    {
        var rig = new Rig();
        rig.Handler.On("GET /Users/Me", _ => throw new HttpRequestException("connection refused to http://jellyfin.test"));

        var result = await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));

        Assert.Equal(503, result.Error!.Status);
        Assert.DoesNotContain(rig.AllLogs, e => e.Message.Contains("jellyfin.test"));
    }

    [Theory]
    [InlineData(null, "missing_token")]
    [InlineData("", "missing_token")]
    [InlineData("Basic abc", "malformed_token")]
    [InlineData("Bearer short", "malformed_token")]
    [InlineData("Bearer 0123456789abcdef0123456789abcd!!", "malformed_token")]
    public async Task UA08_missing_or_malformed_headers_are_401_without_a_jellyfin_call(string? header, string code)
    {
        var rig = new Rig();

        var result = await rig.Auth.ValidateAsync(header, Rig.Ip(1));

        Assert.Equal(401, result.Error!.Status);
        Assert.Equal(code, result.Error.Code);
        Assert.Empty(rig.Handler.Calls);
    }

    [Fact]
    public async Task UA09_the_cache_keeps_at_most_1000_tokens()
    {
        var rig = new Rig();
        for (var i = 0; i < 1001; i++)
        {
            await rig.Auth.ValidateAsync("Bearer " + i.ToString("x32"), Rig.Ip(1));
        }

        Assert.Equal(1001, rig.Handler.Calls.Count);
        await rig.Auth.ValidateAsync("Bearer " + 1000.ToString("x32"), Rig.Ip(1));
        Assert.Equal(1001, rig.Handler.Calls.Count);

        // The oldest one was pushed out.
        await rig.Auth.ValidateAsync("Bearer " + 0.ToString("x32"), Rig.Ip(1));
        Assert.Equal(1002, rig.Handler.Calls.Count);
    }

    // ---- request-user rule ------------------------------------------------------------------------------------

    private static UserDto User(Guid id, string name, int? lockout = null, bool admin = false) =>
        new() { Id = id, Name = name, Policy = new UserPolicy { IsAdministrator = admin, LoginAttemptsBeforeLockout = lockout } };

    [Fact]
    public async Task RU01_admin_listed_star_and_other()
    {
        var jellyfin = new UsersClient { Users = [User(Nate, "nate"), User(Kim, "Kim")] };
        var listed = new RequestUsers(jellyfin, ["kim"], false, new ListLogger<RequestUsers>());
        await listed.RefreshAsync();

        Assert.True(listed.CanRequest(new RequestUser(Nate, "nate", true, false)));
        Assert.True(listed.CanRequest(new RequestUser(Kim, "Kim", false, false)));
        Assert.False(listed.CanRequest(new RequestUser(Nate, "nate", false, false)));

        var everyone = new RequestUsers(jellyfin, [], true, new ListLogger<RequestUsers>());
        Assert.True(everyone.CanRequest(new RequestUser(Nate, "nate", false, false)));

        var byId = new RequestUsers(jellyfin, [Nate.ToString("N")], false, new ListLogger<RequestUsers>());
        await byId.RefreshAsync();
        Assert.True(byId.CanRequest(new RequestUser(Nate, "nate", false, false)));
        Assert.False(byId.CanRequest(new RequestUser(Kim, "Kim", false, false)));
    }

    [Fact]
    public async Task RU02_a_failed_refresh_keeps_the_last_answer_and_the_loop_refreshes_every_10_minutes()
    {
        var rig = new Rig(["kim"]);
        rig.Jellyfin.Users = [User(Nate, "nate"), User(Kim, "kim", lockout: 3)];
        Assert.True(await rig.Users.RefreshAsync());

        rig.Jellyfin.Down = true;
        Assert.False(await rig.Users.RefreshAsync());
        Assert.True(rig.Users.CanRequest(new RequestUser(Kim, "kim", false, false)));
        Assert.Equal(3, rig.Users.LockoutLimit("KIM"));
        Assert.Contains(rig.UsersLogger.Entries, e => e.EventId.Id == 9040);

        rig.Jellyfin.Down = false;
        var before = rig.Jellyfin.Calls;
        await rig.Users.StartAsync(CancellationToken.None);
        await Wait.Until(() => rig.Jellyfin.Calls == before + 1 && rig.Clock.PendingTimers > 0, "first refresh");
        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        await Wait.Until(() => rig.Jellyfin.Calls == before + 2, "second refresh after 10 minutes");
        await rig.Users.StopAsync(CancellationToken.None);
    }

    // ---- login ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LG01_login_sends_the_device_id_and_no_token_and_returns_the_token()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.OK, LoginJson(Nate, "nate", Token, admin: true));

        var result = await Login(rig, "nate", Rig.Ip(1));

        Assert.True(result.Ok);
        Assert.Equal(Token, result.Value!.Token);
        Assert.Equal(new RequestUser(Nate, "nate", true, false), result.Value.User);
        var call = Assert.Single(rig.Handler.Calls);
        Assert.Equal($"MediaBrowser Client=\"Encore Requests\", Device=\"Encore Requests\", DeviceId=\"{DeviceId}\", Version=\"0.3.0-test\"", call.Authorization);
        Assert.DoesNotContain("Token", call.Authorization!);
        Assert.Equal(["Authorization"], call.HeaderNames);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("nate", body.RootElement.GetProperty("Username").GetString());
        Assert.Equal(Password, body.RootElement.GetProperty("Pw").GetString());
    }

    [Fact]
    public async Task LG02_no_log_line_holds_a_password_or_a_token()
    {
        var rig = new Rig();
        rig.Jellyfin.Users = [User(Nate, "nate", lockout: 3)];
        await rig.Users.RefreshAsync();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.OK, LoginJson(Nate, "nate", Token));
        rig.Handler.On("POST /Sessions/Logout", HttpStatusCode.NoContent);

        await Login(rig, "nate", Rig.Ip(1));
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        await rig.Auth.ValidateAsync(rig.Bearer(OtherToken), Rig.Ip(1));
        await rig.Auth.ValidateAsync("Bearer " + Password, Rig.Ip(1));
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);
        await Login(rig, "nate", Rig.Ip(2), Password + "x");
        await Login(rig, Password, Rig.Ip(3));
        rig.Handler.On("GET /Users/Me", HttpStatusCode.Unauthorized);
        await rig.Auth.ValidateAsync(rig.Bearer("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Rig.Ip(4));
        await rig.Auth.LogoutAsync(rig.Bearer(), Rig.Ip(1));

        Assert.NotEmpty(rig.AllLogs);
        foreach (var entry in rig.AllLogs)
        {
            var text = entry.Message + " " + string.Join(" ", entry.Fields.Values.Select(v => v?.ToString()));
            Assert.DoesNotContain(Password, text);
            Assert.DoesNotContain(Token, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(OtherToken, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", text);
            Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LG03_wrong_password_is_401_and_a_bad_request_is_400()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);

        var wrong = await Login(rig, "nate", Rig.Ip(1));
        Assert.Equal(401, wrong.Error!.Status);
        Assert.Equal("invalid_credentials", wrong.Error.Code);

        Assert.Equal(400, (await rig.Auth.LoginAsync("", Password, DeviceId, Rig.Ip(1))).Error!.Status);
        Assert.Equal(400, (await rig.Auth.LoginAsync("nate", Password, "short", Rig.Ip(1))).Error!.Status);
        Assert.Equal(400, (await rig.Auth.LoginAsync("nate", Password, "bad id with spaces!", Rig.Ip(1))).Error!.Status);
        Assert.Equal(1, rig.Handler.CountOf("POST /Users/AuthenticateByName"));
    }

    [Fact]
    public async Task LG04_jellyfin_errors_are_503_and_do_not_count_as_failed_logins()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.BadGateway);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(503, (await Login(rig, "nate", Rig.Ip(1))).Error!.Status);
        }

        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.OK, LoginJson(Nate, "nate", Token));
        Assert.True((await Login(rig, "nate", Rig.Ip(1))).Ok);
    }

    // ---- brute-force guard ------------------------------------------------------------------------------------

    [Fact]
    public async Task BF01_the_sixth_failure_from_one_ip_is_429_with_retry_after()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);
        var ip = Rig.Ip(1);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(401, (await Login(rig, "name" + i, ip)).Error!.Status);
        }

        var sixth = await Login(rig, "name9", ip);
        Assert.Equal(429, sixth.Error!.Status);
        Assert.Equal("login_limited", sixth.Error.Code);
        Assert.Equal(TimeSpan.FromMinutes(15), sixth.Error.RetryAfter);
        Assert.Equal(5, rig.Handler.CountOf("POST /Users/AuthenticateByName"));

        // Another IP is not affected; this one is free again after 15 minutes.
        Assert.Equal(401, (await Login(rig, "name9", Rig.Ip(2))).Error!.Status);
        rig.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(401, (await Login(rig, "name9", ip)).Error!.Status);

        var limited = rig.GuardLogger.Named("auth.login_limited").Single(e => (string)e.Fields["Scope"]! == "ip");
        Assert.Equal("10.0.0.2", limited.Fields["RemoteIp"]);
        Assert.Equal(1, rig.GuardLogger.Named("auth.login_limited").Count(e => (string)e.Fields["Scope"]! == "ip"));
    }

    [Fact]
    public async Task BF02_the_eleventh_failure_for_one_name_from_many_ips_is_429()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(401, (await Login(rig, i % 2 == 0 ? "Victim" : "victim ", Rig.Ip(i))).Error!.Status);
        }

        var eleventh = await Login(rig, "VICTIM", Rig.Ip(50));
        Assert.Equal(429, eleventh.Error!.Status);
        Assert.Equal("login_limited", eleventh.Error.Code);
        Assert.Equal(TimeSpan.FromHours(1), eleventh.Error.RetryAfter);

        // The same answer for a name that does not exist at Jellyfin, and other names still work.
        Assert.Equal(401, (await Login(rig, "someone-else", Rig.Ip(51))).Error!.Status);

        var limited = Assert.Single(rig.GuardLogger.Named("auth.login_limited"), e => (string)e.Fields["Scope"]! == "name");
        var hash = (string)limited.Fields["NameHash"]!;
        Assert.Equal(12, hash.Length);
        Assert.DoesNotContain("victim", limited.Message, StringComparison.OrdinalIgnoreCase);

        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(401, (await Login(rig, "victim", Rig.Ip(52))).Error!.Status);
    }

    [Fact]
    public async Task BF03_a_successful_login_clears_the_ip_and_name_counts()
    {
        var rig = new Rig();
        var ip = Rig.Ip(1);
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);
        for (var i = 0; i < 4; i++)
        {
            await Login(rig, "nate", ip);
        }

        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.OK, LoginJson(Nate, "nate", Token));
        Assert.True((await Login(rig, "nate", ip)).Ok);

        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(401, (await Login(rig, "nate", ip)).Error!.Status);
        }

        // 4 failures again, not 8: the 5th still reaches Jellyfin and only the 6th is refused.
        Assert.Equal(401, (await Login(rig, "nate", ip)).Error!.Status);
        Assert.Equal(429, (await Login(rig, "nate", ip)).Error!.Status);
    }

    [Fact]
    public async Task BF04_at_most_30_login_attempts_a_minute_in_total()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(401, (await Login(rig, "n" + i, Rig.Ip(i))).Error!.Status);
        }

        var over = await Login(rig, "n99", Rig.Ip(99));
        Assert.Equal(429, over.Error!.Status);
        Assert.True(over.Error.RetryAfter > TimeSpan.Zero && over.Error.RetryAfter <= TimeSpan.FromMinutes(1));
        Assert.Equal(1, rig.GuardLogger.Named("auth.login_limited").Count(e => (string)e.Fields["Scope"]! == "global"));

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(401, (await Login(rig, "n99", Rig.Ip(99))).Error!.Status);
    }

    [Fact]
    public async Task BF05_thirty_failed_token_checks_block_the_ip_on_every_route_for_15_minutes()
    {
        var rig = new Rig();
        rig.Handler.On("GET /Users/Me", HttpStatusCode.Unauthorized);
        var ip = Rig.Ip(1);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal("invalid_token", (await rig.Auth.ValidateAsync("Bearer " + i.ToString("x32"), ip)).Error!.Code);
        }

        // Even a good token from that IP is refused now, and so is a login.
        rig.Handler.On("GET /Users/Me", HttpStatusCode.OK, MeJson(Nate, "nate"));
        var blocked = await rig.Auth.ValidateAsync(rig.Bearer(), ip);
        Assert.Equal(429, blocked.Error!.Status);
        Assert.Equal("login_limited", blocked.Error.Code);
        Assert.Equal(429, (await Login(rig, "nate", ip)).Error!.Status);
        Assert.False(rig.Guard.CheckTokenBlock(ip).Allowed);

        // Other IPs are fine; this one is free after 15 minutes.
        Assert.True((await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(2))).Ok);
        rig.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True((await rig.Auth.ValidateAsync(rig.Bearer(), ip)).Ok);
        Assert.Single(rig.GuardLogger.Named("auth.login_limited"), e => (string)e.Fields["Scope"]! == "token");
    }

    [Fact]
    public void BF06_an_ipv4_mapped_address_counts_as_the_same_ip()
    {
        var rig = new Rig();
        var v4 = IPAddress.Parse("192.168.1.9");
        for (var i = 0; i < 5; i++)
        {
            rig.Guard.CheckLogin(i % 2 == 0 ? v4 : v4.MapToIPv6(), "n" + i);
            rig.Guard.RecordLoginFailure(i % 2 == 0 ? v4 : v4.MapToIPv6(), "n" + i);
        }

        Assert.False(rig.Guard.CheckLogin(v4, "other").Allowed);
        Assert.False(rig.Guard.CheckLogin(v4.MapToIPv6(), "other").Allowed);
    }

    // ---- Jellyfin lockout policy ------------------------------------------------------------------------------

    [Fact]
    public async Task LP01_a_user_with_limit_3_is_locked_for_24_hours_after_the_second_failure()
    {
        var rig = new Rig();
        rig.Jellyfin.Users = [User(Nate, "nate", lockout: 3)];
        await rig.Users.RefreshAsync();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);

        Assert.Equal(401, (await Login(rig, "nate", Rig.Ip(1))).Error!.Status);
        Assert.Equal(401, (await Login(rig, "Nate", Rig.Ip(2))).Error!.Status);

        var third = await Login(rig, "nate", Rig.Ip(3));
        Assert.Equal(429, third.Error!.Status);
        Assert.Equal(TimeSpan.FromHours(24), third.Error.RetryAfter);
        Assert.Equal(2, rig.Handler.CountOf("POST /Users/AuthenticateByName"));

        rig.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(429, (await Login(rig, "nate", Rig.Ip(4))).Error!.Status);
        rig.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(401, (await Login(rig, "nate", Rig.Ip(5))).Error!.Status);
    }

    [Fact]
    public async Task LP02_limit_1_locks_after_the_first_failure()
    {
        var rig = new Rig();
        rig.Jellyfin.Users = [User(Nate, "nate", lockout: 1)];
        await rig.Users.RefreshAsync();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);

        Assert.Equal(401, (await Login(rig, "nate", Rig.Ip(1))).Error!.Status);
        Assert.Equal(429, (await Login(rig, "nate", Rig.Ip(2))).Error!.Status);
    }

    [Fact]
    public async Task LP03_a_user_with_no_limit_keeps_the_10_in_an_hour_rule()
    {
        var rig = new Rig();
        rig.Jellyfin.Users = [User(Nate, "nate", lockout: -1), User(Kim, "kim", lockout: 0)];
        await rig.Users.RefreshAsync();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(401, (await Login(rig, "nate", Rig.Ip(i))).Error!.Status);
        }

        var next = await Login(rig, "nate", Rig.Ip(20));
        Assert.Equal(429, next.Error!.Status);
        Assert.Equal(TimeSpan.FromHours(1), next.Error.RetryAfter);
        Assert.Null(rig.Users.LockoutLimit("kim"));
    }

    [Fact]
    public async Task LP04_a_success_clears_the_policy_count_too()
    {
        var rig = new Rig();
        rig.Jellyfin.Users = [User(Nate, "nate", lockout: 3)];
        await rig.Users.RefreshAsync();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);
        await Login(rig, "nate", Rig.Ip(1));
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.OK, LoginJson(Nate, "nate", Token));
        Assert.True((await Login(rig, "nate", Rig.Ip(1))).Ok);
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Unauthorized);

        Assert.Equal(401, (await Login(rig, "nate", Rig.Ip(2))).Error!.Status);
        Assert.Equal(401, (await Login(rig, "nate", Rig.Ip(3))).Error!.Status);
        Assert.Equal(429, (await Login(rig, "nate", Rig.Ip(4))).Error!.Status);
    }

    [Fact]
    public async Task LP05_log_9050_gives_counts_and_no_names_and_repeats_only_on_change()
    {
        var rig = new Rig();
        rig.Jellyfin.Users = [User(Nate, "nate", lockout: 3), User(Kim, "kim", lockout: 5), User(Guid.NewGuid(), "plain")];
        await rig.Users.RefreshAsync();
        await rig.Users.RefreshAsync();

        var line = Assert.Single(rig.UsersLogger.Named("auth.lockout_policies"));
        Assert.Equal(9050, line.EventId.Id);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal(2, line.Fields["UsersWithLimit"]);
        Assert.Equal(3, line.Fields["LowestLimit"]);
        Assert.DoesNotContain("nate", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kim", line.Message, StringComparison.OrdinalIgnoreCase);

        rig.Jellyfin.Users = [User(Nate, "nate", lockout: 3)];
        await rig.Users.RefreshAsync();
        Assert.Equal(2, rig.UsersLogger.Named("auth.lockout_policies").Count());
    }

    // ---- Quick Connect ----------------------------------------------------------------------------------------

    [Fact]
    public async Task QC01_enabled_start_poll_and_finish()
    {
        var rig = new Rig();
        rig.Handler.On("GET /QuickConnect/Enabled", HttpStatusCode.OK, "true");
        Assert.True(await rig.Auth.QuickConnectEnabledAsync());

        rig.Handler.On("POST /QuickConnect/Initiate", HttpStatusCode.OK, """{"Authenticated":false,"Secret":"s3cretvalue","Code":"123456"}""");
        var start = await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(1));
        Assert.True(start.Ok);
        Assert.Equal(new QuickConnectStart("123456", "s3cretvalue"), start.Value);
        var initiate = rig.Handler.Calls.Single(c => c.PathAndQuery == "/QuickConnect/Initiate");
        Assert.Contains($"DeviceId=\"{DeviceId}\"", initiate.Authorization);
        Assert.DoesNotContain("Token", initiate.Authorization!);

        rig.Handler.On("GET /QuickConnect/Connect?secret=s3cretvalue", HttpStatusCode.OK, """{"Authenticated":false,"Secret":"s3cretvalue","Code":"123456"}""");
        var waiting = await rig.Auth.QuickConnectPollAsync("s3cretvalue", DeviceId, Rig.Ip(1));
        Assert.True(waiting.Ok);
        Assert.False(waiting.Value!.Authenticated);
        Assert.Equal(0, rig.Handler.CountOf("POST /Users/AuthenticateWithQuickConnect"));

        rig.Handler.On("GET /QuickConnect/Connect?secret=s3cretvalue", HttpStatusCode.OK, """{"Authenticated":true,"Secret":"s3cretvalue","Code":"123456"}""");
        rig.Handler.On("POST /Users/AuthenticateWithQuickConnect", HttpStatusCode.OK, LoginJson(Nate, "nate", Token));
        var done = await rig.Auth.QuickConnectPollAsync("s3cretvalue", DeviceId, Rig.Ip(1));
        Assert.True(done.Ok);
        Assert.True(done.Value!.Authenticated);
        Assert.Equal(Token, done.Value.Login!.Token);
        var finish = rig.Handler.Calls.Single(c => c.PathAndQuery == "/Users/AuthenticateWithQuickConnect");
        Assert.Equal("""{"Secret":"s3cretvalue"}""", finish.Body);

        // The secret is used up.
        Assert.Equal(404, (await rig.Auth.QuickConnectPollAsync("s3cretvalue", DeviceId, Rig.Ip(1))).Error!.Status);

        foreach (var entry in rig.AllLogs)
        {
            Assert.DoesNotContain("s3cretvalue", entry.Message + string.Join(" ", entry.Fields.Values.Select(v => v?.ToString())));
        }
    }

    [Fact]
    public async Task QC02_a_poll_with_a_secret_encore_never_issued_makes_no_jellyfin_call()
    {
        var rig = new Rig();

        var result = await rig.Auth.QuickConnectPollAsync("guessedsecret", DeviceId, Rig.Ip(1));

        Assert.Equal(404, result.Error!.Status);
        Assert.Equal("unknown_secret", result.Error.Code);
        Assert.Empty(rig.Handler.Calls);
    }

    [Fact]
    public async Task QC03_a_secret_expires_after_a_few_minutes()
    {
        var rig = new Rig();
        rig.Handler.On("POST /QuickConnect/Initiate", HttpStatusCode.OK, """{"Authenticated":false,"Secret":"s3cretvalue","Code":"123456"}""");
        await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(1));

        rig.Clock.Advance(TimeSpan.FromMinutes(7));

        Assert.Equal(404, (await rig.Auth.QuickConnectPollAsync("s3cretvalue", DeviceId, Rig.Ip(1))).Error!.Status);
    }

    [Fact]
    public async Task QC04_ten_starts_per_ip_per_15_minutes()
    {
        var rig = new Rig();
        rig.Handler.On("POST /QuickConnect/Initiate", HttpStatusCode.OK, """{"Authenticated":false,"Secret":"s3cretvalue","Code":"123456"}""");
        for (var i = 0; i < 10; i++)
        {
            Assert.True((await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(1))).Ok);
        }

        var eleventh = await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(1));
        Assert.Equal(429, eleventh.Error!.Status);
        Assert.True(eleventh.Error.RetryAfter > TimeSpan.Zero);
        Assert.True((await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(2))).Ok);
        Assert.Equal(11, rig.Handler.CountOf("POST /QuickConnect/Initiate"));
    }

    [Fact]
    public async Task QC05_quick_connect_off_in_jellyfin_is_reported()
    {
        var rig = new Rig();
        rig.Handler.On("POST /QuickConnect/Initiate", HttpStatusCode.Unauthorized);
        rig.Handler.On("GET /QuickConnect/Enabled", HttpStatusCode.OK, "false");

        Assert.False(await rig.Auth.QuickConnectEnabledAsync());
        var result = await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(1));
        Assert.Equal("quickconnect_disabled", result.Error!.Code);
    }

    // ---- logout -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task LO01_logout_calls_sessions_logout_with_the_token_and_drops_the_cache_entry()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Sessions/Logout", HttpStatusCode.NoContent);
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));

        var result = await rig.Auth.LogoutAsync(rig.Bearer(), Rig.Ip(1));

        Assert.True(result.Ok);
        var call = rig.Handler.Calls.Single(c => c.PathAndQuery == "/Sessions/Logout");
        Assert.Equal($"MediaBrowser Token=\"{Token}\"", call.Authorization);
        Assert.Equal(["Authorization"], call.HeaderNames);

        rig.Handler.On("GET /Users/Me", HttpStatusCode.Unauthorized);
        Assert.Equal(401, (await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1))).Error!.Status);
        Assert.Equal(2, rig.Handler.CountOf("GET /Users/Me"));
    }

    [Fact]
    public async Task LO02_logout_with_jellyfin_down_still_drops_the_cache_entry()
    {
        var rig = new Rig();
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        rig.Handler.On("POST /Sessions/Logout", HttpStatusCode.InternalServerError);

        var result = await rig.Auth.LogoutAsync(rig.Bearer(), Rig.Ip(1));

        Assert.Equal(503, result.Error!.Status);
        await rig.Auth.ValidateAsync(rig.Bearer(), Rig.Ip(1));
        Assert.Equal(2, rig.Handler.CountOf("GET /Users/Me"));
    }
}
