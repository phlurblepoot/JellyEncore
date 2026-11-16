using System.Net;
using Encore.Fetch;
using Encore.Requests.Albums;
using Encore.Requests.Search;
using Encore.Requests.Songs;
using Encore.Store;
using Encore.Tests.Discovery;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;

namespace Encore.Tests.Requests;

/// <summary>T96: the fixes for the open issues of review-v03 (search rate, album monitor memory, retry limit, preflight no-store, retry time).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ReviewFixTests
{
    // ---- issue 4: per-user search rate ---------------------------------------------------------------------------

    [Fact]
    public async Task RV04_the_31st_search_in_a_minute_is_429_search_limited_with_a_retry_after()
    {
        using var rig = new ApiRig();
        for (var i = 0; i < 30; i++)
        {
            using var ok = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("search_limited", (await ApiRig.JsonOf(limited)).GetProperty("error").GetString());
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public void RV04b_the_search_rate_is_per_person_and_a_minute_later_it_is_open_again()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
        var limiter = new SearchRateLimiter(clock);
        for (var i = 0; i < 30; i++)
        {
            Assert.Null(limiter.TryAcquire("kim"));
        }

        Assert.NotNull(limiter.TryAcquire("kim"));
        Assert.Null(limiter.TryAcquire("lee"));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(limiter.TryAcquire("kim"));
    }

    // ---- issue 10: preflight answers are not cached --------------------------------------------------------------

    [Fact]
    public async Task RV10_a_preflight_answer_carries_no_store()
    {
        using var rig = new ApiRig(new Dictionary<string, string?> { ["ENCORE_REQUEST_ORIGINS"] = "https://media.nateplex.com" });

        using var response = await rig.SendAsync(HttpMethod.Options, "/api/v1/me", origin: "https://media.nateplex.com", tweak: r =>
            r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    // ---- issue 5: only what Encore turned on is turned off -------------------------------------------------------

    [Fact]
    public async Task RV05a_a_hand_monitored_album_stays_monitored_when_the_request_is_cancelled()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: true);
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        Assert.True(rig.Row(row.Id).AlbumWasMonitored);

        await rig.Requester.CancelAsync(SongRig.Kim, row.Id, CancellationToken.None);

        Assert.True(album.Monitored);
        Assert.DoesNotContain(rig.Lidarr.Writes, w => w.EndsWith("false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RV05b_a_hand_monitored_album_stays_monitored_after_a_seven_day_not_found()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: true);
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        rig.Clock.Advance(TimeSpan.FromDays(7));
        await rig.PollAsync();

        Assert.Equal(RequestState.Failed, rig.Row(row.Id).State);
        Assert.True(album.Monitored);
    }

    [Fact]
    public async Task RV05c_an_artist_that_encore_switched_on_is_switched_off_again_with_the_album()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: false);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: false);
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        Assert.Equal((false, false), (rig.Row(row.Id).AlbumWasMonitored, rig.Row(row.Id).ArtistWasMonitored));

        await rig.Requester.CancelAsync(SongRig.Kim, row.Id, CancellationToken.None);

        Assert.False(album.Monitored);
        Assert.False(artist.Monitored);
        Assert.Equal(
            new[] { $"PUT album/monitor [{album.Id}] true", $"PUT artist/{artist.Id}", $"PUT album/monitor [{album.Id}] false", $"PUT artist/{artist.Id}" },
            rig.Lidarr.Writes);
    }

    [Fact]
    public async Task RV05d_an_artist_monitored_by_hand_is_left_on()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: false);
        var kim = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Kim);
        var lee = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Lee);

        await rig.Requester.CancelAsync(SongRig.Kim, kim.Id, CancellationToken.None);
        Assert.True(album.Monitored);
        await rig.Requester.CancelAsync(SongRig.Lee, lee.Id, CancellationToken.None);

        Assert.False(album.Monitored);
        Assert.True(artist.Monitored);
    }

    [Fact]
    public async Task RV05e_a_second_request_for_a_hand_monitored_album_does_not_turn_it_off()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: true);
        var kim = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Kim);
        var lee = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Lee);

        await rig.Requester.CancelAsync(SongRig.Kim, kim.Id, CancellationToken.None);
        await rig.Requester.CancelAsync(SongRig.Lee, lee.Id, CancellationToken.None);

        Assert.True(album.Monitored);
    }

    // ---- issue 6: retries are limited per request ----------------------------------------------------------------

    [Fact]
    public async Task RV06_a_song_request_can_be_retried_three_times_then_429_retry_limit_but_an_admin_may_go_on()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);
        for (var i = 1; i <= 3; i++)
        {
            rig.SetReadiness(row, new FetchReadiness.Failed(FetchReasons.ImportFailed));
            rig.Tick();
            rig.Fetch.Readiness.Clear();
            var (retry, _) = rig.Service.Retry(SongRig.Kim, row.Id);
            Assert.Equal(200, retry.Status);
            Assert.Equal(i, rig.Row(row.Id).Retries);
        }

        rig.SetReadiness(row, new FetchReadiness.Failed(FetchReasons.ImportFailed));
        rig.Tick();
        rig.Fetch.Readiness.Clear();

        var (limited, _) = rig.Service.Retry(SongRig.Kim, row.Id);
        Assert.Equal((429, RequestCodes.RetryLimit), (limited.Status, limited.Error));
        Assert.Equal(RequestState.Failed, rig.Row(row.Id).State);
        Assert.Equal(200, rig.Service.Retry(SongRig.Nate, row.Id).Outcome.Status);
    }

    [Fact]
    public async Task RV06b_an_album_request_can_be_retried_three_times_then_429_retry_limit()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        for (var i = 1; i <= 3; i++)
        {
            rig.Clock.Advance(TimeSpan.FromDays(7));
            await rig.PollAsync();
            Assert.Equal(RequestState.Failed, rig.Row(row.Id).State);
            var (retry, _) = await rig.Requester.RetryAsync(SongRig.Kim, row.Id, CancellationToken.None);
            Assert.Equal(200, retry.Status);
        }

        rig.Clock.Advance(TimeSpan.FromDays(7));
        await rig.PollAsync();
        Assert.Equal(RequestState.Failed, rig.Row(row.Id).State);

        var (limited, _) = await rig.Requester.RetryAsync(SongRig.Kim, row.Id, CancellationToken.None);

        Assert.Equal((429, RequestCodes.RetryLimit), (limited.Status, limited.Error));
    }

    // ---- T87: the retry time is a UTC timestamp the browser formats ----------------------------------------------

    [Fact]
    public async Task RV07_the_request_json_carries_the_next_try_as_a_utc_timestamp_not_text()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);
        var start = rig.Clock.GetUtcNow();
        rig.SetReadiness(row, new FetchReadiness.Failed(FetchFailure.NoResults));
        rig.Tick();

        var dto = RequestDto.From(rig.Row(row.Id));

        Assert.Equal(start.AddHours(1), dto.NextTryUtc);
        Assert.DoesNotContain("UTC", dto.Detail);
        Assert.DoesNotContain(":", dto.Detail);
    }
}
