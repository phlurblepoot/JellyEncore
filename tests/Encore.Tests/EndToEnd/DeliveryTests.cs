using Encore.Jellyfin;
using Encore.Store;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// PlayLast without an answer, and the delivery verdict (T27): a send that may have gone through is never retried,
/// and the queue the client reports at its next track change or stop decides whether the songs arrived.
/// </summary>
public class DeliveryTests
{
    private static readonly (Guid Id, string PlaylistItemId)[] LastSongQueue =
    [
        (Catalog.G(1000), "playlistItem0"),
        (Catalog.G(1001), "playlistItem1"),
        (Catalog.SeedSong, "playlistItem2"),
    ];

    private static SessionInfoDto LastSongOfWeb() =>
        Sessions.Web(Catalog.User, Catalog.SeedSong, "playlistItem2", LastSongQueue);

    /// <summary>The stop half of a track change, or a real stop: no playing item, the queue the client reported.</summary>
    private static SessionInfoDto Stopped(IEnumerable<(Guid Id, string PlaylistItemId)> queue) =>
        Sessions.Web(Catalog.User, Catalog.SeedSong, "playlistItem2", queue) with { NowPlayingItem = null, PlaylistItemId = null };

    private static IEnumerable<(Guid Id, string PlaylistItemId)> WithAdded(Guid[] sent) =>
        LastSongQueue.Concat(new (Guid Id, string PlaylistItemId)[] { (sent[0], "playlistItem3"), (sent[1], "playlistItem4") });

    private static JellyfinRequestException Timeout() => new("PlayLast", "timed out", outcomeUnknown: true);

    private static async Task<(OrchestratorHarness H, Encore.Orchestration.Orchestrator O)> StartAsync(Exception? playLastFailure = null)
    {
        var h = new OrchestratorHarness();
        h.Jellyfin.PlayLastFailure = playLastFailure;
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        return (h, orchestrator);
    }

    [Fact]
    public async Task A_playlast_timeout_is_not_retried_and_counts_as_sent()
    {
        var (h, orchestrator) = await StartAsync(Timeout());
        using var _ = h;

        // Well past the 15 s fire retry, on the same last song, twice.
        for (var i = 0; i < 2; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(20));
            await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));
        }

        Assert.Single(h.Jellyfin.PlayLastAttempts);
        Assert.Equal(1, h.Store.CountTriggers(EncoreStore.SentNoReplyOutcome));
        Assert.Equal(0, h.Store.CountTriggers("failed"));
        Assert.Equal(2, h.Store.CountAddedSince(null, h.Time.GetUtcNow() - TimeSpan.FromHours(1)));
        Assert.Single(h.Log.Named("queue.add_no_reply"));
        Assert.Empty(h.Log.Named("queue.add_failed"));
        Assert.Equal(1, orchestrator.Status.Sent);
        Assert.Equal(0, orchestrator.Status.Failed);
        Assert.Equal(EncoreStore.SentNoReplyOutcome, orchestrator.Status.LastOutcome);
    }

    [Fact]
    public async Task A_timed_out_send_is_confirmed_when_the_songs_show_up_in_the_queue()
    {
        var (h, orchestrator) = await StartAsync(Timeout());
        using var _ = h;
        var sent = Assert.Single(h.Jellyfin.PlayLastAttempts).ItemIds;

        // The request did reach the client: the first added song starts and the queue holds both.
        h.Time.Advance(TimeSpan.FromSeconds(200));
        var next = Sessions.Web(Catalog.User, sent[0], "playlistItem3", WithAdded(sent), Catalog.Song(sent[0], Catalog.SeedArtist, "Added"));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, next));

        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Empty(h.Log.Named("queue.unconfirmed"));

        // T28: the first added song is the next-to-last entry, so a near-end top-up fires with new songs; the
        // timed-out send is not sent again.
        Assert.Equal(2, h.Jellyfin.PlayLastAttempts.Count);
        var second = h.Jellyfin.PlayLastAttempts.Last().ItemIds;
        Assert.Empty(second.Intersect(sent));
        var fired = h.Log.Named("trigger.fired").Last();
        Assert.Equal("topup", fired.Fields["Kind"]);
        Assert.Equal("near_end", fired.Fields["Rule"]);
        Assert.Empty(h.Log.Named("queue.resent"));
    }

    [Fact]
    public async Task Playback_stopped_without_the_songs_is_unconfirmed_right_away()
    {
        var (h, orchestrator) = await StartAsync(Timeout());
        using var _ = h;

        // The seed song ends and the client reports its queue without Encore's songs: they never arrived.
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, Stopped(LastSongQueue)));

        var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal("stopped", verdict.Fields["JudgedAt"]);
        Assert.Equal(1, orchestrator.Status.Unconfirmed);
        Assert.Empty(h.Log.Named("queue.confirmed"));
    }

    [Fact]
    public async Task A_stop_whose_queue_holds_the_songs_confirms_them_right_away()
    {
        var (h, orchestrator) = await StartAsync();
        using var _ = h;
        var sent = Assert.Single(h.Jellyfin.PlayLasts).ItemIds;

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, Stopped(WithAdded(sent))));

        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
    }

    [Fact]
    public async Task A_stop_with_an_empty_queue_report_waits_for_the_grace_then_is_unconfirmed()
    {
        var (h, orchestrator) = await StartAsync();
        using var _ = h;

        // Empty report: a real stop, or Finamp moving onto the last queued song. Not judged yet.
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, Stopped([])));
        Assert.Empty(h.Log.Named("queue.unconfirmed"));

        h.Time.Advance(TimeSpan.FromSeconds(11));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, Stopped([])));

        var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal("stopped", verdict.Fields["JudgedAt"]);
    }

    [Fact]
    public async Task A_song_starting_within_the_grace_is_judged_by_the_next_song_check()
    {
        var (h, orchestrator) = await StartAsync();
        using var _ = h;
        var sent = Assert.Single(h.Jellyfin.PlayLasts).ItemIds;

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, Stopped([])));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        var next = Sessions.Web(Catalog.User, sent[0], "playlistItem3", WithAdded(sent), Catalog.Song(sent[0], Catalog.SeedArtist, "Added"));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, next));
        h.Time.Advance(TimeSpan.FromSeconds(20));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, next));

        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
    }

    [Fact]
    public async Task A_session_that_ends_while_waiting_is_unconfirmed()
    {
        var (h, orchestrator) = await StartAsync();
        using var _ = h;

        h.Time.Advance(TimeSpan.FromSeconds(30));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time));

        var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal("session_ended", verdict.Fields["JudgedAt"]);
    }

    [Fact]
    public async Task A_503_is_still_retried_after_the_fire_retry_delay()
    {
        var (h, orchestrator) = await StartAsync(new JellyfinRequestException("PlayLast", 503));
        using var _ = h;
        Assert.Single(h.Jellyfin.PlayLastAttempts);
        Assert.Equal(1, h.Store.CountTriggers("failed"));

        h.Jellyfin.PlayLastFailure = null;
        h.Time.Advance(TimeSpan.FromSeconds(16));
        await orchestrator.HandleSnapshotAsync(OrchestratorHarness.Snapshot(h.Time, LastSongOfWeb()));

        Assert.Equal(2, h.Jellyfin.PlayLastAttempts.Count);
        Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Equal(1, h.Store.CountTriggers(EncoreStore.SentOutcome));
        Assert.Empty(h.Log.Named("queue.add_no_reply"));
    }
}
