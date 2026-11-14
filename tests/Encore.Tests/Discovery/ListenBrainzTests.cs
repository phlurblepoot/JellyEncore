using System.Net;
using Encore.Discovery;
using Encore.Tests.Watch;

namespace Encore.Tests.Discovery;

public sealed class ListenBrainzTests
{
    private static readonly Guid Radiohead = Guid.Parse("a74b1b7f-71a5-4011-9441-d0b5e4122711");

    private const string Similar = """
        [{"artist_mbid":"5b11f4ce-a62d-471e-81fc-a69a8278c7da","name":"Nirvana","comment":"1980s-1990s US grunge band","type":"Group","gender":null,"score":11782,"reference_mbid":"a74b1b7f-71a5-4011-9441-d0b5e4122711"},
        {"artist_mbid":"8bfac288-ccc5-448d-9573-c33ea2aa5c30","name":"Red Hot Chili Peppers","score":11140},
        {"artist_mbid":"not-a-guid","name":"Broken","score":1},
        {"artist_mbid":"9c9f1380-2516-4fc9-a3e6-f9f61941d090","name":"Muse","score":10817}]
        """;

    private const string BadAlgorithm = """{"code":400,"error":"algorithm must be one of: session_based_days_1800_session_300_contribution_3_threshold_10_limit_100_filter_True_skip_30"}""";

    private sealed class Rig
    {
        public Rig(FuncHandler handler, DiscoveryOptions? options = null)
        {
            Handler = handler;
            Runtime = new DiscoveryRuntime(options, Clock);
            Log = new ListLogger<ListenBrainzClient>();
            Client = new ListenBrainzClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://labs.api.listenbrainz.org/"), Timeout = Timeout.InfiniteTimeSpan },
                Runtime, Log);
        }

        public ManualClock Clock { get; } = new();

        public FuncHandler Handler { get; }

        public DiscoveryRuntime Runtime { get; }

        public ListLogger<ListenBrainzClient> Log { get; }

        public ListenBrainzClient Client { get; }
    }

    private static HttpResponseMessage Ok(params (string, string)[] headers) => FuncHandler.Json(HttpStatusCode.OK, Similar, headers);

    [Fact]
    public async Task LB01_Url_is_exact_and_the_bare_array_parses_best_first()
    {
        var rig = new Rig(FuncHandler.Always(_ => Ok()));
        var result = await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        Assert.Equal(
            "/similar-artists/json?artist_mbids=a74b1b7f-71a5-4011-9441-d0b5e4122711&algorithm=session_based_days_9000_session_300_contribution_5_threshold_15_limit_50_skip_30",
            Assert.Single(rig.Handler.Sent).PathAndQuery);
        Assert.Equal(["Nirvana", "Red Hot Chili Peppers", "Muse"], result.Select(a => a.Name));
        Assert.Equal(new LbSimilarArtist(Guid.Parse("5b11f4ce-a62d-471e-81fc-a69a8278c7da"), "Nirvana", 11782), result[0]);

        var limited = await rig.Client.GetSimilarArtistsAsync(Radiohead, 2, CancellationToken.None);
        Assert.Equal(2, limited.Count);
        Assert.Equal(1, rig.Handler.Calls);
    }

    [Fact]
    public async Task LB02_A_400_gives_an_empty_list_logs_it_and_skips_listenbrainz_for_24_hours()
    {
        var rig = new Rig(FuncHandler.Script(
            () => FuncHandler.Json(HttpStatusCode.BadRequest, BadAlgorithm),
            () => Ok()));
        Assert.Empty(await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None));
        var entry = Assert.Single(rig.Log.Named("discovery.source_failed"));
        Assert.Equal(400, entry.Fields["Status"]);
        Assert.Equal("listenbrainz", entry.Fields["Source"]);
        Assert.Contains("algorithm", (string)entry.Fields["Error"]!);

        Assert.Empty(await rig.Client.GetSimilarArtistsAsync(Guid.NewGuid(), 10, CancellationToken.None));
        Assert.Equal(1, rig.Handler.Calls);

        rig.Clock.Advance(TimeSpan.FromHours(24.1));
        Assert.Equal(3, (await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None)).Count);
        Assert.Equal(2, rig.Handler.Calls);
    }

    [Fact]
    public async Task LB03_Remaining_zero_with_reset_in_makes_the_next_call_wait()
    {
        var rig = new Rig(FuncHandler.Always(_ => Ok(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset-In", "3"), ("X-RateLimit-Limit", "30"))));
        await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);

        var next = rig.Client.GetSimilarArtistsAsync(Guid.NewGuid(), 10, CancellationToken.None);
        await Wait.Until(() => rig.Clock.PendingTimers == 1, "the rate-limit wait");
        Assert.False(next.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(next.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await next;
        Assert.Equal(2, rig.Handler.Calls);
    }

    [Fact]
    public async Task LB04_A_429_waits_and_retries_once_and_two_429s_fail_as_quota()
    {
        var rig = new Rig(FuncHandler.Script(
            () => FuncHandler.Json(HttpStatusCode.TooManyRequests, "{}", ("X-RateLimit-Reset-In", "2")),
            () => Ok()));
        var call = rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        await Wait.Until(() => rig.Clock.PendingTimers == 1, "the 429 wait");
        Assert.False(call.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(3, (await call).Count);
        Assert.Equal(2, rig.Handler.Calls);
        Assert.NotEmpty(rig.Log.Named("discovery.quota"));

        var twice = new Rig(FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.TooManyRequests, "{}")));
        var failing = twice.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        await Wait.Until(() => twice.Clock.PendingTimers == 1, "the default 10 s wait");
        twice.Clock.Advance(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<ListenBrainzException>(() => failing);
        Assert.Equal(DiscoveryFailure.Quota, ex.Kind);
        Assert.Equal(2, twice.Handler.Calls);
    }

    [Fact]
    public async Task LB04b_A_429_with_retry_after_uses_it_when_there_are_no_rate_headers()
    {
        var rig = new Rig(FuncHandler.Script(
            () =>
            {
                var response = FuncHandler.Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return response;
            },
            () => Ok()));
        var call = rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        await Wait.Until(() => rig.Clock.PendingTimers == 1, "the Retry-After wait");
        rig.Clock.Advance(TimeSpan.FromSeconds(6.9));
        Assert.False(call.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await call;
    }

    [Fact]
    public async Task LB05_Limiter_holds_the_thirty_first_call_for_the_ten_second_window()
    {
        var rig = new Rig(FuncHandler.Always(_ => Ok()));
        for (var i = 0; i < 30; i++)
        {
            await rig.Client.GetSimilarArtistsAsync(Guid.NewGuid(), 10, CancellationToken.None);
        }

        var thirtyFirst = rig.Client.GetSimilarArtistsAsync(Guid.NewGuid(), 10, CancellationToken.None);
        await Wait.Until(() => rig.Clock.PendingTimers == 1, "the limiter wait");
        Assert.False(thirtyFirst.IsCompleted);
        Assert.Equal(30, rig.Handler.Calls);
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await thirtyFirst;
        Assert.Equal(31, rig.Handler.Calls);
    }

    [Fact]
    public async Task LB06_Cache_keeps_a_list_for_24_hours()
    {
        var rig = new Rig(FuncHandler.Always(_ => Ok()));
        await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromHours(23));
        await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        Assert.Equal(1, rig.Handler.Calls);
        rig.Clock.Advance(TimeSpan.FromHours(2));
        await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        Assert.Equal(2, rig.Handler.Calls);
    }

    [Fact]
    public async Task LB07_No_authorization_header_is_sent_and_a_timeout_fails_without_retry()
    {
        var rig = new Rig(FuncHandler.Always(_ => Ok()));
        await rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        Assert.All(rig.Handler.Sent, s => Assert.False(s.HasAuthorization));

        var slow = new Rig(FuncHandler.Hanging());
        var call = slow.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        await Wait.Until(() => slow.Handler.Calls == 1, "request sent");
        slow.Clock.Advance(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<ListenBrainzException>(() => call);
        Assert.Equal(DiscoveryFailure.Timeout, ex.Kind);
        Assert.Equal(1, slow.Handler.Calls);
    }

    [Fact]
    public async Task LB08_Server_errors_retry_once_and_unreadable_bodies_fail_as_bad_response()
    {
        var rig = new Rig(FuncHandler.Script(
            () => FuncHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"),
            () => Ok()));
        var call = rig.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None);
        await Wait.Until(() => rig.Clock.PendingTimers == 1, "the 1 s wait");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, (await call).Count);

        var bad = new Rig(FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.OK, "{\"not\":\"a list\"}")));
        var ex = await Assert.ThrowsAsync<ListenBrainzException>(() => bad.Client.GetSimilarArtistsAsync(Radiohead, 10, CancellationToken.None));
        Assert.Equal(DiscoveryFailure.BadResponse, ex.Kind);
    }
}
