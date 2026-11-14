using Encore.Jellyfin;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Encore.Trigger;
using Encore.Store;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// End to end with a fake Jellyfin: session snapshot in, PlayLast (or dry run, or nothing) out. These are the
/// design's section 9.2 T17 cases.
/// </summary>
public class OrchestratorTests
{
    // jellyfin-web queue of a session playing the seed song as its last entry.
    private static readonly (Guid Id, string PlaylistItemId)[] LastSongQueue =
    [
        (Catalog.G(1000), "playlistItem0"),
        (Catalog.G(1001), "playlistItem1"),
        (Catalog.SeedSong, "playlistItem2"),
    ];

    private static SessionInfoDto LastSongOfWeb() =>
        Sessions.Web(Catalog.User, Catalog.SeedSong, "playlistItem2", LastSongQueue);

    [Fact]
    public async Task Web_last_song_sends_exactly_one_playlast_with_two_ids()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));

        var call = Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Equal(Sessions.WebId, call.SessionId);
        Assert.Equal(2, call.ItemIds.Length);
        Assert.DoesNotContain(Catalog.SeedSong, call.ItemIds);
        Assert.DoesNotContain(LastSongQueue, q => call.ItemIds.Contains(q.Id));
        Assert.Equal(2, h.Store.CountAddedSince(null, h.Time.GetUtcNow() - TimeSpan.FromHours(1)));
        Assert.Equal(1, h.Store.CountTriggers("sent"));
        Assert.Equal(1, orchestrator.Status.Sent);
        Assert.Equal(2, orchestrator.Status.ItemsSent);
    }

    [Fact]
    public async Task Same_state_again_does_not_fire_twice()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        h.Time.Advance(TimeSpan.FromMilliseconds(1500));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));

        Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Single(h.Log.Named("trigger.fired"));
    }

    [Fact]
    public async Task Dry_run_logs_would_queue_and_sends_nothing()
    {
        using var h = new OrchestratorHarness();
        h.Options = h.Options with { DryRun = true };
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));

        Assert.Empty(h.Jellyfin.PlayLasts);
        var would = Assert.Single(h.Log.Named("queue.would_add"));
        Assert.Contains("Dry run", would.Message, StringComparison.Ordinal);
        Assert.Equal(1, h.Store.CountTriggers("dry_run"));
        Assert.Equal(0, h.Store.CountAddedSince(null, h.Time.GetUtcNow() - TimeSpan.FromHours(1)));
        Assert.Equal(1, orchestrator.Status.DryRun);
        Assert.Equal(0, orchestrator.Status.Sent);
    }

    [Fact]
    public async Task User_not_on_the_allow_list_gets_nothing()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        var guest = Sessions.Web(Catalog.OtherUser, Catalog.SeedSong, "playlistItem2", LastSongQueue);
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, guest));

        Assert.Empty(h.Jellyfin.PlayLasts);
        Assert.Empty(h.Log.Named("trigger.fired"));
        Assert.Equal(0, h.Store.CountTriggers("sent"));
    }

    [Fact]
    public async Task Encores_own_session_is_ignored()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        var own = LastSongOfWeb() with { DeviceId = TriggerHarness.OwnDeviceId };
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, own));

        Assert.Empty(h.Jellyfin.PlayLasts);
        Assert.Equal(0, orchestrator.Status.SessionsTracked);
    }

    [Fact]
    public async Task Top_up_sends_the_next_planned_songs_when_the_last_added_song_is_last()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        var first = Assert.Single(h.Jellyfin.PlayLasts).ItemIds;

        // The batch was queued after the seed song. The second added song is now the last entry.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        var queue = LastSongQueue.Concat(new (Guid Id, string PlaylistItemId)[] { (first[0], "playlistItem3"), (first[1], "playlistItem4") });
        var atTopUp = Sessions.Web(Catalog.User, first[1], "playlistItem4", queue, Catalog.Song(first[1], Catalog.SeedArtist, "Added"));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, atTopUp));

        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        var second = h.Jellyfin.PlayLasts.Last().ItemIds;
        Assert.Equal(2, second.Length);
        Assert.Empty(second.Intersect(first));
        Assert.Equal("topup", h.Log.Named("trigger.fired").Last().Fields["Kind"]);
    }

    [Fact]
    public async Task Restart_in_the_middle_of_the_last_song_does_not_send_again()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        Assert.Single(h.Jellyfin.PlayLasts);

        // Restart ten minutes later, with an empty memory, while the same song is still the last one.
        h.RestartStore();
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var restarted = h.CreateOrchestrator();
        await restarted.RefreshAllowListAsync();
        await restarted.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));

        Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Contains(h.Log.Named("trigger.skipped"), e => e.Fields["Reason"] as string == "duplicate");
    }

    [Fact]
    public async Task A_failed_playlast_is_recorded_as_failed_and_adds_nothing()
    {
        using var h = new OrchestratorHarness();
        h.Jellyfin.PlayLastFailure = new JellyfinRequestException("PlayLast", 503);
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));

        Assert.Empty(h.Jellyfin.PlayLasts);
        Assert.Equal(0, h.Store.CountAddedSince(null, h.Time.GetUtcNow() - TimeSpan.FromHours(1)));
        Assert.Equal(1, h.Store.CountTriggers("failed"));
        Assert.Equal(1, orchestrator.Status.Failed);
        Assert.Single(h.Log.Named("queue.add_failed"));
        Assert.Equal("failed", orchestrator.Status.LastOutcome);
    }

    [Fact]
    public async Task Queued_songs_are_confirmed_when_the_next_song_starts()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        var sent = Assert.Single(h.Jellyfin.PlayLasts).ItemIds;

        // The first added song starts: it is in the queue, so the batch is confirmed.
        h.Time.Advance(TimeSpan.FromSeconds(200));
        var queue = LastSongQueue.Concat(new (Guid Id, string PlaylistItemId)[] { (sent[0], "playlistItem3"), (sent[1], "playlistItem4") });
        var next = Sessions.Web(Catalog.User, sent[0], "playlistItem3", queue, Catalog.Song(sent[0], Catalog.SeedArtist, "Added"));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, next));

        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
        Assert.Equal(0, orchestrator.Status.Unconfirmed);

        // T28: the first added song is next-to-last, so the second PlayLast is a near-end top-up.
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        Assert.Equal("topup", h.Log.Named("trigger.fired").Last().Fields["Kind"]);
    }

    [Fact]
    public async Task Nobody_is_allowed_while_no_allow_list_name_resolves()
    {
        using var h = new OrchestratorHarness();
        h.Jellyfin.Users = [new UserDto { Id = Catalog.G(9), Name = "someone-else" }];
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();

        Assert.Equal(new[] { "nathan" }, orchestrator.Status.AllowListUnresolved);
        Assert.Single(h.Log.Named("allowlist.unresolved"));

        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        Assert.Empty(h.Jellyfin.PlayLasts);
    }

    [Fact]
    public async Task The_running_loop_takes_snapshots_from_the_channel()
    {
        using var h = new OrchestratorHarness();
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(h.Channel.Writer.TryWrite(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb())));
            await Wait.Until(() => h.Jellyfin.PlayLasts.Count == 1, "the PlayLast from the loop");
        }
        finally
        {
            await orchestrator.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, orchestrator.Status.Sent);
    }

    [Fact]
    public async Task A_hung_pick_times_out_as_a_failed_fire_and_sends_nothing()
    {
        using var h = new OrchestratorHarness();
        h.Timing = h.Timing with { PickTimeout = TimeSpan.FromMilliseconds(100) };
        var orchestrator = new Encore.Orchestration.Orchestrator(
            h.Jellyfin, new HangingPicker(), h.Store, h.Options, h.Channel.Reader, h.Time, h.Log, h.Timing);
        await orchestrator.RefreshAllowListAsync();

        var handled = orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        Assert.Same(handled, await Task.WhenAny(handled, Task.Delay(TimeSpan.FromSeconds(10))));
        await handled;

        Assert.Empty(h.Jellyfin.PlayLasts);
        Assert.Equal(1, orchestrator.Status.Failed);
        Assert.Equal(1, h.Store.CountTriggers("failed"));
        Assert.Contains("picking songs took longer", h.Log.Named("queue.add_failed").Single().Message, StringComparison.Ordinal);
    }

    /// <summary>A picker that never answers until it is cancelled, like one stuck on a hung Jellyfin.</summary>
    private sealed class HangingPicker : Encore.Picks.IPicker
    {
        public async Task<Encore.Picks.PickResult> PickAsync(Encore.Picks.PickRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
