using System.Net;
using Encore.Discovery;
using Encore.Tests.Watch;

namespace Encore.Tests.Discovery;

public sealed class DeezerTests
{
    private const string QuotaBody = """{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""";
    private const string NotFoundBody = """{"error":{"type":"DataException","message":"no data","code":800}}""";

    private const string ArtistSearch = """
        {"data":[{"id":53477202,"name":"DJ Radiohead","link":"https://www.deezer.com/artist/53477202","nb_album":31,"nb_fan":64,"radio":true,"type":"artist"},
        {"id":399,"name":"Radiohead","link":"https://www.deezer.com/artist/399","picture_xl":"https://x/1000x1000.jpg","nb_album":45,"nb_fan":4122959,"radio":true,"tracklist":"https://api.deezer.com/artist/399/top?limit=50","type":"artist"},
        {"id":777,"name":"Radiohead","nb_fan":12,"radio":false,"type":"artist"}],
        "total":6,"next":"https://api.deezer.com/search/artist?q=radiohead&limit=10&index=10"}
        """;

    private const string Radio = """
        {"data":[{"id":138547421,"readable":true,"title":"Thinking About You","title_short":"Thinking About You","title_version":"","duration":161,"rank":492165,"explicit_lyrics":false,"preview":"https://cdnt-preview.dzcdn.net/x.mp3?hdnea=exp=1~hmac=SECRET","artist":{"id":399,"name":"Radiohead","type":"artist"},"album":{"id":14880711,"title":"Pablo Honey","type":"album"},"type":"track"},
        {"id":15715644,"title":"Luna (2011 Remaster)","title_short":"Luna","title_version":"(2011 Remaster)","duration":202,"rank":1,"artist":{"id":193331,"name":"The Smashing Pumpkins"},"album":{"id":1452137,"title":"Siamese Dream (Deluxe Edition)"},"type":"track"},
        {"title":"Row without an id","artist":{"id":1,"name":"X"}}],
        "total":2}
        """;

    private const string Related = """
        {"data":[{"id":664,"name":"Jeff Buckley","nb_album":21,"nb_fan":332065,"radio":true,"type":"artist"},
        {"id":193331,"name":"The Smashing Pumpkins","nb_album":36,"nb_fan":1775134,"radio":true,"type":"artist"}],
        "total":20,"next":"https://api.deezer.com/artist/399/related?limit=2&index=2"}
        """;

    private const string Top = """
        {"data":[{"id":138547415,"title":"Creep","title_short":"Creep","duration":238,"rank":978547,"explicit_lyrics":true,
        "contributors":[{"id":399,"name":"Radiohead","role":"Main","type":"artist"}],
        "artist":{"id":399,"name":"Radiohead"},"album":{"id":14880711,"title":"Pablo Honey"},"type":"track"},
        {"id":138539979,"title":"Let Down","duration":299,"artist":{"id":399,"name":"Radiohead"},"album":{"id":14879699,"title":"OK Computer"},"type":"track"}],
        "total":100}
        """;

    private const string TrackDetail = """
        {"id":3135556,"readable":true,"title":"Harder, Better, Faster, Stronger","title_short":"Harder, Better, Faster, Stronger","title_version":"",
        "isrc":"GBDUW0000059","link":"https://www.deezer.com/track/3135556","duration":226,
        "track_position":4,"disk_number":1,"rank":810329,"release_date":"2001-03-12",
        "preview":"https://cdnt-preview.dzcdn.net/y.mp3?hdnea=exp=1~hmac=SECRET","bpm":0,"gain":-12.4,
        "available_countries":["AE","AF"],
        "contributors":[{"id":27,"name":"Daft Punk","role":"Main","type":"artist"},{"id":28,"name":"Someone","role":"Featured","type":"artist"}],
        "track_token":"AAAAA-signed-token",
        "artist":{"id":27,"name":"Daft Punk","type":"artist"},
        "album":{"id":302127,"title":"Discovery","release_date":"2001-03-07","md5_image":"5718f7c8","type":"album"},"type":"track"}
        """;

    private sealed class Rig
    {
        public Rig(FuncHandler handler, DiscoveryOptions? options = null)
        {
            Handler = handler;
            Runtime = new DiscoveryRuntime(options, Clock);
            Log = new ListLogger<DeezerClient>();
            Client = new DeezerClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com/"), Timeout = Timeout.InfiniteTimeSpan },
                Runtime, Log);
        }

        public ManualClock Clock { get; } = new();

        public FuncHandler Handler { get; }

        public DiscoveryRuntime Runtime { get; }

        public ListLogger<DeezerClient> Log { get; }

        public DeezerClient Client { get; }

        public async Task AdvanceAsync(TimeSpan by)
        {
            await Wait.Until(() => Clock.PendingTimers > 0, "a pending delay");
            Clock.Advance(by);
        }
    }

    private static FuncHandler Reply(string json) => FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.OK, json));

    [Fact]
    public async Task DZ01_FindArtist_exact_name_beats_fans_of_a_near_match_and_ignores_accents()
    {
        var rig = new Rig(Reply(ArtistSearch));
        var artist = await rig.Client.FindArtistAsync("RADIOHEAD", CancellationToken.None);
        Assert.Equal(new DeezerArtist(399, "Radiohead", 4122959, true), artist);

        var accent = new Rig(Reply("""{"data":[{"id":1,"name":"Beyoncé","nb_fan":5,"radio":true},{"id":2,"name":"Beyonce Fans","nb_fan":900}]}"""));
        Assert.Equal(1, (await accent.Client.FindArtistAsync("Beyonce", CancellationToken.None))!.Id);

        var none = new Rig(Reply(ArtistSearch));
        Assert.Null(await none.Client.FindArtistAsync("Radiohe", CancellationToken.None));
        Assert.Single(none.Log.Named("discovery.artist_not_found"));
    }

    [Fact]
    public async Task DZ02_Radio_rows_parse_and_rows_without_an_id_are_dropped()
    {
        var rig = new Rig(Reply(Radio));
        var tracks = await rig.Client.GetArtistRadioAsync(399, 25, CancellationToken.None);
        Assert.Equal(2, tracks.Count);
        var luna = tracks[1];
        Assert.Equal("Luna (2011 Remaster)", luna.Title);
        Assert.Equal("Luna", luna.TitleShort);
        Assert.Equal("(2011 Remaster)", luna.TitleVersion);
        Assert.Equal(202, luna.DurationS);
        Assert.Equal(193331, luna.Artist.Id);
        Assert.Equal("Siamese Dream (Deluxe Edition)", luna.Album.Title);
        Assert.Null(tracks[0].TitleVersion);
        Assert.Equal(161, tracks[0].DurationS);
        Assert.Equal(492165, tracks[0].Rank);
    }

    [Fact]
    public async Task DZ03_Related_and_top_parse()
    {
        var related = await new Rig(Reply(Related)).Client.GetRelatedArtistsAsync(399, 2, CancellationToken.None);
        Assert.Equal([664L, 193331L], related.Select(a => a.Id));
        Assert.Equal(332065, related[0].NbFan);
        Assert.True(related[0].Radio);

        var top = await new Rig(Reply(Top)).Client.GetArtistTopAsync(399, 2, CancellationToken.None);
        Assert.Equal(["Creep", "Let Down"], top.Select(t => t.Title));
        Assert.Equal(238, top[0].DurationS);
        Assert.Equal("OK Computer", top[1].Album.Title);
    }

    [Fact]
    public async Task DZ04_Track_detail_parses_isrc_position_dates_and_contributor_roles()
    {
        var detail = await new Rig(Reply(TrackDetail)).Client.GetTrackAsync(3135556, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("GBDUW0000059", detail.Isrc);
        Assert.Equal(4, detail.TrackPosition);
        Assert.Equal(1, detail.DiskNumber);
        Assert.Equal("2001-03-12", detail.ReleaseDate);
        Assert.Equal("2001-03-07", detail.Album.ReleaseDate);
        Assert.Equal(226, detail.DurationS);
        Assert.Equal([("Daft Punk", "Main"), ("Someone", "Featured")], detail.Contributors.Select(c => (c.Name, c.Role)));
    }

    [Fact]
    public async Task DZ05_Code_800_in_a_200_is_not_found_with_one_call_and_no_retry()
    {
        var rig = new Rig(Reply(NotFoundBody));
        Assert.Null(await rig.Client.GetTrackAsync(99999999999, CancellationToken.None));
        Assert.Empty(await rig.Client.GetArtistRadioAsync(99999999999, 25, CancellationToken.None));
        Assert.Equal(2, rig.Handler.Calls);
    }

    [Fact]
    public async Task DZ06_Quota_body_waits_five_seconds_retries_once_then_fails_as_quota()
    {
        var rig = new Rig(FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.OK, QuotaBody)));
        var call = rig.Client.GetArtistTopAsync(399, 5, CancellationToken.None);
        await Wait.Until(() => rig.Handler.Calls == 1, "first call");
        await Wait.Until(() => rig.Clock.PendingTimers == 1, "the 5 s wait");
        Assert.False(call.IsCompleted);

        rig.Clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(1, rig.Handler.Calls);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));

        var ex = await Assert.ThrowsAsync<DeezerException>(() => call);
        Assert.Equal(DiscoveryFailure.Quota, ex.Kind);
        Assert.Equal(2, rig.Handler.Calls);
        Assert.NotEmpty(rig.Log.Named("discovery.quota"));
    }

    [Fact]
    public async Task DZ06b_Quota_then_a_good_answer_succeeds()
    {
        var rig = new Rig(FuncHandler.Script(
            () => FuncHandler.Json(HttpStatusCode.OK, QuotaBody),
            () => FuncHandler.Json(HttpStatusCode.OK, Top)));
        var call = rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await call).Count);
        Assert.Equal(2, rig.Handler.Calls);
    }

    [Fact]
    public async Task DZ07_Http_503_retries_once_after_one_second_and_a_timeout_is_not_retried()
    {
        var rig = new Rig(FuncHandler.Script(
            () => FuncHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"),
            () => FuncHandler.Json(HttpStatusCode.OK, Top)));
        var call = rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await call).Count);
        Assert.Equal(2, rig.Handler.Calls);

        var twice = new Rig(FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.BadGateway, "{}")));
        var failing = twice.Client.GetRelatedArtistsAsync(399, 2, CancellationToken.None);
        await twice.AdvanceAsync(TimeSpan.FromSeconds(1));
        var http = await Assert.ThrowsAsync<DeezerException>(() => failing);
        Assert.Equal(502, http.StatusCode);
        Assert.Equal(2, twice.Handler.Calls);

        var slow = new Rig(FuncHandler.Hanging());
        var hang = slow.Client.GetTrackAsync(5, CancellationToken.None);
        await Wait.Until(() => slow.Handler.Calls == 1, "request sent");
        slow.Clock.Advance(TimeSpan.FromSeconds(10));
        var timeout = await Assert.ThrowsAsync<DeezerException>(() => hang);
        Assert.Equal(DiscoveryFailure.Timeout, timeout.Kind);
        Assert.Equal(1, slow.Handler.Calls);
    }

    [Fact]
    public async Task DZ07b_Caller_cancellation_is_not_a_timeout()
    {
        var rig = new Rig(FuncHandler.Hanging());
        using var cts = new CancellationTokenSource();
        var call = rig.Client.GetTrackAsync(5, cts.Token);
        await Wait.Until(() => rig.Handler.Calls == 1, "request sent");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task DZ08_Cache_serves_repeats_without_http_until_the_ttl_ends_and_remembers_a_missing_artist()
    {
        var rig = new Rig(Reply(Top));
        await rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None);
        await rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None);
        Assert.Equal(1, rig.Handler.Calls);

        rig.Clock.Advance(TimeSpan.FromHours(23));
        await rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None);
        Assert.Equal(1, rig.Handler.Calls);
        rig.Clock.Advance(TimeSpan.FromHours(2));
        await rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None);
        Assert.Equal(2, rig.Handler.Calls);

        var misses = new Rig(Reply("""{"data":[],"total":0}"""));
        Assert.Null(await misses.Client.FindArtistAsync("Nobody", CancellationToken.None));
        Assert.Null(await misses.Client.FindArtistAsync("nobody ", CancellationToken.None));
        Assert.Equal(1, misses.Handler.Calls);
        misses.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Null(await misses.Client.FindArtistAsync("Nobody", CancellationToken.None));
        Assert.Equal(2, misses.Handler.Calls);

        var found = new Rig(Reply(ArtistSearch));
        await found.Client.FindArtistAsync("Radiohead", CancellationToken.None);
        found.Clock.Advance(TimeSpan.FromDays(6));
        await found.Client.FindArtistAsync("Radiohead", CancellationToken.None);
        Assert.Equal(1, found.Handler.Calls);
        found.Clock.Advance(TimeSpan.FromDays(2));
        await found.Client.FindArtistAsync("Radiohead", CancellationToken.None);
        Assert.Equal(2, found.Handler.Calls);
    }

    [Fact]
    public async Task DZ08b_A_failed_call_is_never_cached()
    {
        var rig = new Rig(FuncHandler.Script(
            () => FuncHandler.Json(HttpStatusCode.NotFound, "{}"),
            () => FuncHandler.Json(HttpStatusCode.OK, Top)));
        await Assert.ThrowsAsync<DeezerException>(() => rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None));
        Assert.Equal(2, (await rig.Client.GetArtistTopAsync(399, 2, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task DZ09_Limiter_lets_forty_calls_through_and_holds_the_forty_first_for_the_window()
    {
        var rig = new Rig(Reply(TrackDetail));
        for (var i = 1; i <= 40; i++)
        {
            await rig.Client.GetTrackAsync(i, CancellationToken.None);
        }

        Assert.Equal(40, rig.Handler.Calls);
        var forty1 = rig.Client.GetTrackAsync(41, CancellationToken.None);
        await Wait.Until(() => rig.Clock.PendingTimers == 1, "the limiter wait");
        Assert.False(forty1.IsCompleted);
        Assert.Equal(40, rig.Handler.Calls);

        rig.Clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(forty1.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await forty1;
        Assert.Equal(41, rig.Handler.Calls);
    }

    [Fact]
    public async Task DZ10_Cache_cap_evicts_the_oldest_entry()
    {
        var rig = new Rig(Reply(TrackDetail), new DiscoveryOptions { CacheCapacity = 3 });
        for (var i = 1; i <= 4; i++)
        {
            await rig.Client.GetTrackAsync(i, CancellationToken.None);
        }

        Assert.Equal(3, rig.Runtime.Cache.Count);
        await rig.Client.GetTrackAsync(4, CancellationToken.None);
        Assert.Equal(4, rig.Handler.Calls);
        await rig.Client.GetTrackAsync(1, CancellationToken.None);
        Assert.Equal(5, rig.Handler.Calls);
    }

    [Fact]
    public async Task DZ11_Paths_and_queries_are_exact()
    {
        var rig = new Rig(Reply(ArtistSearch));
        await rig.Client.FindArtistAsync("Simon & Garfunkel", CancellationToken.None);
        await rig.Client.GetArtistRadioAsync(399, 25, CancellationToken.None);
        await rig.Client.GetRelatedArtistsAsync(399, 10, CancellationToken.None);
        await rig.Client.GetArtistTopAsync(399, 50, CancellationToken.None);
        await rig.Client.GetTrackAsync(3135556, CancellationToken.None);
        Assert.Equal(
        [
            "/search/artist?q=Simon%20%26%20Garfunkel&limit=10",
            "/artist/399/radio?limit=25",
            "/artist/399/related?limit=10",
            "/artist/399/top?limit=50",
            "/track/3135556",
        ], rig.Handler.Sent.Select(s => s.PathAndQuery));
        Assert.All(rig.Handler.Sent, s => Assert.False(s.HasAuthorization));
    }

    [Fact]
    public async Task DZ12_Logs_and_errors_never_hold_preview_links_or_track_tokens()
    {
        var rig = new Rig(FuncHandler.Script(
            () => FuncHandler.Json(HttpStatusCode.OK, Radio),
            () => FuncHandler.Json(HttpStatusCode.OK, TrackDetail),
            () => FuncHandler.Json(HttpStatusCode.OK, QuotaBody),
            () => FuncHandler.Json(HttpStatusCode.OK, QuotaBody)));
        await rig.Client.GetArtistRadioAsync(399, 25, CancellationToken.None);
        await rig.Client.GetTrackAsync(3135556, CancellationToken.None);
        var call = rig.Client.GetArtistTopAsync(399, 5, CancellationToken.None);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<DeezerException>(() => call);

        var everything = string.Join('\n', rig.Log.Entries.Select(e => e.Message + string.Join(',', e.Fields.Values))) + ex.Message;
        foreach (var secret in new[] { "dzcdn", "hmac", "SECRET", "track_token", "AAAAA-signed-token", "preview", "deezer.com/track" })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    [Fact]
    public void Cache_entries_expire_and_a_stored_null_is_an_answer()
    {
        var clock = new ManualClock();
        var cache = new TtlCache(10, clock);
        cache.Set("a", null, TimeSpan.FromMinutes(1));
        Assert.True(cache.TryGet<string>("a", out var value));
        Assert.Null(value);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cache.TryGet<string>("a", out _));
        Assert.Equal(0, cache.Count);
    }
}
