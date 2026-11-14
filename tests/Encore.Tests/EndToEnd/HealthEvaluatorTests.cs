using System.Text.Json;
using Encore.Config;
using Encore.Health;
using Encore.Orchestration;
using Encore.Watch;

namespace Encore.Tests.EndToEnd;

public class HealthEvaluatorTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    private static EncoreOptions Options(bool dryRun) => new()
    {
        JellyfinUrl = new Uri("http://jellyfin.test:8096"),
        JellyfinApiKey = "k-never-shown",
        AllowedUsers = ["nathan"],
        DataDir = "/data",
        DeviceId = "encore-0123456789ab",
        DryRun = dryRun,
    };

    private static OrchestratorStatus Loop(DateTimeOffset heartbeat, bool resolved, bool storeOk = true) => new()
    {
        StartedUtc = Started,
        LastHeartbeatUtc = heartbeat,
        AllowListConfigured = ["nathan"],
        AllowListResolved = resolved ? new Dictionary<string, string> { ["nathan"] = "4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f" } : new Dictionary<string, string>(),
        AllowListResolvedUtc = resolved ? heartbeat : null,
        StoreOk = storeOk,
    };

    private static WatcherStatus Socket(SocketState state, DateTimeOffset now, DateTimeOffset? poll = null) => new()
    {
        State = state,
        Since = Started,
        LastSessionsAt = now,
        LastPollAt = poll,
    };

    private static (int Code, HealthReport Report) Eval(DateTimeOffset now, OrchestratorStatus loop, WatcherStatus socket, bool dryRun = true) =>
        HealthEvaluator.Evaluate(new HealthInput(Options(dryRun), "0.1.0", now, socket, loop, "/data/encore.db", 1));

    [Fact]
    public void Without_a_heartbeat_for_60_seconds_the_endpoint_is_503_and_stopped()
    {
        var now = Started + TimeSpan.FromMinutes(5);
        var (code, report) = Eval(now, Loop(Started, resolved: true), Socket(SocketState.Subscribed, now));

        Assert.Equal(503, code);
        Assert.Equal("stopped", report.Status);
    }

    [Fact]
    public void The_first_30_seconds_report_starting()
    {
        var now = Started + TimeSpan.FromSeconds(10);
        var (code, report) = Eval(now, Loop(now, resolved: false), Socket(SocketState.Connecting, now));

        Assert.Equal(200, code);
        Assert.Equal("starting", report.Status);
    }

    [Fact]
    public void A_subscribed_socket_with_every_name_resolved_and_an_open_store_is_ok()
    {
        var now = Started + TimeSpan.FromMinutes(5);
        var (code, report) = Eval(now, Loop(now, resolved: true), Socket(SocketState.Subscribed, now));

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.True(report.DryRun);
        Assert.Equal("subscribed", report.Socket.State);
        Assert.Equal(new[] { "nathan" }, report.AllowList.Configured);
        Assert.Equal(1, report.Store.SchemaVersion);
    }

    [Fact]
    public void Polling_that_works_counts_as_a_live_source()
    {
        var now = Started + TimeSpan.FromMinutes(5);
        var (_, report) = Eval(now, Loop(now, resolved: true), Socket(SocketState.Down, now, poll: now - TimeSpan.FromSeconds(10)));

        Assert.Equal("ok", report.Status);
    }

    [Fact]
    public void A_name_that_does_not_resolve_is_degraded()
    {
        var now = Started + TimeSpan.FromMinutes(5);
        var (code, report) = Eval(now, Loop(now, resolved: false), Socket(SocketState.Subscribed, now));

        Assert.Equal(200, code);
        Assert.Equal("degraded", report.Status);
    }

    [Fact]
    public void No_socket_and_no_poll_is_degraded()
    {
        var now = Started + TimeSpan.FromMinutes(5);
        var (_, report) = Eval(now, Loop(now, resolved: true), Socket(SocketState.Down, now));

        Assert.Equal("degraded", report.Status);
    }

    [Fact]
    public void A_store_error_is_degraded()
    {
        var now = Started + TimeSpan.FromMinutes(5);
        var (_, report) = Eval(now, Loop(now, resolved: true, storeOk: false), Socket(SocketState.Subscribed, now));

        Assert.Equal("degraded", report.Status);
        Assert.False(report.Store.Ok);
    }

    [Fact]
    public void The_json_uses_camel_case_and_shows_the_dry_run_flag_and_socket_times_without_the_key()
    {
        var now = Started + TimeSpan.FromMinutes(5);
        var (_, report) = Eval(now, Loop(now, resolved: true), Socket(SocketState.Subscribed, now), dryRun: false);

        var json = JsonSerializer.Serialize(report, JsonSerializerOptions.Web);

        Assert.Contains("\"dryRun\":false", json);
        Assert.Contains("\"lastSessionsUtc\"", json);
        Assert.Contains("\"allowList\"", json);
        Assert.DoesNotContain("k-never-shown", json);
    }
}
