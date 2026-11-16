using System.Globalization;
using System.Net;
using Encore.Jellyfin;
using Encore.Requests.Auth;
using Encore.Store;
using Encore.Tests.Discovery;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;

namespace Encore.Tests.Requests;

/// <summary>An in-memory page token table for UserAuth tests (the SQLite one is tested in <see cref="PageTokenTests"/>).</summary>
internal sealed class MemoryPageTokenStore : IPageTokenStore
{
    public Dictionary<string, PageTokenRow> Rows { get; } = [];

    public void InsertPageToken(string tokenHash, string userId, DateTimeOffset at) => Rows.Add(tokenHash, new PageTokenRow(tokenHash, userId, at, at));

    public PageTokenRow? GetPageToken(string tokenHash) => Rows.TryGetValue(tokenHash, out var row) ? row : null;

    public void TouchPageToken(string tokenHash, DateTimeOffset at)
    {
        if (Rows.TryGetValue(tokenHash, out var row))
        {
            Rows[tokenHash] = row with { LastUsedUtc = at };
        }
    }

    public bool DeletePageToken(string tokenHash) => Rows.Remove(tokenHash);

    public int DeletePageTokens(string? userId) => RemoveWhere(r => userId is null || r.UserId == userId);

    public int DeletePageTokensExcept(IReadOnlySet<string> keepUserIds) => RemoveWhere(r => !keepUserIds.Contains(r.UserId));

    public int DeleteExpiredPageTokens(DateTimeOffset unusedBefore, DateTimeOffset createdBefore) =>
        RemoveWhere(r => r.LastUsedUtc < unusedBefore || r.CreatedUtc < createdBefore);

    public int CountPageTokens() => Rows.Count;

    private int RemoveWhere(Func<PageTokenRow, bool> match)
    {
        var gone = Rows.Values.Where(match).Select(r => r.TokenHash).ToList();
        gone.ForEach(h => Rows.Remove(h));
        return gone.Count;
    }
}

/// <summary>T114: the request page's own tokens (no Jellyfin session slot held after sign-in).</summary>
public sealed class PageTokenTests : IDisposable
{
    private const string JellyfinToken = "0123456789abcdef0123456789abcdef";
    private const string Password = "hunter2-Secret-Password";
    private const string DeviceId = "aabbccddeeff00112233445566778899";
    private static readonly Guid Nate = Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f");
    private static readonly Guid Kim = Guid.Parse("11111111222233334444555555555555");
    private static readonly Guid NewUser = Guid.Parse("99999999222233334444555555555555");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-pt-" + Guid.NewGuid().ToString("N"));

    public PageTokenTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static UserDto User(Guid id, string name, bool admin = false, bool disabled = false, int sessions = 0) =>
        new() { Id = id, Name = name, Policy = new UserPolicy { IsAdministrator = admin, IsDisabled = disabled, MaxActiveSessions = sessions } };

    private static string MeJson(Guid id, string name, bool admin = false) =>
        "{\"Id\":\"" + id.ToString("N") + "\",\"Name\":\"" + name + "\",\"Policy\":{\"IsAdministrator\":" + (admin ? "true" : "false") + ",\"IsDisabled\":false}}";

    private static string LoginJson(Guid id, string name, bool admin = false) =>
        $$"""{"User":{{MeJson(id, name, admin)}},"AccessToken":"{{JellyfinToken}}","ServerId":"s"}""";

    private sealed class Rig
    {
        public Rig(IPageTokenStore? store = null, ManualClock? clock = null)
        {
            Clock = clock ?? new ManualClock();
            Store = store ?? new MemoryPageTokenStore();
            Jellyfin.Users = [User(Nate, "nate", admin: true), User(Kim, "kim", sessions: 2)];
            Users = new RequestUsers(Jellyfin, [], true, new ListLogger<RequestUsers>(), Clock);
            Guard = new LoginGuard(Clock, new ListLogger<LoginGuard>(), Users.LockoutLimit);
            Tokens = new PageTokens(Store, Users, TokensLogger, Clock);
            Auth = new UserAuth(new HttpClient(Handler) { BaseAddress = new Uri("http://jellyfin.test:8096/") }, Users, Guard, Tokens, AuthLogger, Clock, "0.3.0-test");
            Handler.On("POST /Users/AuthenticateByName", request =>
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"nate\"", StringComparison.Ordinal)
                    ? AuthHandler.Reply(HttpStatusCode.OK, LoginJson(Nate, "nate", admin: true))
                    : AuthHandler.Reply(HttpStatusCode.OK, LoginJson(Kim, "kim")));
            Handler.On("POST /Sessions/Logout", HttpStatusCode.NoContent);
            Handler.On("GET /Users/Me", HttpStatusCode.OK, MeJson(Kim, "kim"));
            Users.RefreshAsync().GetAwaiter().GetResult();
        }

        public AuthHandler Handler { get; } = new();

        public ManualClock Clock { get; }

        public UsersClient Jellyfin { get; } = new();

        public RequestUsers Users { get; }

        public LoginGuard Guard { get; }

        public IPageTokenStore Store { get; }

        public ListLogger<PageTokens> TokensLogger { get; } = new();

        public ListLogger<UserAuth> AuthLogger { get; } = new();

        public PageTokens Tokens { get; }

        public UserAuth Auth { get; }

        public async Task<string> SignInAsync(string name = "kim", int ip = 1)
        {
            var result = await Auth.LoginAsync(name, Password, DeviceId, Ip(ip));
            Assert.True(result.Ok, result.Error?.Code);
            return result.Value!.Token;
        }

        public Task<AuthOutcome<RequestUser>> CheckAsync(string token, int ip = 1) => Auth.ValidateAsync("Bearer " + token, Ip(ip));

        public static IPAddress Ip(int n) => IPAddress.Parse($"10.1.{n / 250}.{n % 250 + 1}");
    }

    [Fact]
    public async Task PT01_a_page_token_is_checked_by_encore_alone_with_the_name_and_admin_flag_from_get_users()
    {
        var rig = new Rig();
        var token = await rig.SignInAsync();
        var before = rig.Handler.Calls.Count;

        var result = await rig.CheckAsync(token);

        Assert.True(result.Ok);
        Assert.Equal(new RequestUser(Kim, "kim", false, false), result.Value);
        Assert.Equal(before, rig.Handler.Calls.Count);
        Assert.Equal(1, rig.Handler.CountOf("POST /Sessions/Logout"));

        // A rename or a new admin flag in Jellyfin shows on the next GET /Users.
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true), User(Kim, "Kimberly", admin: true)];
        await rig.Users.RefreshAsync();
        Assert.Equal(new RequestUser(Kim, "Kimberly", true, false), (await rig.CheckAsync(token)).Value);
    }

    [Fact]
    public async Task PT02_quick_connect_also_logs_the_jellyfin_session_out_and_returns_a_working_page_token()
    {
        var rig = new Rig();
        rig.Handler.On("POST /QuickConnect/Initiate", HttpStatusCode.OK, """{"Authenticated":false,"Secret":"s3cretvalue","Code":"123456"}""");
        rig.Handler.On("GET /QuickConnect/Connect?secret=s3cretvalue", HttpStatusCode.OK, """{"Authenticated":true,"Secret":"s3cretvalue","Code":"123456"}""");
        rig.Handler.On("POST /Users/AuthenticateWithQuickConnect", HttpStatusCode.OK, LoginJson(Kim, "kim"));
        await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(1));

        var poll = await rig.Auth.QuickConnectPollAsync("s3cretvalue", DeviceId, Rig.Ip(1));

        var token = poll.Value!.Login!.Token;
        Assert.Matches("^enc1_[A-Za-z0-9_-]{43}$", token);
        var logout = Assert.Single(rig.Handler.Calls, c => c.PathAndQuery == "/Sessions/Logout");
        Assert.Equal($"MediaBrowser Token=\"{JellyfinToken}\"", logout.Authorization);
        Assert.True((await rig.CheckAsync(token)).Ok);
    }

    [Fact]
    public async Task PT03_a_token_expires_after_30_days_unused_and_after_90_days_in_all()
    {
        var rig = new Rig();
        var idle = await rig.SignInAsync();
        var busy = await rig.SignInAsync();
        var store = (MemoryPageTokenStore)rig.Store;

        // Used every 20 days: never idle for 30, but gone after 90 in all.
        for (var day = 20; day <= 80; day += 20)
        {
            rig.Clock.Advance(TimeSpan.FromDays(20));
            await rig.Users.RefreshAsync();
            Assert.True((await rig.CheckAsync(busy)).Ok, "day " + day);
        }

        Assert.Equal(401, (await rig.CheckAsync(idle)).Error!.Status);
        Assert.False(store.Rows.ContainsKey(PageTokens.Hash(idle)));

        rig.Clock.Advance(TimeSpan.FromDays(11));
        await rig.Users.RefreshAsync();
        var old = await rig.CheckAsync(busy);
        Assert.Equal(401, old.Error!.Status);
        Assert.Equal("invalid_token", old.Error.Code);
        Assert.Empty(store.Rows);
        Assert.Contains(rig.TokensLogger.Named("auth.page_tokens_revoked"), e => (string)e.Fields["Reason"]! == "expired");
    }

    [Fact]
    public async Task PT03b_last_use_is_written_at_most_once_a_minute()
    {
        var rig = new Rig();
        var token = await rig.SignInAsync();
        var store = (MemoryPageTokenStore)rig.Store;
        var made = store.Rows.Values.Single().LastUsedUtc;

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        await rig.CheckAsync(token);
        Assert.Equal(made, store.Rows.Values.Single().LastUsedUtc);

        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await rig.CheckAsync(token);
        Assert.Equal(made + TimeSpan.FromSeconds(61), store.Rows.Values.Single().LastUsedUtc);
    }

    [Fact]
    public async Task PT04_a_disabled_user_loses_every_page_token_on_the_next_refresh_or_check()
    {
        var rig = new Rig();
        var first = await rig.SignInAsync();
        var second = await rig.SignInAsync();
        var nate = await rig.SignInAsync("nate", 2);
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true), User(Kim, "kim", disabled: true)];

        // Within 10 minutes of the last GET /Users the token still works; the refresh removes it.
        await rig.Users.RefreshAsync();
        var store = (MemoryPageTokenStore)rig.Store;
        Assert.Single(store.Rows);
        Assert.Equal(401, (await rig.CheckAsync(first)).Error!.Status);
        Assert.True((await rig.CheckAsync(nate)).Ok);

        // Checked after more than 10 minutes without a refresh: Encore asks Jellyfin first.
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true), User(Kim, "kim")];
        await rig.Users.RefreshAsync();
        var third = await rig.SignInAsync();
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true), User(Kim, "kim", disabled: true)];
        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        var calls = rig.Jellyfin.Calls;
        var result = await rig.CheckAsync(third);
        Assert.Equal(calls + 1, rig.Jellyfin.Calls);
        Assert.False(result.Ok);
        Assert.Equal(403, result.Error!.Status);
        Assert.Equal("user_disabled", result.Error.Code);
        Assert.False(store.Rows.ContainsKey(PageTokens.Hash(third)));
        Assert.Equal(401, (await rig.CheckAsync(second)).Error!.Status);
    }

    [Fact]
    public async Task PT04b_a_disabled_user_found_at_check_time_is_403_and_the_tokens_go()
    {
        // Only the account left in Jellyfin is the disabled one; the check refreshes (data over 10 minutes old) and refuses.
        var rig = new Rig();
        var token = await rig.SignInAsync();
        rig.Jellyfin.Users = [User(Kim, "kim", disabled: true)];
        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        var store = (MemoryPageTokenStore)rig.Store;

        // The refresh during the check removes the token, and the answer says the account is disabled.
        var result = await rig.CheckAsync(token);

        Assert.Equal(403, result.Error!.Status);
        Assert.Equal("user_disabled", result.Error.Code);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task PT05_a_deleted_user_loses_every_page_token()
    {
        var rig = new Rig();
        var token = await rig.SignInAsync();
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true)];
        await rig.Users.RefreshAsync();

        var result = await rig.CheckAsync(token);

        Assert.Equal(401, result.Error!.Status);
        Assert.Equal("invalid_token", result.Error.Code);
        Assert.Empty(((MemoryPageTokenStore)rig.Store).Rows);
    }

    [Fact]
    public async Task PT05b_a_deleted_user_seen_only_at_check_time_loses_the_token_too()
    {
        // No refresh since the deletion: the check asks Jellyfin (data over 10 minutes old) and finds the user gone.
        var rig = new Rig();
        var token = await rig.SignInAsync();
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true)];
        rig.Clock.Advance(TimeSpan.FromMinutes(11));

        var result = await rig.CheckAsync(token);

        Assert.Equal(401, result.Error!.Status);
        Assert.Empty(((MemoryPageTokenStore)rig.Store).Rows);
    }

    [Fact]
    public async Task PT06_a_user_made_after_the_last_get_users_is_looked_up_before_being_called_deleted()
    {
        var rig = new Rig();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.OK, LoginJson(NewUser, "newbie"));
        var token = await rig.SignInAsync("newbie");
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true), User(Kim, "kim"), User(NewUser, "newbie")];

        var result = await rig.CheckAsync(token);

        Assert.True(result.Ok);
        Assert.Equal("newbie", result.Value!.Name);
    }

    [Fact]
    public async Task PT07_jellyfin_down_with_user_data_older_than_10_minutes_is_503_and_keeps_the_token()
    {
        var rig = new Rig();
        var token = await rig.SignInAsync();
        rig.Jellyfin.Down = true;
        rig.Clock.Advance(TimeSpan.FromMinutes(11));

        var result = await rig.CheckAsync(token);

        Assert.Equal(503, result.Error!.Status);
        Assert.Equal("jellyfin_unavailable", result.Error.Code);
        Assert.Single(((MemoryPageTokenStore)rig.Store).Rows);

        // A second check within 30 s does not ask Jellyfin again.
        var calls = rig.Jellyfin.Calls;
        await rig.CheckAsync(token);
        Assert.Equal(calls, rig.Jellyfin.Calls);

        rig.Jellyfin.Down = false;
        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await rig.CheckAsync(token)).Ok);
    }

    [Fact]
    public async Task PT08_logout_deletes_the_page_token_without_calling_jellyfin()
    {
        var rig = new Rig();
        var token = await rig.SignInAsync();
        var before = rig.Handler.Calls.Count;

        var result = await rig.Auth.LogoutAsync("Bearer " + token, Rig.Ip(1));

        Assert.True(result.Ok);
        Assert.Equal(before, rig.Handler.Calls.Count);
        Assert.Empty(((MemoryPageTokenStore)rig.Store).Rows);
        Assert.Equal(401, (await rig.CheckAsync(token)).Error!.Status);
        Assert.True((await rig.Auth.LogoutAsync("Bearer " + token, Rig.Ip(1))).Ok);
    }

    [Fact]
    public async Task PT09_jellyfin_at_its_session_limit_is_409_too_many_sessions_and_no_failed_password()
    {
        var rig = new Rig();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Forbidden);

        for (var i = 0; i < 12; i++)
        {
            var refused = await rig.Auth.LoginAsync("kim", Password, DeviceId, Rig.Ip(1));
            Assert.Equal(409, refused.Error!.Status);
            Assert.Equal("too_many_sessions", refused.Error.Code);
        }

        Assert.Equal(0, rig.Handler.CountOf("POST /Sessions/Logout"));
        Assert.Empty(((MemoryPageTokenStore)rig.Store).Rows);

        // Quick Connect skips Jellyfin's disabled check, so its 403 is always the session limit.
        rig.Handler.On("POST /QuickConnect/Initiate", HttpStatusCode.OK, """{"Authenticated":false,"Secret":"s3cretvalue","Code":"123456"}""");
        rig.Handler.On("GET /QuickConnect/Connect?secret=s3cretvalue", HttpStatusCode.OK, """{"Authenticated":true,"Secret":"s3cretvalue","Code":"123456"}""");
        rig.Handler.On("POST /Users/AuthenticateWithQuickConnect", HttpStatusCode.Forbidden);
        await rig.Auth.QuickConnectInitiateAsync(DeviceId, Rig.Ip(1));
        var quick = await rig.Auth.QuickConnectPollAsync("s3cretvalue", DeviceId, Rig.Ip(1));
        Assert.Equal(409, quick.Error!.Status);
        Assert.Equal("too_many_sessions", quick.Error.Code);
    }

    [Fact]
    public async Task PT09b_a_403_for_a_user_without_a_session_limit_or_a_disabled_one_stays_a_failed_password()
    {
        var rig = new Rig();
        rig.Jellyfin.Users = [User(Nate, "nate", admin: true), User(Kim, "kim", disabled: true, sessions: 2)];
        await rig.Users.RefreshAsync();
        rig.Handler.On("POST /Users/AuthenticateByName", HttpStatusCode.Forbidden);

        Assert.Equal(401, (await rig.Auth.LoginAsync("kim", Password, DeviceId, Rig.Ip(1))).Error!.Status);
        Assert.Equal(401, (await rig.Auth.LoginAsync("nate", Password, DeviceId, Rig.Ip(1))).Error!.Status);
        Assert.Equal(401, (await rig.Auth.LoginAsync("nobody", Password, DeviceId, Rig.Ip(1))).Error!.Status);
        for (var i = 0; i < 3; i++)
        {
            await rig.Auth.LoginAsync("nobody", Password, DeviceId, Rig.Ip(1));
        }

        Assert.Equal(429, (await rig.Auth.LoginAsync("nobody", Password, DeviceId, Rig.Ip(1))).Error!.Status);
    }

    [Fact]
    public async Task PT10_unknown_page_tokens_count_as_guesses_and_a_bad_shape_is_malformed()
    {
        var rig = new Rig();
        for (var i = 0; i < 30; i++)
        {
            var guess = "enc1_" + i.ToString("D43", CultureInfo.InvariantCulture);
            Assert.Equal(401, (await rig.CheckAsync(guess)).Error!.Status);
        }

        Assert.Equal(429, (await rig.CheckAsync("enc1_" + new string('A', 43))).Error!.Status);
        Assert.DoesNotContain(rig.Handler.Calls, c => c.PathAndQuery == "/Users/Me");

        var other = new Rig();
        Assert.Equal("malformed_token", (await other.CheckAsync("enc1_short")).Error!.Code);
        Assert.Equal("malformed_token", (await other.CheckAsync("enc1_" + new string('A', 43) + "!")).Error!.Code);
    }

    [Fact]
    public async Task PT11_admin_revoke_for_one_user_and_for_everyone()
    {
        var rig = new Rig();
        var kim1 = await rig.SignInAsync();
        var kim2 = await rig.SignInAsync();
        var nate = await rig.SignInAsync("nate", 2);

        Assert.Equal(2, rig.Tokens.RevokeAll(Kim));
        Assert.Equal(401, (await rig.CheckAsync(kim1)).Error!.Status);
        Assert.Equal(401, (await rig.CheckAsync(kim2)).Error!.Status);
        Assert.True((await rig.CheckAsync(nate)).Ok);

        await rig.SignInAsync();
        Assert.Equal(2, rig.Tokens.RevokeAll(null));
        Assert.Equal(401, (await rig.CheckAsync(nate)).Error!.Status);
        var lines = rig.TokensLogger.Named("auth.page_tokens_revoked").Select(e => (string)e.Fields["Reason"]!).ToList();
        Assert.Contains("admin_user", lines);
        Assert.Contains("admin_all", lines);
    }

    [Fact]
    public async Task PT12_a_page_token_survives_an_encore_restart_in_the_sqlite_store()
    {
        var path = Path.Combine(_dir, "encore.db");
        var clock = new ManualClock();
        string token;
        using (var main = EncoreStore.Open(path, clock))
        using (var store = RequestStore.Open(path))
        {
            var rig = new Rig(store, clock);
            token = await rig.SignInAsync();
            Assert.Equal(1, store.CountPageTokens());
            Assert.NotNull(store.GetPageToken(PageTokens.Hash(token)));
        }

        using (var main = EncoreStore.Open(path, clock))
        using (var store = RequestStore.Open(path))
        {
            var rig = new Rig(store, clock);
            var result = await rig.CheckAsync(token);
            Assert.True(result.Ok);
            Assert.Equal("kim", result.Value!.Name);

            // Nothing in the file holds the token itself.
            using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT token_hash || user_id || created_utc || last_used_utc FROM page_tokens";
            var text = (string)cmd.ExecuteScalar()!;
            Assert.DoesNotContain(token[5..], text, StringComparison.Ordinal);
            Assert.Contains(PageTokens.Hash(token), text, StringComparison.Ordinal);

            Assert.Equal(1, rig.Tokens.RevokeAll(Kim));
            Assert.Equal(0, store.CountPageTokens());
        }
    }

    [Fact]
    public void PT13_the_sqlite_store_deletes_expired_and_other_users_tokens()
    {
        var path = Path.Combine(_dir, "encore.db");
        var at = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        using var main = EncoreStore.Open(path, new ManualClock());
        using var store = RequestStore.Open(path);
        store.InsertPageToken("A", "u1", at);
        store.InsertPageToken("B", "u1", at.AddDays(10));
        store.InsertPageToken("C", "u2", at.AddDays(10));
        store.InsertPageToken("D", "u3", at.AddDays(10));
        store.TouchPageToken("B", at.AddDays(20));

        Assert.Equal(at.AddDays(20), store.GetPageToken("B")!.LastUsedUtc);
        Assert.Equal(1, store.DeleteExpiredPageTokens(at.AddDays(5), at.AddDays(-1)));
        Assert.Equal(1, store.DeletePageTokensExcept(new HashSet<string> { "u1", "u2" }));
        Assert.Equal(1, store.DeletePageTokens("u2"));
        Assert.True(store.DeletePageToken("B"));
        Assert.False(store.DeletePageToken("B"));
        Assert.Equal(0, store.CountPageTokens());
    }
}

/// <summary>T114 through the HTTP host: the page token on the routes, too_many_sessions, the admin revoke route.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PageTokenApiTests
{
    private static async Task<string> SignInAsync(ApiRig rig)
    {
        using var login = await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = ApiRig.Password, deviceId = ApiRig.DeviceId });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await ApiRig.JsonOf(login)).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task PA01_the_page_token_works_for_me_search_and_requests_and_jellyfin_saw_one_logout_with_the_sign_in_token()
    {
        using var rig = new ApiRig();

        var token = await SignInAsync(rig);

        Assert.Matches("^enc1_[A-Za-z0-9_-]{43}$", token);
        var logout = Assert.Single(rig.Jellyfin.Calls, c => c.PathAndQuery == "/Sessions/Logout");
        Assert.Equal($"MediaBrowser Token=\"{ApiRig.KimToken}\"", logout.Authorization);
        var meCalls = rig.Jellyfin.CountOf("GET /Users/Me");

        using var me = await rig.GetAsync("/api/v1/me", bearer: token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await ApiRig.JsonOf(me);
        Assert.Equal("kim", body.GetProperty("name").GetString());
        Assert.False(body.GetProperty("isAdmin").GetBoolean());
        Assert.True(body.GetProperty("canRequest").GetBoolean());

        using var search = await rig.GetAsync("/api/v1/search?q=daft", bearer: token);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        using var requests = await rig.GetAsync("/api/v1/requests", bearer: token);
        Assert.Equal(HttpStatusCode.OK, requests.StatusCode);

        // Never sent to Jellyfin.
        Assert.Equal(meCalls, rig.Jellyfin.CountOf("GET /Users/Me"));
        Assert.DoesNotContain(rig.Jellyfin.Calls, c => c.Authorization?.Contains(token, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task PA02_a_jellyfin_token_still_works_for_the_web_search_script()
    {
        using var rig = new ApiRig();

        using var me = await rig.GetAsync("/api/v1/me", bearer: ApiRig.KimToken);
        using var search = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(0, rig.Jellyfin.CountOf("POST /Sessions/Logout"));
        Assert.Equal(0, rig.PageTokens.RevokeAll(null));
    }

    [Fact]
    public async Task PA03_jellyfin_at_the_session_limit_is_409_too_many_sessions_with_the_plain_text()
    {
        using var rig = new ApiRig();
        rig.Fake.Users =
        [
            new UserDto { Id = ApiRig.Nate, Name = "nate", Policy = new UserPolicy { IsAdministrator = true } },
            new UserDto { Id = ApiRig.Kim, Name = "kim", Policy = new UserPolicy { MaxActiveSessions = 2 } },
        ];
        await rig.Users.RefreshAsync();
        rig.Jellyfin.On("POST /Users/AuthenticateByName", HttpStatusCode.Forbidden);

        using var refused = await rig.PostAsync("/api/v1/auth/login", new { username = "kim", password = ApiRig.Password, deviceId = ApiRig.DeviceId });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await ApiRig.JsonOf(refused);
        Assert.Equal("too_many_sessions", body.GetProperty("error").GetString());
        Assert.Equal(
            "Your Jellyfin account is already signed in on the maximum number of devices. Sign out on one of them, or use Requests inside Jellyfin web.",
            body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PA04_admins_revoke_page_tokens_for_one_user_or_everyone()
    {
        using var rig = new ApiRig();
        var kim = await SignInAsync(rig);

        using var notAdmin = await rig.SendAsync(HttpMethod.Delete, "/api/v1/admin/sessions", bearer: kim);
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        Assert.Equal("admin_only", (await ApiRig.JsonOf(notAdmin)).GetProperty("error").GetString());

        using var bad = await rig.SendAsync(HttpMethod.Delete, "/api/v1/admin/sessions?userId=nope", bearer: ApiRig.NateToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var other = await rig.SendAsync(HttpMethod.Delete, "/api/v1/admin/sessions?userId=" + ApiRig.Lee.ToString("N"), bearer: ApiRig.NateToken);
        Assert.Equal(0, (await ApiRig.JsonOf(other)).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/api/v1/me", bearer: kim)).StatusCode);

        using var one = await rig.SendAsync(HttpMethod.Delete, "/api/v1/admin/sessions?userId=" + ApiRig.Kim.ToString("N"), bearer: ApiRig.NateToken);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(1, (await ApiRig.JsonOf(one)).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.GetAsync("/api/v1/me", bearer: kim)).StatusCode);

        var again1 = await SignInAsync(rig);
        var again2 = await SignInAsync(rig);
        using var all = await rig.SendAsync(HttpMethod.Delete, "/api/v1/admin/sessions", bearer: ApiRig.NateToken);
        Assert.Equal(2, (await ApiRig.JsonOf(all)).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.GetAsync("/api/v1/me", bearer: again1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.GetAsync("/api/v1/me", bearer: again2)).StatusCode);

        // The Jellyfin token the admin used is not touched.
        Assert.Equal(HttpStatusCode.OK, (await rig.GetAsync("/api/v1/me", bearer: ApiRig.NateToken)).StatusCode);
        var lines = rig.Logs.Entries.Where(e => e.EventId.Name == "auth.page_tokens_revoked").ToList();
        Assert.Contains(lines, e => (string)e.Fields["Reason"]! == "admin_all" && (int)e.Fields["Count"]! == 2);
        Assert.DoesNotContain(lines, e => e.Message.Contains("kim", StringComparison.OrdinalIgnoreCase) || e.Message.Contains(ApiRig.Kim.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task PA05_a_disabled_or_deleted_user_page_token_stops_on_the_next_refresh()
    {
        using var rig = new ApiRig();
        var kim = await SignInAsync(rig);
        rig.Fake.Users =
        [
            new UserDto { Id = ApiRig.Nate, Name = "nate", Policy = new UserPolicy { IsAdministrator = true } },
            new UserDto { Id = ApiRig.Kim, Name = "kim", Policy = new UserPolicy { IsDisabled = true } },
        ];
        await rig.Users.RefreshAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.GetAsync("/api/v1/me", bearer: kim)).StatusCode);

        rig.Fake.Users = [new UserDto { Id = ApiRig.Nate, Name = "nate", Policy = new UserPolicy { IsAdministrator = true } }, new UserDto { Id = ApiRig.Kim, Name = "kim", Policy = new UserPolicy() }];
        await rig.Users.RefreshAsync();
        var kim2 = await SignInAsync(rig);
        rig.Fake.Users = [new UserDto { Id = ApiRig.Nate, Name = "nate", Policy = new UserPolicy { IsAdministrator = true } }];
        await rig.Users.RefreshAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.GetAsync("/api/v1/me", bearer: kim2)).StatusCode);
    }
}
