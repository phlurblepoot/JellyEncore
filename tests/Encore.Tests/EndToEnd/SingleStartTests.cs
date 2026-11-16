using Encore.Jellyfin;
using Encore.Trigger;
using Orchestrator = Encore.Orchestration.Orchestrator;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// T110 end to end: a song played on its own from a stopped player (Jellyfin 12.4 still holds an empty list) starts
/// the radio once, near the song's end, with <c>rule=single</c>; in dry run it only logs what it would add.
/// Catalog songs are 3 minutes long, so the near-end point is at 90 s.
/// </summary>
public class SingleStartTests
{
    private static readonly Guid Song = Catalog.SeedSong;

    private static async Task<(OrchestratorHarness H, Orchestrator O)> CreateAsync(Action<OrchestratorHarness>? setup = null)
    {
        var h = new OrchestratorHarness();
        setup?.Invoke(h);
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();
        return (h, orchestrator);
    }

    private static SessionInfoDto At(SessionInfoDto dto, double seconds) =>
        dto with { PlayState = dto.PlayState! with { PositionTicks = TimeSpan.FromSeconds(seconds).Ticks } };

    /// <summary>jellyfin-web playing <see cref="Song"/> from idle: entry playlistItem0, the server's list still [].</summary>
    private static SessionInfoDto WebSingle(double seconds) => At(Sessions.Web(Catalog.User, Song, "playlistItem0", []), seconds);

    private static SessionInfoDto FinampSingle(double seconds) => At(Sessions.Finamp(Song), seconds);

    [Fact]
    public async Task Web_single_from_idle_sends_once_at_the_near_end_point()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;

        await h.Send(o, WebSingle(0));
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(o, WebSingle(60));
        Assert.Empty(h.Jellyfin.PlayLasts);
        Assert.Contains(h.Log.Named("trigger.skipped_quiet"), e => e.Fields["Reason"] as string == "single_waiting");
        Assert.DoesNotContain(h.Log.Named("trigger.skipped"), e => e.Fields["Reason"] as string == "stale_queue");

        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Send(o, WebSingle(91));
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.Send(o, WebSingle(93));

        var call = Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Equal(2, call.ItemIds.Length);
        Assert.DoesNotContain(Song, call.ItemIds);
        var fired = Assert.Single(h.Log.Named("trigger.fired"));
        Assert.Equal("start", fired.Fields["Kind"]);
        Assert.Equal("single", fired.Fields["Rule"]);
        Assert.Equal(1, h.Store.CountTriggers("sent"));
    }

    [Fact]
    public async Task Finamp_single_from_idle_sends_once_with_no_history_since_Encore_started()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;

        await h.Send(o, FinampSingle(5));
        Assert.Empty(h.Jellyfin.PlayLasts);

        h.Time.Advance(TimeSpan.FromSeconds(100));
        await h.Send(o, FinampSingle(105));
        // A stop/start flip-flop of the same song does not send again.
        await h.Send(o, Sessions.FinampStopped());
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Send(o, FinampSingle(106));

        Assert.Single(h.Jellyfin.PlayLasts);
        var fired = Assert.Single(h.Log.Named("trigger.fired"));
        Assert.Equal("single", fired.Fields["Rule"]);
    }

    [Fact]
    public async Task A_restart_in_the_middle_of_the_song_does_not_send_again()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await h.Send(o, FinampSingle(100));
        Assert.Single(h.Jellyfin.PlayLasts);

        h.RestartStore();
        var restarted = h.CreateOrchestrator();
        await restarted.RefreshAllowListAsync();
        h.Time.Advance(TimeSpan.FromSeconds(20));
        await h.Send(restarted, FinampSingle(121));

        Assert.Single(h.Jellyfin.PlayLasts);
    }

    [Fact]
    public async Task Dry_run_logs_would_add_and_sends_nothing()
    {
        var (h, o) = await CreateAsync(x => x.Options = x.Options with { DryRun = true });
        using var _ = h;

        await h.Send(o, WebSingle(100));
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.Send(o, WebSingle(102));

        Assert.Empty(h.Jellyfin.PlayLasts);
        Assert.Single(h.Log.Named("queue.would_add"));
        Assert.Equal("single", Assert.Single(h.Log.Named("trigger.fired")).Fields["Rule"]);
        Assert.Equal(1, h.Store.CountTriggers("dry_run"));
    }
}
