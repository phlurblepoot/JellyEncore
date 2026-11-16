using Encore.Config;
using Encore.Health;
using Encore.Jellyfin;
using Encore.Orchestration;
using Encore.Tests.Watch;
using Encore.Watch;

namespace Encore.Tests.EndToEnd;

/// <summary>T117: ENCORE_ALLOWED_USERS=* lets every Jellyfin user have radio without resolving names.</summary>
public class EveryoneTests : IDisposable
{
    private static readonly (Guid Id, string PlaylistItemId)[] Queue =
    [
        (Catalog.G(1000), "playlistItem0"),
        (Catalog.G(1001), "playlistItem1"),
        (Catalog.SeedSong, "playlistItem2"),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-everyone-" + Guid.NewGuid().ToString("N"));

    public EveryoneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private Dictionary<string, string?> Env(string users) => new()
    {
        ["JELLYFIN_URL"] = "http://jellyfin:8096",
        ["JELLYFIN_API_KEY"] = "key",
        ["ENCORE_ALLOWED_USERS"] = users,
        ["ENCORE_DATA_DIR"] = _dir,
    };

    [Fact]
    public void Star_alone_is_accepted_and_means_everyone()
    {
        var result = EncoreOptionsLoader.Load(Env("*"));
        Assert.True(result.IsValid);
        Assert.True(result.Options!.AllowedUsersAll);
        Assert.Empty(result.Options.AllowedUsers);
    }

    [Fact]
    public void Star_among_names_means_everyone_and_keeps_the_names()
    {
        var result = EncoreOptionsLoader.Load(Env("Nathan, *, Sam"));
        Assert.True(result.Options!.AllowedUsersAll);
        Assert.Equal(["Nathan", "Sam"], result.Options.AllowedUsers);
    }

    [Fact]
    public void Named_list_is_not_everyone_and_an_empty_list_is_still_an_error()
    {
        Assert.False(EncoreOptionsLoader.Load(Env("Nathan")).Options!.AllowedUsersAll);
        var bad = EncoreOptionsLoader.Load(Env(" , "));
        Assert.False(bad.IsValid);
        Assert.Contains(bad.Errors, e => e.Variable == "ENCORE_ALLOWED_USERS");
    }

    [Fact]
    public async Task A_user_who_is_not_listed_and_never_resolved_gets_radio_with_star()
    {
        using var h = new OrchestratorHarness();
        h.Options = h.Options with { AllowedUsers = [], AllowedUsersAll = true };
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        var guest = Sessions.Web(Catalog.OtherUser, Catalog.SeedSong, "playlistItem2", Queue);
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, guest));

        Assert.Single(h.Jellyfin.PlayLasts);
        Assert.True(orchestrator.Status.AllowListEveryone);
    }

    [Fact]
    public async Task A_user_created_after_start_is_allowed_without_a_restart()
    {
        using var h = new OrchestratorHarness();
        h.Options = h.Options with { AllowedUsers = ["nobody"], AllowedUsersAll = true };
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        var newcomer = Catalog.G(424242);
        var session = Sessions.Web(newcomer, Catalog.SeedSong, "playlistItem2", Queue);
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, session));

        Assert.Single(h.Jellyfin.PlayLasts);
    }

    [Fact]
    public async Task Named_list_still_refuses_a_user_who_is_not_listed()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        var guest = Sessions.Web(Catalog.OtherUser, Catalog.SeedSong, "playlistItem2", Queue);
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, guest));

        Assert.Empty(h.Jellyfin.PlayLasts);
        Assert.False(orchestrator.Status.AllowListEveryone);
    }

    [Fact]
    public void Health_shows_star_the_everyone_flag_and_no_resolved_list_and_is_ok()
    {
        var started = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
        var now = started + TimeSpan.FromMinutes(5);
        var options = new EncoreOptions
        {
            JellyfinUrl = new Uri("http://jellyfin.test:8096"),
            JellyfinApiKey = "k",
            AllowedUsers = [],
            AllowedUsersAll = true,
            DataDir = "/data",
            DeviceId = "encore-0123456789ab",
        };
        var loop = new OrchestratorStatus
        {
            StartedUtc = started,
            LastHeartbeatUtc = now,
            AllowListConfigured = ["*"],
            AllowListEveryone = true,
            AllowListResolvedUtc = started,
            StoreOk = true,
        };
        var socket = new WatcherStatus { State = SocketState.Subscribed, Since = started, LastSessionsAt = now };

        var (code, report) = HealthEvaluator.Evaluate(new HealthInput(options, "0.3.6", now, socket, loop, "/data/encore.db", 1));

        Assert.Equal(200, code);
        Assert.Equal("ok", report.Status);
        Assert.Equal(["*"], report.AllowList.Configured);
        Assert.True(report.AllowList.Everyone);
        Assert.Empty(report.AllowList.Resolved);
        Assert.Empty(report.AllowList.Unresolved);
    }
}
