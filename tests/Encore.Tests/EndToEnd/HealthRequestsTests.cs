using System.Text.Json;
using Encore.Config;
using Encore.Fetch;
using Encore.Health;
using Encore.Lidarr;
using Encore.Orchestration;
using Encore.Requests.Auth;
using Encore.Store;
using Encore.Tests.Requests;
using Encore.Tests.Trigger;
using Encore.Watch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.EndToEnd;

/// <summary>/health requests section and warnings (v0.3 design section 12, cases HR01-HR13).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class HealthRequestsTests
{
    private const string LidarrKey = "LIDARRKEY0123456789abcd";
    private const string LidarrUrl = "http://127.0.0.1:1";
    private static readonly DateTimeOffset Started = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Started + TimeSpan.FromMinutes(20);

    private static EncoreOptions Options(bool requests) => new()
    {
        JellyfinUrl = new Uri("http://jellyfin.test:8096"),
        JellyfinApiKey = "jf-key-distinct-4410",
        AllowedUsers = ["nathan"],
        DataDir = "/data",
        DeviceId = "encore-0123456789ab",
        DryRun = false,
        RequestsEnabled = requests,
    };

    private static OrchestratorStatus Loop() => new()
    {
        StartedUtc = Started,
        LastHeartbeatUtc = Now,
        AllowListConfigured = ["nathan"],
        AllowListResolved = new Dictionary<string, string> { ["nathan"] = "4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f" },
        AllowListResolvedUtc = Now,
        StoreOk = true,
    };

    private static WatcherStatus Socket() => new() { State = SocketState.Subscribed, Since = Started, LastSessionsAt = Now };

    private static FetchHealthInput LiveFetch() => new(
        new FetchStatus(FetchMode.Live, true, null, 0, 1, 1, 14, 3, 1, 0, 5, 30, true, true, Now, null, true, "Music", 1_759_218_604_441, 0, Now, null),
        3, true, 1412, Now, null);

    private static RequestsHealthInput Req(LidarrState? lidarr, int waiting = 0) => new(
        new Dictionary<string, int> { ["searching"] = 1, ["importing"] = 2 },
        new GuardSnapshot(1, 2, 3),
        (4, 3, 1),
        lidarr, 5, 1, waiting);

    private static (int Code, HealthReport Report) Eval(bool requests, FetchHealthInput? fetch, RequestsHealthInput? input) =>
        HealthEvaluator.Evaluate(new HealthInput(Options(requests), "0.3.0", Now, Socket(), Loop(), "/data/encore.db", 3, fetch, input));

    private static string Json(HealthReport report) => JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void HR01_Requests_off_is_just_enabled_false_and_adds_no_warning()
    {
        var (code, report) = Eval(false, null, null);

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.Empty(report.Warnings);
        using var doc = JsonDocument.Parse(Json(report));
        Assert.Equal("{\"enabled\":false}", doc.RootElement.GetProperty("requests").GetRawText());
    }

    [Fact]
    public void HR02_Requests_on_shows_counts_states_and_lidarr()
    {
        var lidarr = new LidarrState(true, true, "3.1.6.5079", Now, null, false);
        var (_, report) = Eval(true, LiveFetch(), Req(lidarr));

        using var doc = JsonDocument.Parse(Json(report));
        var r = doc.RootElement.GetProperty("requests");
        Assert.True(r.GetProperty("enabled").GetBoolean());
        Assert.Equal("available", r.GetProperty("songs").GetString());
        Assert.Equal("available", r.GetProperty("albums").GetString());
        Assert.Equal(1, r.GetProperty("active").GetProperty("searching").GetInt32());
        Assert.Equal(2, r.GetProperty("active").GetProperty("importing").GetInt32());
        Assert.Equal(0, r.GetProperty("active").GetProperty("queued").GetInt32());
        Assert.Equal(1, r.GetProperty("auth").GetProperty("blockedIps").GetInt32());
        Assert.Equal(2, r.GetProperty("auth").GetProperty("lockedNames").GetInt32());
        Assert.Equal(3, r.GetProperty("auth").GetProperty("failedLoginsLastHour").GetInt32());
        Assert.Equal(4, r.GetProperty("today").GetProperty("created").GetInt32());
        Assert.Equal(3, r.GetProperty("today").GetProperty("ready").GetInt32());
        Assert.Equal(1, r.GetProperty("today").GetProperty("failed").GetInt32());
        Assert.Equal("3.1.6.5079", r.GetProperty("lidarr").GetProperty("version").GetString());
        Assert.True(r.GetProperty("lidarr").GetProperty("reachable").GetBoolean());
        Assert.Equal(5, r.GetProperty("notify").GetProperty("sent").GetInt32());
        Assert.Equal(1, r.GetProperty("notify").GetProperty("failed").GetInt32());
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void HR03_Lidarr_down_is_a_warning_and_the_status_stays_ok()
    {
        var lidarr = new LidarrState(true, false, null, null, "connection refused", false);
        var (code, report) = Eval(true, LiveFetch(), Req(lidarr));

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.Equal(["lidarr: unreachable"], report.Warnings);
        Assert.Equal("lidarr_unreachable", report.Requests!.Albums);
        Assert.Equal("available", report.Requests.Songs);
    }

    [Fact]
    public void HR04_Lidarr_key_rejected_is_a_warning_and_the_status_stays_ok()
    {
        var lidarr = new LidarrState(true, true, null, null, "key rejected (HTTP 401)", true);
        var (_, report) = Eval(true, LiveFetch(), Req(lidarr));

        Assert.Equal("ok", report.Status);
        Assert.Equal(["lidarr: key rejected"], report.Warnings);
        Assert.Equal("lidarr_key_rejected", report.Requests!.Albums);
    }

    [Fact]
    public void HR05_Lidarr_not_configured_means_albums_off_and_no_warning()
    {
        var (_, report) = Eval(true, LiveFetch(), Req(null));

        Assert.Equal("lidarr_not_configured", report.Requests!.Albums);
        Assert.False(report.Requests.Lidarr!.Configured);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void HR06_Albums_waiting_for_Jellyfin_are_counted_in_a_warning()
    {
        var lidarr = new LidarrState(true, true, "3.1.6.5079", Now, null, false);
        var (_, report) = Eval(true, LiveFetch(), Req(lidarr, waiting: 2));

        Assert.Equal(["requests: 2 album(s) in Lidarr but not in Jellyfin"], report.Warnings);
    }

    [Fact]
    public void HR07_Fetch_mode_off_says_songs_fetch_off_with_a_warning()
    {
        var lidarr = new LidarrState(true, true, "3.1.6.5079", Now, null, false);
        var (_, report) = Eval(true, null, Req(lidarr));

        Assert.Equal("fetch_off", report.Requests!.Songs);
        Assert.Equal(["requests: song fetching is off"], report.Warnings);
    }

    [Fact]
    public void HR08_Fetch_unavailable_names_the_reason_for_songs()
    {
        var fetch = LiveFetch();
        fetch = fetch with { Status = fetch.Status with { Available = false, UnavailableReason = "slskd_unreachable", SlskdReachable = false } };
        var (_, report) = Eval(true, fetch, Req(new LidarrState(true, true, "1", Now, null, false)));

        Assert.Equal("fetch_unavailable:slskd_unreachable", report.Requests!.Songs);
    }

    [Fact]
    public void HR09_The_body_holds_no_key_no_token_and_no_user_name()
    {
        var lidarr = new LidarrState(true, false, null, null, "connection refused", false);
        var json = Json(Eval(true, LiveFetch(), Req(lidarr)).Report);

        Assert.DoesNotContain("jf-key-distinct-4410", json);
        Assert.DoesNotContain(LidarrKey, json);
        Assert.DoesNotContain("Bearer", json);
    }

    [Fact]
    public void HR10_The_guard_snapshot_counts_blocks_locks_and_failures_without_names_or_ips()
    {
        var clock = new ManualTimeProvider(Now);
        var guard = new LoginGuard(clock, NullLogger<LoginGuard>.Instance);
        var ip = System.Net.IPAddress.Parse("203.0.113.9");
        for (var i = 0; i < 5; i++)
        {
            guard.RecordLoginFailure(ip, "alice");
        }

        var snap = guard.Snapshot();
        Assert.Equal(1, snap.BlockedIps);
        Assert.Equal(0, snap.LockedNames);
        Assert.Equal(5, snap.FailedLoginsLastHour);

        clock.Advance(TimeSpan.FromMinutes(61));
        var later = guard.Snapshot();
        Assert.Equal(0, later.BlockedIps);
        Assert.Equal(0, later.FailedLoginsLastHour);
    }

    [Fact]
    public void HR11_The_store_counts_active_states_and_the_last_day()
    {
        var path = Path.Combine(Path.GetTempPath(), "encore-hr-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var main = EncoreStore.Open(path, new ManualTimeProvider(Now));
            using var store = RequestStore.Open(path);
            store.Insert(new NewRequest("a0000000000000000000000000000001", "song", "u1", "kim", "A", Now, Title: "One"));
            store.Insert(new NewRequest("a0000000000000000000000000000002", "song", "u1", "kim", "B", Now, Title: "Two"));
            store.Insert(new NewRequest("a0000000000000000000000000000003", "song", "u1", "kim", "C", Now.AddDays(-3), Title: "Old"));
            store.Update("a0000000000000000000000000000002", RequestState.Ready, Now, new RequestUpdate(ReadyUtc: Now));

            var active = store.CountActiveByState();
            Assert.Equal(2, active["queued"]);
            Assert.False(active.ContainsKey("ready"));
            var today = store.CountsSince(Now - TimeSpan.FromHours(24));
            Assert.Equal(2, today.Created);
            Assert.Equal(1, today.Ready);
            Assert.Equal(0, today.Failed);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task HR12_The_real_host_serves_enabled_false_with_requests_off()
    {
        using var rig = new ApiRig(on: false);

        using var response = await rig.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("{\"enabled\":false}", doc.RootElement.GetProperty("requests").GetRawText());
    }

    [Fact]
    public async Task HR13_The_real_host_with_Lidarr_down_warns_keeps_the_status_and_shows_no_key_or_token()
    {
        using var rig = new ApiRig(new Dictionary<string, string?>
        {
            ["ENCORE_LIDARR_URL"] = LidarrUrl,
            ["ENCORE_LIDARR_API_KEY"] = LidarrKey,
        });
        await rig.Services.GetRequiredService<LidarrStatusMonitor>().CheckOnceAsync(CancellationToken.None);
        using var me = await rig.GetAsync("/api/v1/me", bearer: ApiRig.KimToken);

        using var response = await rig.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var requests = doc.RootElement.GetProperty("requests");
        Assert.True(requests.GetProperty("enabled").GetBoolean());
        Assert.Equal("lidarr_unreachable", requests.GetProperty("albums").GetString());
        Assert.True(requests.GetProperty("lidarr").GetProperty("configured").GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()), w => w == "lidarr: unreachable");
        Assert.NotEqual("stopped", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(200, (int)response.StatusCode);
        Assert.DoesNotContain(LidarrKey, body);
        Assert.DoesNotContain(ApiRig.KimToken, body);
    }
}
