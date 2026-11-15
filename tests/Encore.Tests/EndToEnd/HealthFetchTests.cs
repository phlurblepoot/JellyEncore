using System.Text.Json;
using Encore.Config;
using Encore.Fetch;
using Encore.Health;
using Encore.Orchestration;
using Encore.Watch;

namespace Encore.Tests.EndToEnd;

/// <summary>/health sections slskd and fetch (v0.2 design section 12, cases HE01-HE10).</summary>
public class HealthFetchTests
{
    private const string SlskdKey = "slskd-key-distinct-7731";
    private const string JellyfinKey = "jf-key-distinct-4410";
    private static readonly DateTimeOffset Started = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Started + TimeSpan.FromMinutes(20);

    private static EncoreOptions Options(FetchMode mode) => new()
    {
        JellyfinUrl = new Uri("http://jellyfin.test:8096"),
        JellyfinApiKey = JellyfinKey,
        AllowedUsers = ["nathan"],
        DataDir = "/data",
        DeviceId = "encore-0123456789ab",
        DryRun = mode != FetchMode.Live,
        FetchMode = mode,
        SlskdUrl = mode == FetchMode.Off ? null : new Uri("http://gluetun-slskd:5030"),
        SlskdApiKey = mode == FetchMode.Off ? null : SlskdKey,
    };

    private static OrchestratorStatus Loop(bool storeOk = true) => new()
    {
        StartedUtc = Started,
        LastHeartbeatUtc = Now,
        AllowListConfigured = ["nathan"],
        AllowListResolved = new Dictionary<string, string> { ["nathan"] = "4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f" },
        AllowListResolvedUtc = Now,
        StoreOk = storeOk,
        PassedOver = 4,
        Skipped = 2,
    };

    private static WatcherStatus Socket() => new() { State = SocketState.Subscribed, Since = Started, LastSessionsAt = Now };

    private static FetchStatus Healthy(FetchMode mode = FetchMode.Live) => new(
        mode, true, null, 0, 1, 1, 14, 3, 1, 0, 5, 30, true, true, Now, null, true, "Music", 1_759_218_604_441, 0, Now, null);

    private static FetchHealthInput Input(FetchStatus status, bool ownedLoaded = true, DateTimeOffset? deezerFailed = null) =>
        new(status, 3, ownedLoaded, 1412, Now, deezerFailed);

    private static (int Code, HealthReport Report) Eval(EncoreOptions options, OrchestratorStatus loop, FetchHealthInput? fetch) =>
        HealthEvaluator.Evaluate(new HealthInput(options, "0.2.0", Now, Socket(), loop, "/data/encore.db", 2, fetch));

    [Fact]
    public void HE01_Mode_off_shows_slskd_not_configured_fetch_off_and_no_warnings()
    {
        var (code, report) = Eval(Options(FetchMode.Off), Loop(), null);

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.False(report.Slskd.Configured);
        Assert.Null(report.Slskd.Url);
        Assert.Equal("off", report.Fetch.Mode);
        Assert.False(report.Fetch.Available);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void HE02_Live_with_slskd_unreachable_is_ok_with_a_warning()
    {
        var status = Healthy() with { Available = false, UnavailableReason = "slskd_unreachable", SlskdReachable = false, SlskdLoggedIn = false, SlskdLastError = "slskd down" };
        var (code, report) = Eval(Options(FetchMode.Live), Loop(), Input(status));

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.False(report.Slskd.Reachable);
        Assert.Equal(["slskd: unreachable"], report.Warnings);
        Assert.Equal("slskd_unreachable", report.Fetch.UnavailableReason);
    }

    [Fact]
    public void HE03_Not_logged_in_is_a_warning()
    {
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy() with { SlskdLoggedIn = false }));

        Assert.Equal("ok", report.Status);
        Assert.Equal(["slskd: not logged in"], report.Warnings);
    }

    [Fact]
    public void HE04_Radio_dir_not_in_a_library_is_a_warning()
    {
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy() with { RadioDirInLibrary = false, RadioLibrary = null }));

        Assert.Equal("ok", report.Status);
        Assert.Equal(["music-radio is not in a Jellyfin music library"], report.Warnings);
        Assert.False(report.Fetch.RadioDirInLibrary);
    }

    [Fact]
    public void HE05b_Unreadable_free_space_is_a_warning_after_the_start_grace()
    {
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy() with { FreeBytes = null }));

        Assert.Equal("ok", report.Status);
        Assert.Equal(["music-radio free space unknown (disk guard off)"], report.Warnings);
    }

    [Fact]
    public void HE05_Free_space_below_the_floor_is_a_warning()
    {
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy() with { FreeBytes = 19_000_000_000 }));

        Assert.Equal("ok", report.Status);
        Assert.Equal(["music-radio free space below 20 GB"], report.Warnings);
    }

    [Fact]
    public void HE06_Copies_left_behind_are_a_warning()
    {
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy() with { SourceLeftBehind = 2 }));

        Assert.Equal("ok", report.Status);
        Assert.Equal(["slskd copies left behind: 2"], report.Warnings);
        Assert.Equal(2, report.Fetch.SourceLeftBehind);
    }

    [Fact]
    public void An_owned_index_not_loaded_after_five_minutes_and_a_recent_deezer_error_are_warnings()
    {
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy(), ownedLoaded: false, deezerFailed: Now - TimeSpan.FromMinutes(2)));

        Assert.Equal("ok", report.Status);
        Assert.Equal(["owned index not loaded", "deezer: failing"], report.Warnings);

        var (_, old) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy(), deezerFailed: Now - TimeSpan.FromMinutes(30)));
        Assert.Empty(old.Warnings);
    }

    [Fact]
    public void HE07_Counters_equal_the_fetch_status_plus_the_orchestrator_passed_over_and_skipped()
    {
        var status = Healthy() with { Queued = 2, InFlight = 1, Ingesting = 1, Ready = 14, Failed = 3, Cancelled = 1, WouldDownload = 5, LastHour = 7 };
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(status));
        var f = report.Fetch;

        Assert.Equal(("live", 2, 1, 1, 14, 3, 1), (f.Mode, f.Queued, f.InFlight, f.Ingesting, f.Ready, f.Failed, f.Cancelled));
        Assert.Equal((4, 2, 5, 7, 30), (f.PassedOver, f.Skipped, f.WouldDownload, f.LastHour, f.HourlyCap));
        Assert.Equal(new HealthOwnedIndex(true, 1412, Now), f.OwnedIndex);
        Assert.Equal("34/220", report.Slskd.SearchLimit);
        Assert.Equal(3, report.Slskd.SearchesInWindow);
    }

    [Fact]
    public void HE08_The_body_holds_neither_key()
    {
        var status = Healthy() with { SlskdReachable = false, SlskdLastError = "slskd request failed" };
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(status));
        var json = JsonSerializer.Serialize(report, JsonSerializerOptions.Web);

        Assert.DoesNotContain(SlskdKey, json);
        Assert.DoesNotContain(JellyfinKey, json);
        Assert.Contains("\"url\":\"http://gluetun-slskd:5030\"", json);
    }

    [Fact]
    public void HE09_The_json_names_and_shape_match_the_design()
    {
        var (_, report) = Eval(Options(FetchMode.Live), Loop(), Input(Healthy() with { SlskdLoggedIn = false }));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonSerializerOptions.Web));
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.Equal(
            ["configured", "url", "reachable", "loggedIn", "lastOkUtc", "lastError", "searchesInWindow", "searchLimit"],
            root.GetProperty("slskd").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["mode", "available", "unavailableReason", "ownedIndex", "queued", "inFlight", "ingesting", "ready", "failed", "cancelled",
             "passedOver", "skipped", "wouldDownload", "lastHour", "hourlyCap", "radioDirInLibrary", "radioLibrary", "freeBytes",
             "sourceLeftBehind", "lastReadyUtc", "lastError"],
            root.GetProperty("fetch").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["loaded", "items", "refreshedUtc"],
            root.GetProperty("fetch").GetProperty("ownedIndex").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void HE10_A_broken_store_is_still_degraded_even_with_a_healthy_fetch_side()
    {
        var (code, report) = Eval(Options(FetchMode.Live), Loop(storeOk: false), Input(Healthy()));

        Assert.Equal(200, code);
        Assert.Equal("degraded", report.Status);
    }
}
