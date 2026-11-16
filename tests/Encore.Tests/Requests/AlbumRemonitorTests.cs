using Encore.Requests;
using Encore.Requests.Albums;
using Encore.Store;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Requests;

/// <summary>
/// T109: Lidarr turning a requested album off again (a new artist's background refresh saves the albums it loaded, so an
/// album a second request monitored a moment before is written back unmonitored), the 3 re-monitor limit, the hourly artist
/// check, the faster poll for two requests of one artist, and the waiting text over time.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AlbumRemonitorTests
{
    [Fact]
    public async Task RM01_A_sibling_album_that_Lidarrs_refresh_turned_off_is_monitored_again_at_the_next_poll()
    {
        using var rig = new AlbumRig();

        // Two requests for a new artist 2 s apart: the first POST album adds the artist, the second finds it there.
        var first = await rig.CreateAsync(AlbumRig.OkComputer);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = await rig.CreateAsync(AlbumRig.KidA);
        Assert.Equal(RequestState.Searching, second.State);
        var kidA = rig.Lidarr.AlbumOf(AlbumRig.KidA);
        Assert.True(kidA.Monitored);

        // Lidarr's refresh of the new artist writes the album back unmonitored after Encore's write and after-check.
        kidA.Monitored = false;
        rig.Lidarr.Writes.Clear();

        // Two requests for one artist within 15 min: the second is polled after 30 s, not 2 min.
        rig.Clock.Advance(RequestLimits.AlbumRacePoll + TimeSpan.FromSeconds(1));
        await rig.Requester.TickAsync(CancellationToken.None);

        Assert.True(kidA.Monitored);
        Assert.Equal(new[] { $"PUT album/monitor [{kidA.Id}] true" }, rig.Lidarr.Writes);
        var warning = Assert.Single(rig.Log.Named("lidarr.album_remonitored"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(second.Id, warning.Fields["RequestId"]);
        Assert.Equal((long)kidA.Id, warning.Fields["LidarrAlbumId"]);
        Assert.Equal(true, warning.Fields["AlbumOff"]);
        Assert.Equal(false, warning.Fields["ArtistOff"]);
        Assert.Equal(1, warning.Fields["Used"]);
        Assert.Equal(RequestState.Searching, rig.Row(second.Id).State);
        Assert.Equal(RequestState.Searching, rig.Row(first.Id).State);
        Assert.True(rig.Lidarr.AlbumOf(AlbumRig.OkComputer).Monitored);
    }

    [Fact]
    public async Task RM02_A_single_request_is_polled_every_2_min_and_two_old_requests_of_one_artist_too()
    {
        using var rig = new AlbumRig();
        await rig.CreateAsync(AlbumRig.OkComputer);
        rig.Lidarr.Calls.Clear();

        rig.Clock.Advance(RequestLimits.AlbumRacePoll + TimeSpan.FromSeconds(1));
        await rig.Requester.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(rig.Lidarr.Calls, c => c.StartsWith("album/", StringComparison.Ordinal));

        // A second request of the same artist after the 15-min window: no fast poll either.
        rig.Clock.Advance(RequestLimits.AlbumRaceWindow);
        var late = await rig.CreateAsync(AlbumRig.KidA);
        await rig.Requester.TickAsync(CancellationToken.None);
        rig.Lidarr.Calls.Clear();
        rig.Lidarr.AlbumOf(AlbumRig.KidA).Monitored = false;
        rig.Clock.Advance(RequestLimits.AlbumRacePoll + TimeSpan.FromSeconds(1));
        await rig.Requester.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(rig.Lidarr.Calls, c => c.StartsWith("album/", StringComparison.Ordinal));

        // The normal 2-min poll still puts it back on.
        await rig.PollAsync();
        Assert.True(rig.Lidarr.AlbumOf(AlbumRig.KidA).Monitored);
        Assert.Equal(RequestState.Searching, rig.Row(late.Id).State);
    }

    [Fact]
    public async Task RM03_After_3_re_monitors_the_request_fails_lidarr_rejected_and_Lidarr_is_left_as_it_is()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);

        for (var i = 1; i <= RequestLimits.AlbumRemonitors; i++)
        {
            album.Monitored = false;
            await rig.PollAsync();
            Assert.True(album.Monitored);
            Assert.Equal(RequestState.Searching, rig.Row(row.Id).State);
        }

        Assert.Equal(RequestLimits.AlbumRemonitors, rig.Log.Named("lidarr.album_remonitored").Count());

        album.Monitored = false;
        rig.Lidarr.Writes.Clear();
        var notify = await rig.PollAsync();

        var failed = rig.Row(row.Id);
        Assert.Equal(RequestState.Failed, failed.State);
        Assert.Equal(AlbumReasons.LidarrRejected, failed.FailReason);
        Assert.Contains(notify, n => n.Id == row.Id);
        Assert.Empty(rig.Lidarr.Writes);
        Assert.False(album.Monitored);
        Assert.True(rig.Lidarr.ArtistOf(album.ArtistId).Monitored);
        var limit = Assert.Single(rig.Log.Named("lidarr.album_remonitor_limit"));
        Assert.Equal(LogLevel.Warning, limit.Level);
        Assert.Equal(RequestLimits.AlbumRemonitors, limit.Fields["Used"]);
    }

    [Fact]
    public async Task RM04_A_retry_after_the_limit_gets_3_re_monitors_again()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        for (var i = 0; i <= RequestLimits.AlbumRemonitors; i++)
        {
            album.Monitored = false;
            await rig.PollAsync();
        }

        Assert.Equal(RequestState.Failed, rig.Row(row.Id).State);

        var (outcome, _) = await rig.Requester.RetryAsync(SongRig.Kim, row.Id, CancellationToken.None);
        Assert.Equal(RequestState.Searching, outcome.Row!.State);
        album.Monitored = false;
        await rig.PollAsync();
        Assert.True(album.Monitored);
        Assert.Equal(RequestState.Searching, rig.Row(row.Id).State);
    }

    [Fact]
    public async Task RM05_The_artist_flag_is_checked_at_most_once_an_hour_unless_the_album_is_re_monitored()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        var artist = rig.Lidarr.ArtistOf(album.ArtistId);
        rig.Lidarr.Writes.Clear();

        // Turned off by hand after the send: the polls within the hour leave it.
        artist.Monitored = false;
        for (var i = 0; i < 20; i++)
        {
            await rig.PollAsync();
        }

        Assert.Empty(rig.Lidarr.Writes);

        // The hourly check switches it back on, once.
        rig.Clock.Advance(TimeSpan.FromMinutes(25));
        await rig.PollAsync();
        Assert.Equal(new[] { $"PUT artist/{artist.Id}" }, rig.Lidarr.Writes);
        Assert.True(artist.Monitored);
        var warning = Assert.Single(rig.Log.Named("lidarr.album_remonitored"));
        Assert.Equal(false, warning.Fields["AlbumOff"]);
        Assert.Equal(true, warning.Fields["ArtistOff"]);

        // Off again: not touched until the next hour, unless the album is off too, which checks the artist at once.
        artist.Monitored = false;
        rig.Lidarr.Writes.Clear();
        await rig.PollAsync();
        Assert.Empty(rig.Lidarr.Writes);
        album.Monitored = false;
        await rig.PollAsync();
        Assert.Equal(new[] { $"PUT album/monitor [{album.Id}] true", $"PUT artist/{artist.Id}" }, rig.Lidarr.Writes);
        Assert.True(album.Monitored);
        Assert.True(artist.Monitored);
        Assert.Equal(RequestState.Searching, rig.Row(row.Id).State);
    }

    [Fact]
    public async Task RM06_Two_requests_holding_one_album_re_monitor_it_once_per_pass()
    {
        using var rig = new AlbumRig();
        var mine = await rig.CreateAsync(AlbumRig.OkComputer);
        var theirs = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Lee);
        Assert.Equal(mine.LidarrAlbumId, theirs.LidarrAlbumId);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        album.Monitored = false;
        rig.Lidarr.Writes.Clear();

        await rig.PollAsync();

        Assert.Equal(new[] { $"PUT album/monitor [{album.Id}] true" }, rig.Lidarr.Writes);
        Assert.Single(rig.Log.Named("lidarr.album_remonitored"));
    }

    [Fact]
    public async Task RM07_The_waiting_text_changes_after_30_min_without_restarting_the_7_day_timer()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var began = rig.Row(row.Id).UpdatedUtc;
        Assert.Equal("Waiting for Soularr to find it on Soulseek", rig.Row(row.Id).Detail);

        // 28 min (14 polls): still the first text.
        for (var i = 0; i < 13; i++)
        {
            await rig.PollAsync();
        }

        Assert.True(rig.Clock.GetUtcNow() - began < RequestLimits.AlbumFirstSearch);
        Assert.Equal(AlbumRequester.Details.Searching, rig.Row(row.Id).Detail);

        await rig.PollAsync();
        await rig.PollAsync();
        var later = rig.Row(row.Id);
        Assert.Equal(RequestState.Searching, later.State);
        Assert.Equal("Not found on Soulseek yet; Soularr tries again within a day", later.Detail);
        Assert.Equal(began, later.UpdatedUtc);

        // The 7-day not_found timer still counts from when searching began.
        rig.Clock.Advance(began + RequestLimits.AlbumNoFileTimeout - rig.Clock.GetUtcNow() - RequestLimits.AlbumPoll);
        await rig.PollAsync();
        Assert.Equal(RequestState.Failed, rig.Row(row.Id).State);
        Assert.Equal(AlbumReasons.NotFound, rig.Row(row.Id).FailReason);
    }

    [Fact]
    public async Task RM08_A_row_with_the_old_waiting_text_gets_the_new_one_and_keeps_its_timer()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var began = rig.Row(row.Id).UpdatedUtc;
        rig.Store.Update(row.Id, RequestState.Searching, began, new RequestUpdate(Detail: "Waiting for Soulseek (Soularr checks every few minutes)"));
        rig.Clock.Advance(TimeSpan.FromHours(3));

        await rig.PollAsync();

        var now = rig.Row(row.Id);
        Assert.Equal(AlbumRequester.Details.NotFoundYet, now.Detail);
        Assert.Equal(began, now.UpdatedUtc);
    }

    [Fact]
    public void RM09_Waiting_text_by_time()
    {
        Assert.Equal(AlbumRequester.Details.Searching, AlbumRequester.Details.SearchingAfter(TimeSpan.Zero));
        Assert.Equal(AlbumRequester.Details.Searching, AlbumRequester.Details.SearchingAfter(TimeSpan.FromMinutes(29)));
        Assert.Equal(AlbumRequester.Details.NotFoundYet, AlbumRequester.Details.SearchingAfter(TimeSpan.FromMinutes(30)));
        Assert.Equal(AlbumRequester.Details.NotFoundYet, AlbumRequester.Details.SearchingAfter(TimeSpan.FromDays(3)));
    }
}
