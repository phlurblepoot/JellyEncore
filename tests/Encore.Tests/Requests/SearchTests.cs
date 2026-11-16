using System.Net;
using System.Text.Json;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Requests.Search;
using Encore.Store;
using Encore.Tests.Discovery;
using Encore.Tests.Jellyfin;
using Encore.Tests.Watch;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Tests.Requests;

/// <summary>v03 section 15, T85: song and library search (the Deezer client, the Jellyfin call, SearchService and GET /api/v1/search).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SearchTests
{
    private const string DeezerSearch = """
        {"data":[
        {"id":3135556,"readable":true,"title":"Harder, Better, Faster, Stronger","title_short":"Harder, Better, Faster, Stronger","title_version":"","duration":226,"explicit_lyrics":false,
         "preview":"https://cdnt-preview.dzcdn.net/y.mp3?hdnea=exp=1~hmac=PREVIEWSECRET","md5_image":"abc",
         "artist":{"id":27,"name":"Daft Punk","type":"artist"},
         "album":{"id":302127,"title":"Discovery","cover":"https://x/c.jpg","cover_medium":"https://cdn-images.dzcdn.net/images/cover/abc/250x250-000000-80-0-0.jpg","type":"album"},"type":"track"},
        {"id":15715644,"title":"Luna (2011 Remaster)","title_short":"Luna","title_version":"(2011 Remaster)","duration":202,"explicit_lyrics":true,
         "preview":"https://cdnt-preview.dzcdn.net/z.mp3?hdnea=exp=1~hmac=PREVIEWSECRET",
         "artist":{"id":193331,"name":"The Smashing Pumpkins"},
         "album":{"id":1452137,"title":"Siamese Dream","cover_medium":"http://insecure/c.jpg"},"type":"track"},
        {"id":99,"title":"Aerodynamic","title_short":"Aerodynamic","title_version":"Live","duration":212,
         "artist":{"id":27,"name":"Daft Punk"},"album":{"id":302127,"title":"Alive 2007"},"type":"track"},
        {"title":"Row without an id","artist":{"id":1,"name":"X"}}],
        "total":3}
        """;

    private static readonly Guid SongItem = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid AlbumItem = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid OwnedItem = Guid.Parse("33333333333333333333333333333333");
    private static readonly Guid ArtistId = Guid.Parse("44444444444444444444444444444444");

    private static DeezerSearchTrack Row(long id, string artist, string title, string? version = null, int seconds = 200) =>
        new(id, title, version, artist, "Album " + id, seconds, "https://cdn-images.dzcdn.net/x.jpg", false);

    private static BaseItemDto Song(Guid id, string artist, string title) => new()
    {
        Id = id,
        Name = title,
        Type = "Audio",
        Album = "Some album",
        ArtistItems = [new NameGuidPair { Id = ArtistId, Name = artist }],
        AlbumArtist = artist,
    };

    private static BaseItemDto Album(Guid id, string artist, string title, int year) => new()
    {
        Id = id,
        Name = title,
        Type = "MusicAlbum",
        AlbumArtist = artist,
        ProductionYear = year,
    };

    private static JsonElement[] Items(JsonElement array) => array.EnumerateArray().ToArray();

    // ---- the Deezer client ------------------------------------------------------------------------------

    private sealed class DeezerRig
    {
        public DeezerRig(FuncHandler handler)
        {
            Handler = handler;
            Runtime = new DiscoveryRuntime(null, Clock);
            Client = new DeezerClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com/"), Timeout = Timeout.InfiniteTimeSpan },
                Runtime, new ListLogger<DeezerClient>());
        }

        public ManualClock Clock { get; } = new();

        public FuncHandler Handler { get; }

        public DiscoveryRuntime Runtime { get; }

        public DeezerClient Client { get; }
    }

    private static FuncHandler Reply(string json) => FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.OK, json));

    [Fact]
    public async Task SE01_Deezer_search_asks_for_the_query_and_limit_and_reads_only_the_fields_the_rows_need()
    {
        var rig = new DeezerRig(Reply(DeezerSearch));

        var rows = await rig.Client.SearchTracksAsync("  daft punk & co ", 25, CancellationToken.None);

        Assert.Equal("/search?q=daft%20punk%20%26%20co&limit=25", rig.Handler.Sent[0].PathAndQuery);
        Assert.Equal(3, rows.Count);
        var first = rows[0];
        Assert.Equal(3135556, first.Id);
        Assert.Equal("Harder, Better, Faster, Stronger", first.Title);
        Assert.Null(first.TitleVersion);
        Assert.Equal("Daft Punk", first.ArtistName);
        Assert.Equal("Discovery", first.AlbumTitle);
        Assert.Equal(226, first.DurationS);
        Assert.Equal("https://cdn-images.dzcdn.net/images/cover/abc/250x250-000000-80-0-0.jpg", first.CoverUrl);
        Assert.False(first.Explicit);
        Assert.Equal("(2011 Remaster)", rows[1].TitleVersion);
        Assert.True(rows[1].Explicit);
        Assert.Null(rows[1].CoverUrl);
        Assert.Equal("Live", rows[2].TitleVersion);
    }

    [Fact]
    public void SE02_The_search_row_type_has_no_preview_or_year_member()
    {
        var names = typeof(DeezerSearchTrack).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("preview", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("year", StringComparison.Ordinal) || n.Contains("date", StringComparison.Ordinal));
        var rowNames = typeof(SongRow).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(rowNames, n => n.Contains("preview", StringComparison.Ordinal) || n.Contains("year", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SE03_Deezer_search_is_cached_by_the_folded_query_and_limit_and_failures_are_not()
    {
        var rig = new DeezerRig(Reply(DeezerSearch));
        await rig.Client.SearchTracksAsync("Daft Punk", 25, CancellationToken.None);
        await rig.Client.SearchTracksAsync("  daft   PUNK ", 25, CancellationToken.None);
        Assert.Equal(1, rig.Handler.Calls);

        await rig.Client.SearchTracksAsync("daft punk", 10, CancellationToken.None);
        Assert.Equal(2, rig.Handler.Calls);

        var failing = new DeezerRig(FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.OK, "{\"error\":{\"code\":100}}")));
        await Assert.ThrowsAsync<DeezerException>(() => failing.Client.SearchTracksAsync("x y", 25, CancellationToken.None));
        await Assert.ThrowsAsync<DeezerException>(() => failing.Client.SearchTracksAsync("x y", 25, CancellationToken.None));
        Assert.Equal(2, failing.Handler.Calls);
    }

    [Fact]
    public async Task SE04_Deezer_search_takes_a_slot_from_the_shared_limiter()
    {
        var options = new DiscoveryOptions { DeezerLimit = 1, DeezerWindow = TimeSpan.FromSeconds(5) };
        var handler = Reply(DeezerSearch);
        var clock = new ManualClock();
        var runtime = new DiscoveryRuntime(options, clock);
        var client = new DeezerClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com/"), Timeout = Timeout.InfiniteTimeSpan }, runtime, new ListLogger<DeezerClient>());

        await client.SearchTracksAsync("one", 25, CancellationToken.None);
        var second = client.SearchTracksAsync("two", 25, CancellationToken.None);
        await Wait.Until(() => clock.PendingTimers > 0, "the limiter wait");
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, handler.Calls);
    }

    // ---- the Jellyfin call ------------------------------------------------------------------------------

    [Fact]
    public async Task SE05_Jellyfin_search_sends_the_term_user_types_and_limit()
    {
        var script = new ScriptedHandler();
        var http = new HttpClient(script) { BaseAddress = new Uri("http://jellyfin.test:8096/") };
        var client = new JellyfinClient(http, new JellyfinClientOptions { RetryDelays = [], Jitter = 0 });
        script.Reply(HttpStatusCode.OK, """
            {"Items":[{"Id":"11111111111111111111111111111111","Name":"One More Time","Type":"Audio","Album":"Discovery","AlbumArtist":"Daft Punk"},
            {"Id":"22222222222222222222222222222222","Name":"Discovery","Type":"MusicAlbum","AlbumArtist":"Daft Punk","ProductionYear":2001}],"TotalRecordCount":2}
            """);

        var result = await client.SearchItemsAsync("daft & punk", Guid.Parse("bbbb0000bbbb0000bbbb0000bbbb0000"), 20);

        Assert.Equal(
            "/Items?searchTerm=daft%20%26%20punk&includeItemTypes=Audio,MusicAlbum&recursive=true&limit=20&userId=bbbb0000bbbb0000bbbb0000bbbb0000",
            script.Sent[0].PathAndQuery);
        Assert.Equal("Daft Punk", result.Items[0].AlbumArtist);
        Assert.Equal(2001, result.Items[1].ProductionYear);
    }

    // ---- the endpoint -----------------------------------------------------------------------------------

    private static BaseItemDto OneSong() => Song(SongItem, "Some Band", "Some Song");

    [Fact]
    public async Task SE10_Library_and_Deezer_are_merged_and_the_albums_part_is_empty()
    {
        using var rig = new ApiRig();
        rig.Fake.SearchResult = [OneSong(), Album(AlbumItem, "Daft Punk", "Discovery", 2001)];
        rig.Deezer.SearchRows.Add(Row(1, "Daft Punk", "Around the World", seconds: 429));

        using var response = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ApiRig.JsonOf(response);
        var librarySong = Items(json.GetProperty("library").GetProperty("songs")).Single();
        Assert.Equal("11111111111111111111111111111111", librarySong.GetProperty("itemId").GetString());
        Assert.Equal("Some Band", librarySong.GetProperty("artist").GetString());
        Assert.Equal("Some Song", librarySong.GetProperty("title").GetString());
        Assert.Equal("Some album", librarySong.GetProperty("album").GetString());
        var libraryAlbum = Items(json.GetProperty("library").GetProperty("albums")).Single();
        Assert.Equal("Discovery", libraryAlbum.GetProperty("title").GetString());
        Assert.Equal(2001, libraryAlbum.GetProperty("year").GetInt32());
        var song = Items(json.GetProperty("songs")).Single();
        Assert.Equal(1, song.GetProperty("deezerTrackId").GetInt64());
        Assert.Equal("Around the World", song.GetProperty("title").GetString());
        Assert.Equal("Daft Punk", song.GetProperty("artist").GetString());
        Assert.Equal(429, song.GetProperty("durationS").GetInt32());
        Assert.False(song.GetProperty("owned").GetBoolean());
        Assert.True(song.GetProperty("requestable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, song.GetProperty("request").ValueKind);
        Assert.Empty(Items(json.GetProperty("albums")));
        Assert.Empty(Items(json.GetProperty("warnings")));
        Assert.Equal((ApiRig.Kim, 20), (rig.Fake.SearchCalls.Single().UserId, rig.Fake.SearchCalls.Single().Limit));
        Assert.Contains(rig.Deezer.Calls, c => c == "search:daft:25");
    }

    [Fact]
    public async Task SE11_A_Deezer_row_the_server_owns_is_marked_with_its_item_and_is_not_requestable()
    {
        using var rig = new ApiRig();
        rig.Owned.Add(new OwnedEntry(OwnedItem, TrackKey.Create("Daft Punk", "One More Time")!.Value, "One More Time", null, "Daft Punk", 320, "/music/a.flac", null, false));
        rig.Deezer.SearchRows.Add(Row(1, "Daft Punk", "One More Time", seconds: 320));
        rig.Deezer.SearchRows.Add(Row(2, "Daft Punk", "Aerodynamic"));

        using var response = await rig.GetAsync("/api/v1/search?q=daft+punk", bearer: ApiRig.KimToken);

        var songs = Items((await ApiRig.JsonOf(response)).GetProperty("songs"));
        Assert.True(songs[0].GetProperty("owned").GetBoolean());
        Assert.Equal("33333333333333333333333333333333", songs[0].GetProperty("itemId").GetString());
        Assert.False(songs[0].GetProperty("requestable").GetBoolean());
        Assert.False(songs[1].GetProperty("owned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, songs[1].GetProperty("itemId").ValueKind);
    }

    [Fact]
    public async Task SE12_A_song_found_in_the_library_results_counts_as_owned_even_before_the_index_has_it()
    {
        using var rig = new ApiRig();
        rig.Fake.SearchResult = [Song(SongItem, "Daft Punk", "One More Time")];
        rig.Deezer.SearchRows.Add(Row(1, "Daft Punk", "One More Time"));

        using var response = await rig.GetAsync("/api/v1/search?q=one+more", bearer: ApiRig.KimToken);

        var song = Items((await ApiRig.JsonOf(response)).GetProperty("songs")).Single();
        Assert.True(song.GetProperty("owned").GetBoolean());
        Assert.Equal("11111111111111111111111111111111", song.GetProperty("itemId").GetString());
    }

    [Fact]
    public async Task SE13_An_active_request_is_attached_and_says_whether_it_is_the_callers()
    {
        using var rig = new ApiRig();
        var store = rig.Services.GetRequiredService<RequestStore>();
        var key = TrackKey.Create("Daft Punk", "Around the World")!.Value.Value;
        store.Insert(new NewRequest("req-kim", "song", "bbbb0000bbbb0000bbbb0000bbbb0000", "kim", "Daft Punk", DateTimeOffset.UtcNow, Title: "Around the World", TrackKey: key, DeezerTrackId: 1));
        store.Update("req-kim", RequestState.Downloading, DateTimeOffset.UtcNow, new RequestUpdate(Detail: "Downloading FLAC from a peer"));
        var done = TrackKey.Create("Daft Punk", "Aerodynamic")!.Value.Value;
        store.Insert(new NewRequest("req-old", "song", "cccc0000cccc0000cccc0000cccc0000", "lee", "Daft Punk", DateTimeOffset.UtcNow, Title: "Aerodynamic", TrackKey: done, DeezerTrackId: 2));
        store.Update("req-old", RequestState.Ready, DateTimeOffset.UtcNow);
        rig.Deezer.SearchRows.Add(Row(1, "Daft Punk", "Around the World"));
        rig.Deezer.SearchRows.Add(Row(2, "Daft Punk", "Aerodynamic"));

        using var mine = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);
        using var theirs = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.LeeToken);

        var kimSongs = Items((await ApiRig.JsonOf(mine)).GetProperty("songs"));
        var request = kimSongs[0].GetProperty("request");
        Assert.Equal("req-kim", request.GetProperty("id").GetString());
        Assert.Equal("downloading", request.GetProperty("state").GetString());
        Assert.Equal("Downloading FLAC from a peer", request.GetProperty("detail").GetString());
        Assert.True(request.GetProperty("mine").GetBoolean());
        Assert.Equal(JsonValueKind.Null, kimSongs[1].GetProperty("request").ValueKind);
        var leeSongs = Items((await ApiRig.JsonOf(theirs)).GetProperty("songs"));
        Assert.False(leeSongs[0].GetProperty("request").GetProperty("mine").GetBoolean());
    }

    [Fact]
    public async Task SE14_A_failing_Jellyfin_gives_a_warning_and_still_returns_the_Deezer_songs()
    {
        using var rig = new ApiRig();
        rig.Fake.SearchFailure = new JellyfinRequestException("SearchItems", 503);
        rig.Deezer.SearchRows.Add(Row(1, "Daft Punk", "Around the World"));

        using var response = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ApiRig.JsonOf(response);
        Assert.Equal(["jellyfin: failed"], Items(json.GetProperty("warnings")).Select(w => w.GetString()));
        Assert.Single(Items(json.GetProperty("songs")));
        Assert.Empty(Items(json.GetProperty("library").GetProperty("songs")));
        Assert.Single(rig.Logs.Entries, e => e.EventId.Name == "request.search_source_failed");
    }

    [Fact]
    public async Task SE15_A_failing_Deezer_gives_a_warning_and_still_returns_the_library()
    {
        using var rig = new ApiRig();
        rig.Fake.SearchResult = [OneSong()];
        rig.Deezer.Failure = new DeezerException("/search", DiscoveryFailure.Quota, "quota");

        using var response = await rig.GetAsync("/api/v1/search?q=some", bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ApiRig.JsonOf(response);
        Assert.Equal(["deezer: failed"], Items(json.GetProperty("warnings")).Select(w => w.GetString()));
        Assert.Empty(Items(json.GetProperty("songs")));
        Assert.Single(Items(json.GetProperty("library").GetProperty("songs")));
    }

    [Fact]
    public async Task SE16_A_source_that_hangs_is_cut_off_by_its_own_timeout()
    {
        using var rig = new ApiRig();
        rig.SearchTimeouts = new SearchOptions { JellyfinTimeout = TimeSpan.FromMilliseconds(150), DeezerTimeout = TimeSpan.FromMilliseconds(150) };
        rig.Fake.SearchHang = true;
        rig.Deezer.Hang = true;

        using var response = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warnings = Items((await ApiRig.JsonOf(response)).GetProperty("warnings")).Select(w => w.GetString()).Order().ToList();
        Assert.Equal(["deezer: timeout", "jellyfin: timeout"], warnings);
    }

    [Theory]
    [InlineData("/api/v1/search", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/search?q=", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/search?q=a", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/search?q=%20a%20", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/search?q=ab", HttpStatusCode.OK)]
    [InlineData("/api/v1/search?q=ab&kinds=song,album", HttpStatusCode.OK)]
    [InlineData("/api/v1/search?q=ab&kinds=song", HttpStatusCode.OK)]
    [InlineData("/api/v1/search?q=ab&kinds=video", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/search?q=ab&kinds=song,movie", HttpStatusCode.BadRequest)]
    public async Task SE17_The_query_and_kinds_rules(string url, HttpStatusCode expected)
    {
        using var rig = new ApiRig();
        using var response = await rig.GetAsync(url, bearer: ApiRig.KimToken);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Equal("bad_request", (await ApiRig.JsonOf(response)).GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task SE18_A_query_of_100_characters_is_fine_and_101_is_not()
    {
        using var rig = new ApiRig();
        using var ok = await rig.GetAsync("/api/v1/search?q=" + new string('a', 100), bearer: ApiRig.KimToken);
        using var tooLong = await rig.GetAsync("/api/v1/search?q=" + new string('a', 101), bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task SE19_Search_needs_a_token()
    {
        using var rig = new ApiRig();
        using var response = await rig.GetAsync("/api/v1/search?q=daft");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(rig.Fake.SearchCalls);
        Assert.Empty(rig.Deezer.Calls);
    }

    [Fact]
    public async Task SE20_Searching_for_albums_only_does_not_ask_Deezer_and_returns_no_songs()
    {
        using var rig = new ApiRig();
        rig.Fake.SearchResult = [OneSong(), Album(AlbumItem, "Daft Punk", "Discovery", 2001)];
        rig.Deezer.SearchRows.Add(Row(1, "Daft Punk", "Around the World"));

        using var response = await rig.GetAsync("/api/v1/search?q=daft&kinds=album", bearer: ApiRig.KimToken);

        var json = await ApiRig.JsonOf(response);
        Assert.Empty(Items(json.GetProperty("songs")));
        Assert.Empty(Items(json.GetProperty("library").GetProperty("songs")));
        Assert.Single(Items(json.GetProperty("library").GetProperty("albums")));
        Assert.Empty(rig.Deezer.Calls);
    }

    [Fact]
    public async Task SE21_The_version_is_shown_in_brackets_unless_the_title_has_it()
    {
        Assert.Equal("Aerodynamic (Live)", SearchService.DisplayTitle("Aerodynamic", "Live"));
        Assert.Equal("Aerodynamic (Live)", SearchService.DisplayTitle("Aerodynamic", "(Live)"));
        Assert.Equal("Luna (2011 Remaster)", SearchService.DisplayTitle("Luna (2011 Remaster)", "(2011 Remaster)"));
        Assert.Equal("Luna", SearchService.DisplayTitle("Luna", null));
        Assert.Equal("Luna", SearchService.DisplayTitle("Luna", "  "));
        Assert.Equal("Luna", SearchService.DisplayTitle("Luna", "()"));

        using var rig = new ApiRig();
        rig.Deezer.SearchRows.Add(Row(1, "Daft Punk", "Aerodynamic", "Live"));
        using var response = await rig.GetAsync("/api/v1/search?q=aero", bearer: ApiRig.KimToken);
        var song = Items((await ApiRig.JsonOf(response)).GetProperty("songs")).Single();
        Assert.Equal("Aerodynamic (Live)", song.GetProperty("title").GetString());
    }

    [Fact]
    public async Task SE22_The_preview_link_is_not_in_the_response_the_store_or_the_logs()
    {
        using var rig = new ApiRig();
        var rig2 = new DeezerRig(Reply(DeezerSearch));
        // Real parsing of a body that carries signed preview links, handed to the service through the fake.
        rig.Deezer.SearchRows.AddRange(await rig2.Client.SearchTracksAsync("daft", 25, CancellationToken.None));

        using var response = await rig.GetAsync("/api/v1/search?q=daft", bearer: ApiRig.KimToken);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Harder, Better, Faster, Stronger", body, StringComparison.Ordinal);
        Assert.DoesNotContain("preview", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PREVIEWSECRET", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PREVIEWSECRET", rig.Logs.Everything(), StringComparison.Ordinal);
        Assert.DoesNotContain("hdnea", rig.Logs.Everything(), StringComparison.Ordinal);
        Assert.Empty(rig.Services.GetRequiredService<RequestStore>().ListAll());
    }
}
