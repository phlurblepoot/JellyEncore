using System.Text.Json;
using Encore.Config;
using Encore.Health;
using Encore.Orchestration;
using Encore.Retention;
using Encore.Store;
using Encore.Watch;

namespace Encore.Tests.EndToEnd;

/// <summary>/health retention section and warnings (v0.4 design section 11).</summary>
public class HealthRetentionTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Started + TimeSpan.FromMinutes(20);

    private static EncoreOptions Options(RetentionMode mode) => new()
    {
        JellyfinUrl = new Uri("http://jellyfin.test:8096"),
        JellyfinApiKey = "jf-key-distinct-4410",
        AllowedUsers = ["nathan"],
        DataDir = "/data",
        DeviceId = "encore-0123456789ab",
        RetentionMode = mode,
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

    private static TrackedFile File(long id) => new(
        id, "k" + id, "Artist", "Song " + id, "ready", "/music-radio/a.flac", null, null, false, null, null, null, null, null, 0, 0, Now, "u", null, null);

    private static PlannedRow Row(long id, string decision, string? reason = null) =>
        new(File(id), decision, reason, null, 1, 0, 0, null, null, null, null);

    private static SignalsSnapshot Signals(bool installed, bool read) =>
        new(Now, 3, new HashSet<Guid>(), [], installed, read, []);

    private static PassResult Pass(SignalsSnapshot signals, params PlannedRow[] rows) =>
        new("p1", Now - TimeSpan.FromMinutes(5), RetentionMode.DryRun, new PlannerSettings(30, 3, 3), rows, [], signals, 5);

    private static RetentionHealthInput Input(
        RetentionMode mode, PassResult? latest = null, bool shared = true, bool eventsUnreadable = false, int stalled = 0, PromotionGuard? guard = null) =>
        new(mode, latest, Now + TimeSpan.FromHours(3), 1, 2, 0, stalled, 0, shared, 12, eventsUnreadable, Now, Now, false, guard);

    private static (int Code, HealthReport Report) Eval(EncoreOptions options, RetentionHealthInput? retention) =>
        HealthEvaluator.Evaluate(new HealthInput(options, "0.4.0", Now, Socket(), Loop(), "/data/encore.db", 5, null, null, retention));

    [Fact]
    public void HR01_Mode_off_gives_mode_off_and_no_warning()
    {
        var (code, report) = Eval(Options(RetentionMode.Off), null);

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.Equal("off", report.Retention!.Mode);
        Assert.Empty(report.Warnings);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var retention = doc.RootElement.GetProperty("retention");
        Assert.Single(retention.EnumerateObject());
        Assert.Equal("off", retention.GetProperty("mode").GetString());
    }

    [Fact]
    public void HR02_Dry_run_before_the_first_pass_shows_mode_and_no_counts()
    {
        var (_, report) = Eval(Options(RetentionMode.DryRun), Input(RetentionMode.DryRun));

        Assert.Equal("dry_run", report.Retention!.Mode);
        Assert.Null(report.Retention.LastPassUtc);
        Assert.Null(report.Retention.Decisions);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void HR03_A_pass_shows_decisions_kept_by_signals_and_counters()
    {
        var pass = Pass(Signals(true, true), Row(1, RetentionDecision.Promote), Row(2, RetentionDecision.Keep, "requested"), Row(3, RetentionDecision.Keep, "favorite"),
            Row(4, RetentionDecision.Delete, "age"), Row(5, RetentionDecision.Wait));
        var (_, report) = Eval(Options(RetentionMode.DryRun), Input(RetentionMode.DryRun, pass));

        var r = report.Retention!;
        Assert.Equal(5, r.Tracked);
        Assert.Equal(1, r.Decisions![RetentionDecision.Promote]);
        Assert.Equal(2, r.Decisions[RetentionDecision.Keep]);
        Assert.Equal(1, r.KeptBy!["requested"]);
        Assert.Equal(1, r.KeptBy["favorite"]);
        Assert.Equal(0, r.KeptBy["plays"]);
        Assert.Equal(1, r.Promoted);
        Assert.Equal(2, r.Deleted);
        Assert.Equal(3, r.Signals!.Users);
        Assert.True(r.Signals.SmartListsInstalled);
        Assert.True(r.Signals.SmartListsRead);
        Assert.True(r.Slskd!.RadioShared);
        Assert.Equal(12, r.Slskd.UploadsCounted);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void HR04_Not_shared_gives_a_warning_with_status_ok()
    {
        var (code, report) = Eval(Options(RetentionMode.DryRun), Input(RetentionMode.DryRun, shared: false));

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.Contains("retention: music-radio is not shared on Soulseek", report.Warnings);
    }

    [Fact]
    public void HR05_Not_shared_is_not_a_warning_in_the_first_minutes_after_start()
    {
        var early = HealthEvaluator.Evaluate(new HealthInput(
            Options(RetentionMode.DryRun), "0.4.0", Started + TimeSpan.FromMinutes(1), Socket(), Loop(), "/data/encore.db", 5, null, null,
            Input(RetentionMode.DryRun, shared: false)));

        Assert.DoesNotContain(early.Report.Warnings, w => w.Contains("not shared"));
    }

    [Fact]
    public void HR06_SmartLists_installed_but_unreadable_warns_that_deletes_are_skipped()
    {
        var pass = Pass(Signals(true, false), Row(1, RetentionDecision.Wait));
        var (code, report) = Eval(Options(RetentionMode.Live), Input(RetentionMode.Live, pass));

        Assert.Equal("ok", report.Status);
        Assert.Equal(200, code);
        Assert.Contains("retention: SmartLists list unreadable, deletes skipped", report.Warnings);
        Assert.False(report.Retention!.Signals!.SmartListsRead);
    }

    [Fact]
    public void HR07_SmartLists_not_installed_is_no_warning()
    {
        var pass = Pass(Signals(false, false), Row(1, RetentionDecision.Wait));
        var (_, report) = Eval(Options(RetentionMode.Live), Input(RetentionMode.Live, pass));

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void HR08_Stalled_events_and_pause_warnings()
    {
        var guard = new PromotionGuard(false, PromotionPause.LidarrDefaults, "x", new LidarrGuardResult(true, false, PromotionPause.LidarrDefaults, "x"), 1000);
        var (_, report) = Eval(Options(RetentionMode.Live), Input(RetentionMode.Live, eventsUnreadable: true, stalled: 2, guard: guard));

        Assert.Contains("retention: 2 promotions stalled", report.Warnings);
        Assert.Contains("retention: slskd events unreadable", report.Warnings);
        Assert.Contains("retention: promotion paused (lidarr_defaults)", report.Warnings);
        Assert.False(report.Retention!.LidarrGuard!.Ok);
        Assert.Equal(PromotionPause.LidarrDefaults, report.Retention.LidarrGuard.Reason);
        Assert.Equal(1000, report.Retention.LibraryFreeBytes);
        Assert.Equal("ok", report.Status);
    }

    [Fact]
    public void HR09_Library_not_writable_has_its_own_warning()
    {
        var guard = new PromotionGuard(false, PromotionPause.LibraryNotWritable, "x", LidarrGuardResult.NotConfigured, null);
        var (_, report) = Eval(Options(RetentionMode.Live), Input(RetentionMode.Live, guard: guard));

        Assert.Contains("retention: library dir not writable", report.Warnings);
    }
}
