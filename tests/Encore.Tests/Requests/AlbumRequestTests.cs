using System.Net;
using System.Text.Json.Nodes;
using Encore.Config;
using Encore.Jellyfin;
using Encore.Lidarr;
using Encore.Requests;
using Encore.Requests.Albums;
using Encore.Requests.Auth;
using Encore.Requests.Search;
using Encore.Store;
using Encore.Tests.Discovery;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Tests.Requests;

/// <summary>An album requester over real stores (temp folder), the in-memory Lidarr, a fake Jellyfin album library and a manual clock.</summary>
internal sealed class AlbumRig : IDisposable
{
    public const string OkComputer = "b1392450-e666-3926-a536-22c65f834433";
    public const string KidA = "c0000000-0000-0000-0000-00000000000a";
    public const string Amnesiac = "c0000000-0000-0000-0000-00000000000b";
    public const string Unreleased = "c0000000-0000-0000-0000-00000000000c";
    public const string Radiohead = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-albumreq-" + Guid.NewGuid().ToString("N"));
    private readonly EncoreStore _main;

    public AlbumRig(int perDay = 2, string? rootFolder = null, PathMapping? map = null)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "encore.db");
        _main = EncoreStore.Open(path, Clock);
        Store = RequestStore.Open(path);
        Settings = new AlbumRequestSettings(perDay, rootFolder, map ?? new PathMapping("/media/music", "/media/music"));
        Lidarr.AddCatalog(OkComputer, "OK Computer", Radiohead, "Radiohead");
        Lidarr.AddCatalog(KidA, "Kid A", Radiohead, "Radiohead", new DateTimeOffset(2000, 10, 2, 0, 0, 0, TimeSpan.Zero), 10);
        Lidarr.AddCatalog(Amnesiac, "Amnesiac", Radiohead, "Radiohead", new DateTimeOffset(2001, 6, 5, 0, 0, 0, TimeSpan.Zero), 11);
        Lidarr.AddCatalog(Unreleased, "Next Album", Radiohead, "Radiohead", new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero), 9);
        Requester = new AlbumRequester(Store, Lidarr, Library, Settings, Log, Clock);
    }

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    public FakeLidarrApi Lidarr { get; } = new();

    public FakeAlbumLibrary Library { get; } = new();

    public RequestStore Store { get; }

    public AlbumRequestSettings Settings { get; }

    public ListLogger<AlbumRequester> Log { get; } = new();

    public AlbumRequester Requester { get; }

    public async Task<RequestRow> CreateAsync(string fid, RequestUser? user = null, HttpStatusCode expect = HttpStatusCode.Created)
    {
        var (outcome, _) = await Requester.CreateAsync(user ?? SongRig.Kim, fid, CancellationToken.None);
        Assert.True((int)expect == outcome.Status, $"expected {(int)expect}, got {outcome.Status} {outcome.Error}");
        return outcome.Row!;
    }

    public async Task<string?> FailAsync(string fid, RequestUser? user, HttpStatusCode expect)
    {
        var (outcome, _) = await Requester.CreateAsync(user ?? SongRig.Kim, fid, CancellationToken.None);
        Assert.Equal((int)expect, outcome.Status);
        Assert.Null(outcome.Row);
        return outcome.Error;
    }

    /// <summary>Lets the 2-min poll come due and runs one pass.</summary>
    public async Task<IReadOnlyList<RequestRow>> PollAsync()
    {
        Clock.Advance(RequestLimits.AlbumPoll + TimeSpan.FromSeconds(1));
        return await Requester.TickAsync(CancellationToken.None);
    }

    public RequestRow Row(string id) => Store.Get(id)!;

    public static LidarrAlbumStatistics Stats(int files, int expected, int total) =>
        new(files, expected, total, files * 1000L, expected == 0 ? 0 : files * 100.0 / expected);

    public void Dispose()
    {
        Store.Dispose();
        _main.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>v03 section 15, T90: album requests through Lidarr (create, send, progress, timeouts, cancel, retry, search rows, route).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AlbumRequestTests
{
    private static readonly Guid JellyfinAlbum = Guid.Parse("66666666666666666666666666666666");

    // ---- send to Lidarr (design 7.2) ------------------------------------------------------------------------------

    [Fact]
    public async Task AR01_A_missing_artist_is_added_by_exactly_one_POST_album_with_monitored_artist_and_no_addOptions()
    {
        using var rig = new AlbumRig();

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(RequestState.Searching, row.State);
        Assert.Equal(AlbumRequester.Details.Searching, row.Detail);
        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer }, rig.Lidarr.Writes);
        var post = rig.Lidarr.Posts.Single();
        Assert.Equal(AlbumRig.OkComputer, post.ForeignAlbumId);
        Assert.Equal("OK Computer", post.Title);
        var artist = post.Artist;
        Assert.True(artist["monitored"]!.GetValue<bool>());
        Assert.Equal("none", artist["monitorNewItems"]!.GetValue<string>());
        Assert.Equal(3, artist["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(2, artist["metadataProfileId"]!.GetValue<int>());
        Assert.Equal("/media/music", artist["rootFolderPath"]!.GetValue<string>());
        Assert.Equal(AlbumRig.Radiohead, artist["foreignArtistId"]!.GetValue<string>());
        Assert.False(artist.ContainsKey("addOptions"));
        Assert.DoesNotContain(rig.Lidarr.Writes, w => w.StartsWith("POST artist", StringComparison.Ordinal));

        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        Assert.True(album.Monitored);
        Assert.True(rig.Lidarr.ArtistOf(album.ArtistId).Monitored);
        Assert.Equal(album.Id, row.LidarrAlbumId);
        Assert.Equal(album.ArtistId, row.LidarrArtistId);
        Assert.Single(rig.Log.Named("lidarr.artist_added"));
        Assert.Equal("add_album", rig.Log.Named("lidarr.album_monitored").Single().Fields["Via"]);
    }

    [Fact]
    public async Task AR02_Existing_unmonitored_artist_and_album_get_PUT_monitor_and_an_artist_PUT_with_only_monitored_changed()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: false);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: false);
        var before = (JsonObject)artist.Raw.DeepClone();

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(RequestState.Searching, row.State);
        Assert.Equal(new[] { $"PUT album/monitor [{album.Id}] true", $"PUT artist/{artist.Id}" }, rig.Lidarr.Writes);
        var put = rig.Lidarr.ArtistPuts.Single();
        before["monitored"] = true;
        Assert.True(JsonNode.DeepEquals(before, put), put.ToJsonString());
        Assert.Equal("/media/music/Radiohead", put["path"]!.GetValue<string>());
        Assert.Equal("all", put["monitorNewItems"]!.GetValue<string>());
        Assert.Equal("existing", rig.Log.Named("lidarr.album_monitored").Single().Fields["Via"]);
        Assert.Empty(rig.Log.Named("lidarr.artist_added"));
    }

    [Fact]
    public async Task AR03_A_monitored_artist_is_left_alone()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: false);

        await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(new[] { $"PUT album/monitor [{album.Id}] true" }, rig.Lidarr.Writes);
    }

    [Fact]
    public async Task AR04_Existing_artist_without_the_album_gets_POST_album_with_the_artist_as_GET_returned_it()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(RequestState.Searching, row.State);
        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer }, rig.Lidarr.Writes);
        Assert.True(JsonNode.DeepEquals(artist.Raw, rig.Lidarr.Posts.Single().Artist));
        Assert.Empty(rig.Log.Named("lidarr.artist_added"));
        Assert.Equal(1, rig.Lidarr.Calls.Count(c => c == "artist?mbId"));
    }

    [Fact]
    public async Task AR05_POST_album_already_added_falls_back_to_PUT_monitor()
    {
        using var rig = new AlbumRig();
        rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        rig.Lidarr.PostRace = true;

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer, $"PUT album/monitor [{album.Id}] true" }, rig.Lidarr.Writes);
        Assert.Equal(RequestState.Searching, row.State);
        Assert.Equal(album.Id, row.LidarrAlbumId);
        Assert.Equal("existing", rig.Log.Named("lidarr.album_monitored").Single().Fields["Via"]);
    }

    [Fact]
    public async Task AR06_The_after_check_fails_the_request_when_the_artist_stays_unmonitored_and_unmonitors_the_album()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: false);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: false);
        rig.Lidarr.IgnoreArtistMonitor = true;

        var (outcome, notify) = await rig.Requester.CreateAsync(SongRig.Kim, AlbumRig.OkComputer, CancellationToken.None);

        Assert.Equal(201, outcome.Status);
        Assert.Equal(RequestState.Failed, outcome.Row!.State);
        Assert.Equal(AlbumReasons.LidarrRejected, outcome.Row.FailReason);
        Assert.Equal(
            new[] { $"PUT album/monitor [{album.Id}] true", $"PUT artist/{artist.Id}", $"PUT album/monitor [{album.Id}] false" },
            rig.Lidarr.Writes);
        Assert.False(album.Monitored);
        Assert.Contains(rig.Log.Named("lidarr.request_failed"), e => (string?)e.Fields["Operation"] == "after_check");
        Assert.Single(notify);
    }

    [Fact]
    public async Task AR07_Other_monitored_missing_albums_of_the_artist_are_logged_and_the_request_goes_on()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: false);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: false);
        var other = rig.Lidarr.AddAlbum(AlbumRig.KidA, artist, monitored: true, AlbumRig.Stats(0, 10, 10));
        var quiet = rig.Lidarr.AddAlbum(AlbumRig.Amnesiac, artist, monitored: false, AlbumRig.Stats(0, 0, 11));

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(RequestState.Searching, row.State);
        var warn = rig.Log.Named("lidarr.artist_other_wanted").Single();
        Assert.Equal(1, warn.Fields["OtherWantedAlbums"]);
        Assert.Equal(new[] { $"PUT album/monitor [{album.Id}] true", $"PUT artist/{artist.Id}" }, rig.Lidarr.Writes);
        Assert.True(other.Monitored);
        Assert.False(quiet.Monitored);
    }

    [Fact]
    public async Task AR08_A_root_folder_is_needed_for_a_new_artist()
    {
        using var rig = new AlbumRig();
        rig.Lidarr.Roots.Add(new LidarrRootFolder(2, "/media/other", 1, 1, true));

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(RequestState.Failed, row.State);
        Assert.Equal(AlbumReasons.RootFolder, row.FailReason);
        Assert.Empty(rig.Lidarr.Writes);

        using var configured = new AlbumRig(rootFolder: "/media/other/");
        configured.Lidarr.Roots.Add(new LidarrRootFolder(2, "/media/other", 4, 5, true));
        var ok = await configured.CreateAsync(AlbumRig.OkComputer);
        Assert.Equal(RequestState.Searching, ok.State);
        var body = configured.Lidarr.Posts.Single().Artist;
        Assert.Equal("/media/other", body["rootFolderPath"]!.GetValue<string>());
        Assert.Equal(4, body["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(5, body["metadataProfileId"]!.GetValue<int>());
    }

    // ---- create rules (design 7.1) ----------------------------------------------------------------------------------

    [Fact]
    public async Task AR10_No_approval_a_listener_starts_at_once_and_the_daily_cap_answers_429()
    {
        using var rig = new AlbumRig(perDay: 2);

        Assert.Equal(RequestState.Searching, (await rig.CreateAsync(AlbumRig.OkComputer)).State);
        Assert.Equal(RequestState.Searching, (await rig.CreateAsync(AlbumRig.KidA)).State);
        Assert.Equal("daily_limit", await rig.FailAsync(AlbumRig.Amnesiac, SongRig.Kim, HttpStatusCode.TooManyRequests));
        Assert.Single(rig.Log.Named("request.limit"));

        // An admin has no cap; the window is a rolling 24 h.
        Assert.Equal(RequestState.Searching, (await rig.CreateAsync(AlbumRig.Amnesiac, SongRig.Nate)).State);
        rig.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        rig.Lidarr.AddCatalog("c0000000-0000-0000-0000-00000000000d", "Hail to the Thief", AlbumRig.Radiohead, "Radiohead");
        await rig.CreateAsync("c0000000-0000-0000-0000-00000000000d");

        using var off = new AlbumRig(perDay: 0);
        Assert.Equal("daily_limit", await off.FailAsync(AlbumRig.OkComputer, SongRig.Kim, HttpStatusCode.TooManyRequests));
        Assert.Empty(off.Lidarr.Calls);
    }

    [Fact]
    public async Task AR11_An_unreleased_album_is_409_with_no_row_and_no_count()
    {
        using var rig = new AlbumRig(perDay: 1);

        Assert.Equal("not_released", await rig.FailAsync(AlbumRig.Unreleased, SongRig.Kim, HttpStatusCode.Conflict));
        Assert.Empty(rig.Store.ListAll());
        Assert.Empty(rig.Lidarr.Writes);

        // No release date at all is never wanted in Lidarr either.
        rig.Lidarr.AddCatalog("c0000000-0000-0000-0000-0000000000ee", "Undated", AlbumRig.Radiohead, "Radiohead");
        rig.Lidarr.Catalog["c0000000-0000-0000-0000-0000000000ee"] = rig.Lidarr.Catalog["c0000000-0000-0000-0000-0000000000ee"] with { Release = null };
        Assert.Equal("not_released", await rig.FailAsync("c0000000-0000-0000-0000-0000000000ee", SongRig.Kim, HttpStatusCode.Conflict));

        // The one request of the day is still free.
        await rig.CreateAsync(AlbumRig.OkComputer);
    }

    [Fact]
    public async Task AR12_Unknown_or_bad_ids_and_an_unreachable_Lidarr_are_errors_without_a_row()
    {
        using var rig = new AlbumRig();

        Assert.Equal("not_found", await rig.FailAsync("d0000000-0000-0000-0000-000000000000", SongRig.Kim, HttpStatusCode.NotFound));
        Assert.Equal("bad_request", await rig.FailAsync("not-a-guid", SongRig.Kim, HttpStatusCode.BadRequest));
        rig.Lidarr.Down = new LidarrRequestException("LookupAlbums", "timeout");
        Assert.Equal("albums_unavailable", await rig.FailAsync(AlbumRig.OkComputer, SongRig.Kim, HttpStatusCode.ServiceUnavailable));
        rig.Lidarr.Down = new LidarrAuthException("LookupAlbums", 401);
        Assert.Equal("albums_unavailable", await rig.FailAsync(AlbumRig.OkComputer, SongRig.Kim, HttpStatusCode.ServiceUnavailable));
        Assert.Empty(rig.Store.ListAll());
    }

    [Fact]
    public async Task AR13_An_owned_album_is_ready_at_once_with_no_Lidarr_write()
    {
        using var rig = new AlbumRig();
        rig.Library.Albums.Add(("Radiohead", "OK Computer (Deluxe Edition)", JellyfinAlbum));

        var (outcome, notify) = await rig.Requester.CreateAsync(SongRig.Kim, AlbumRig.OkComputer, CancellationToken.None);

        Assert.Equal(201, outcome.Status);
        Assert.Equal(RequestState.Ready, outcome.Row!.State);
        Assert.Equal(JellyfinAlbum.ToString("N"), outcome.Row.JellyfinItemId);
        Assert.Equal(SongRig.Kim.Id, rig.Library.Finds.Single().User);
        Assert.Single(notify);
        Assert.Empty(rig.Lidarr.Writes);

        // Complete in Lidarr (trackCount > 0, every track has a file) is owned too.
        using var complete = new AlbumRig();
        var artist = complete.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        complete.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: true, AlbumRig.Stats(12, 12, 12));
        var row = await complete.CreateAsync(AlbumRig.OkComputer);
        Assert.Equal(RequestState.Ready, row.State);
        Assert.Null(row.JellyfinItemId);
        Assert.Empty(complete.Lidarr.Writes);

        // trackCount 0 (unmonitored, no files) is not owned.
        using var empty = new AlbumRig();
        var a2 = empty.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        empty.Lidarr.AddAlbum(AlbumRig.OkComputer, a2, monitored: false, AlbumRig.Stats(0, 0, 12));
        Assert.Equal(RequestState.Searching, (await empty.CreateAsync(AlbumRig.OkComputer)).State);
    }

    [Fact]
    public async Task AR14_A_second_request_for_the_same_album_joins_the_first_with_no_second_write()
    {
        using var rig = new AlbumRig();
        var first = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Kim);

        var again = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Kim, HttpStatusCode.OK);
        var second = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Lee);

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(RequestState.Searching, second.State);
        Assert.Equal(first.LidarrAlbumId, second.LidarrAlbumId);
        Assert.Single(rig.Lidarr.Writes);
        Assert.Single(rig.Log.Named("request.joined"));

        // One GET per Lidarr album per pass serves both.
        rig.Lidarr.Calls.Clear();
        await rig.PollAsync();
        Assert.Equal(1, rig.Lidarr.Calls.Count(c => c.StartsWith("album/", StringComparison.Ordinal)));
    }

    // ---- progress (design 7.3) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AR20_Progress_follows_the_statistics_table()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);

        async Task<RequestRow> At(LidarrAlbumStatistics? stats)
        {
            album.Stats = stats;
            await rig.PollAsync();
            return rig.Row(row.Id);
        }

        Assert.Equal(RequestState.Searching, (await At(null)).State);
        Assert.Equal(RequestState.Searching, (await At(AlbumRig.Stats(0, 0, 12))).State);
        var none = await At(AlbumRig.Stats(0, 12, 12));
        Assert.Equal(RequestState.Searching, none.State);
        Assert.Null(none.TracksTotal);

        var partial = await At(AlbumRig.Stats(5, 12, 12));
        Assert.Equal(RequestState.Downloading, partial.State);
        Assert.Equal("5 of 12 tracks", partial.Detail);
        Assert.Equal((5, 12), (partial.TracksDone!.Value, partial.TracksTotal!.Value));
        Assert.Single(rig.Log.Named("lidarr.album_progress"));

        var full = await At(AlbumRig.Stats(12, 12, 12));
        Assert.Equal(RequestState.Importing, full.State);
        Assert.Equal(AlbumRequester.Details.Importing, full.Detail);

        // The monitored release has fewer expected tracks than it lists: full counts as importing; progress shows the total.
        var fewer = await At(AlbumRig.Stats(10, 10, 12));
        Assert.Equal(RequestState.Importing, fewer.State);
        Assert.Equal((10, 12), (fewer.TracksDone!.Value, fewer.TracksTotal!.Value));
        Assert.Single(rig.Lidarr.Writes);
    }

    [Fact]
    public async Task AR21_Importing_becomes_ready_when_Jellyfin_lists_the_album_and_the_requester_is_notified()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        rig.Lidarr.AlbumOf(AlbumRig.OkComputer).Stats = AlbumRig.Stats(12, 12, 12);

        Assert.Empty(await rig.PollAsync());
        Assert.Equal(RequestState.Importing, rig.Row(row.Id).State);

        rig.Library.Albums.Add(("Radiohead", "OK Computer", JellyfinAlbum));
        var notify = await rig.PollAsync();

        var ready = rig.Row(row.Id);
        Assert.Equal(RequestState.Ready, ready.State);
        Assert.Equal(JellyfinAlbum.ToString("N"), ready.JellyfinItemId);
        Assert.Equal(rig.Clock.GetUtcNow(), ready.ReadyUtc);
        Assert.Equal(row.Id, notify.Single().Id);
        Assert.Single(rig.Lidarr.Writes);
    }

    [Fact]
    public async Task AR22_The_first_track_file_sends_one_Media_Updated_with_the_mapped_artist_folder()
    {
        using var rig = new AlbumRig(map: new PathMapping("/media/music", "/data/music"));
        await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);

        await rig.PollAsync();
        Assert.Empty(rig.Library.Nudges);

        album.Stats = AlbumRig.Stats(1, 12, 12);
        await rig.PollAsync();
        album.Stats = AlbumRig.Stats(6, 12, 12);
        await rig.PollAsync();

        Assert.Equal(new[] { "/data/music/Radiohead" }, rig.Library.Nudges);
        Assert.DoesNotContain(rig.Lidarr.Writes, w => !w.StartsWith("POST album", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/media/music/Radiohead", "/media/music", "/data/music", "/data/music/Radiohead")]
    [InlineData("/media/music/Radiohead/", "/media/music/", "/data/music", "/data/music/Radiohead")]
    [InlineData("/media/musicals/Cats", "/media/music", "/data/music", "/media/musicals/Cats")]
    [InlineData("/media/music/Radiohead", "/media/music", "/media/music", "/media/music/Radiohead")]
    public void AR23_The_path_map_swaps_whole_folder_prefixes_only(string path, string from, string to, string expected) =>
        Assert.Equal(expected, AlbumMatch.MapPath(path, new PathMapping(from, to)));

    [Fact]
    public async Task AR24_Seven_days_with_no_file_fails_not_found_and_unmonitors_the_album()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);

        rig.Clock.Advance(TimeSpan.FromDays(6));
        await rig.PollAsync();
        Assert.Equal(RequestState.Searching, rig.Row(row.Id).State);

        rig.Clock.Advance(TimeSpan.FromDays(1));
        var notify = await rig.PollAsync();

        var failed = rig.Row(row.Id);
        Assert.Equal(RequestState.Failed, failed.State);
        Assert.Equal(AlbumReasons.NotFound, failed.FailReason);
        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer, $"PUT album/monitor [{album.Id}] false" }, rig.Lidarr.Writes);
        Assert.False(album.Monitored);
        Assert.True(rig.Lidarr.ArtistOf(album.ArtistId).Monitored);
        Assert.Single(rig.Log.Named("lidarr.album_unmonitored"));
        Assert.Equal(row.Id, notify.Single().Id);
    }

    [Fact]
    public async Task AR25_Three_days_stalled_part_way_fails_incomplete_and_leaves_the_album_monitored()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        album.Stats = AlbumRig.Stats(5, 12, 12);
        await rig.PollAsync();

        rig.Clock.Advance(TimeSpan.FromDays(2));
        album.Stats = AlbumRig.Stats(6, 12, 12);
        await rig.PollAsync();
        rig.Clock.Advance(TimeSpan.FromDays(2));
        await rig.PollAsync();
        Assert.Equal(RequestState.Downloading, rig.Row(row.Id).State);

        rig.Clock.Advance(TimeSpan.FromDays(1));
        await rig.PollAsync();

        var failed = rig.Row(row.Id);
        Assert.Equal(RequestState.Failed, failed.State);
        Assert.Equal(AlbumReasons.Incomplete, failed.FailReason);
        Assert.True(album.Monitored);
        Assert.Single(rig.Lidarr.Writes);
    }

    [Fact]
    public async Task AR26_Lidarr_unreachable_keeps_the_state_and_the_next_poll_goes_on()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        album.Stats = AlbumRig.Stats(3, 12, 12);

        rig.Lidarr.Down = new LidarrRequestException("GetAlbum", 503);
        await rig.PollAsync();
        Assert.Equal(RequestState.Searching, rig.Row(row.Id).State);
        Assert.NotEmpty(rig.Log.Named("lidarr.request_failed"));

        rig.Lidarr.Down = null;
        await rig.PollAsync();
        Assert.Equal(RequestState.Downloading, rig.Row(row.Id).State);
    }

    [Fact]
    public async Task AR27_In_Lidarr_but_not_in_Jellyfin_for_2_h_is_counted_for_health()
    {
        using var rig = new AlbumRig();
        await rig.CreateAsync(AlbumRig.OkComputer);
        rig.Lidarr.AlbumOf(AlbumRig.OkComputer).Stats = AlbumRig.Stats(12, 12, 12);
        await rig.PollAsync();
        Assert.Equal(0, rig.Requester.WaitingForJellyfin);

        rig.Clock.Advance(TimeSpan.FromHours(2));
        await rig.PollAsync();
        Assert.Equal(1, rig.Requester.WaitingForJellyfin);
    }

    // ---- restart, cancel, retry ------------------------------------------------------------------------------------

    [Fact]
    public async Task AR30_A_queued_request_is_sent_by_the_next_pass_and_waits_while_Lidarr_is_down()
    {
        using var rig = new AlbumRig();
        rig.Store.Insert(new NewRequest("q0000000000000000000000000000001", "album", SongRig.Kim.Id.ToString("N"), "kim", "Radiohead",
            rig.Clock.GetUtcNow(), Title: "OK Computer", ForeignAlbumId: AlbumRig.OkComputer, ForeignArtistId: AlbumRig.Radiohead));

        rig.Lidarr.Down = new LidarrRequestException("LookupAlbums", "timeout");
        await rig.Requester.TickAsync(CancellationToken.None);
        var waiting = rig.Row("q0000000000000000000000000000001");
        Assert.Equal(RequestState.Queued, waiting.State);
        Assert.Equal(AlbumRequester.Details.LidarrDown, waiting.Detail);

        // Not before the 2-min poll.
        rig.Lidarr.Down = null;
        await rig.Requester.TickAsync(CancellationToken.None);
        Assert.Empty(rig.Lidarr.Writes);

        await rig.PollAsync();
        Assert.Equal(RequestState.Searching, rig.Row("q0000000000000000000000000000001").State);
        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer }, rig.Lidarr.Writes);
    }

    [Fact]
    public async Task AR31_Cancel_unmonitors_the_album_unless_another_active_request_holds_it()
    {
        using var rig = new AlbumRig();
        var kim = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Kim);
        var lee = await rig.CreateAsync(AlbumRig.OkComputer, SongRig.Lee);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);

        var first = await rig.Requester.CancelAsync(SongRig.Kim, kim.Id, CancellationToken.None);
        Assert.Equal(RequestState.Cancelled, first.Row!.State);
        Assert.True(album.Monitored);

        Assert.Equal(404, (await rig.Requester.CancelAsync(SongRig.Kim, lee.Id, CancellationToken.None)).Status);
        var second = await rig.Requester.CancelAsync(SongRig.Lee, lee.Id, CancellationToken.None);
        Assert.Equal(RequestState.Cancelled, second.Row!.State);
        Assert.False(album.Monitored);
        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer, $"PUT album/monitor [{album.Id}] false" }, rig.Lidarr.Writes);
        Assert.Equal("cancelled", rig.Log.Named("lidarr.album_unmonitored").Single().Fields["Reason"]);
    }

    [Fact]
    public async Task AR32_The_owner_cannot_cancel_once_downloading_but_an_admin_can()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        rig.Lidarr.AlbumOf(AlbumRig.OkComputer).Stats = AlbumRig.Stats(2, 12, 12);
        await rig.PollAsync();

        Assert.Equal(409, (await rig.Requester.CancelAsync(SongRig.Kim, row.Id, CancellationToken.None)).Status);
        Assert.Equal(200, (await rig.Requester.CancelAsync(SongRig.Nate, row.Id, CancellationToken.None)).Status);
        Assert.Equal(RequestState.Cancelled, rig.Row(row.Id).State);
    }

    [Fact]
    public async Task AR33_Retry_sends_a_failed_album_again()
    {
        using var rig = new AlbumRig();
        var row = await rig.CreateAsync(AlbumRig.OkComputer);
        var album = rig.Lidarr.AlbumOf(AlbumRig.OkComputer);
        rig.Clock.Advance(TimeSpan.FromDays(7));
        await rig.PollAsync();
        Assert.Equal(RequestState.Failed, rig.Row(row.Id).State);

        var (outcome, _) = await rig.Requester.RetryAsync(SongRig.Kim, row.Id, CancellationToken.None);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(RequestState.Searching, outcome.Row!.State);
        Assert.Equal(0, outcome.Row.Attempts);
        Assert.Null(outcome.Row.FailReason);
        Assert.True(album.Monitored);
        Assert.Equal($"PUT album/monitor [{album.Id}] true", rig.Lidarr.Writes.Last());
        Assert.Equal(409, (await rig.Requester.RetryAsync(SongRig.Kim, row.Id, CancellationToken.None)).Outcome.Status);
    }

    // ---- search rows (design 5 item 3) -------------------------------------------------------------------------------

    private static (SearchService Search, UsersClient Jellyfin, ManualTimeProvider Clock) SearchOver(AlbumRig rig)
    {
        var jellyfin = new UsersClient();
        var albums = new AlbumSearch(rig.Lidarr, new ListLogger<AlbumSearch>(), rig.Clock);
        var search = new SearchService(jellyfin, new FakeDeezer(), null, rig.Store, new ListLogger<SearchService>(), null, albums);
        return (search, jellyfin, rig.Clock);
    }

    [Fact]
    public async Task AR40_Album_rows_come_from_the_Lidarr_lookup_with_owned_released_and_request_marks()
    {
        using var rig = new AlbumRig();
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);
        var ok = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: true, AlbumRig.Stats(12, 12, 12));
        var leeRequest = await rig.CreateAsync(AlbumRig.Amnesiac, SongRig.Lee);
        var (search, jellyfin, _) = SearchOver(rig);
        jellyfin.SearchResult =
        [
            new BaseItemDto { Id = JellyfinAlbum, Name = "Kid A", Type = "MusicAlbum", AlbumArtist = "Radiohead", ProductionYear = 2000 },
        ];
        rig.Lidarr.Calls.Clear();

        var result = await search.SearchAsync(SongRig.Kim.Id, "radiohead", SearchKinds.Album, CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.Empty(result.Songs);
        var rows = result.Albums.ToDictionary(r => r.ForeignAlbumId);
        Assert.Equal(4, rows.Count);

        var okRow = rows[AlbumRig.OkComputer];
        Assert.True(okRow.Owned);
        Assert.False(okRow.Requestable);
        Assert.Null(okRow.ItemId);

        var kid = rows[AlbumRig.KidA];
        Assert.True(kid.Owned);
        Assert.Equal(JellyfinAlbum.ToString("N"), kid.ItemId);
        Assert.Equal(2000, kid.Year);
        Assert.Equal(10, kid.TrackCount);
        Assert.Equal("https://img.test/" + AlbumRig.KidA + ".jpg", kid.CoverUrl);
        Assert.Equal("Radiohead", kid.Artist);
        Assert.Equal(AlbumRig.Radiohead, kid.ForeignArtistId);

        var amnesiac = rows[AlbumRig.Amnesiac];
        Assert.False(amnesiac.Owned);
        Assert.True(amnesiac.Requestable);
        Assert.Equal(leeRequest.Id, amnesiac.Request!.Id);
        Assert.False(amnesiac.Request.Mine);

        var next = rows[AlbumRig.Unreleased];
        Assert.False(next.Released);
        Assert.False(next.Requestable);

        // Lookup rows have no statistics: the rows Lidarr has get theirs from one albumIds call.
        var amnesiacId = rig.Lidarr.AlbumOf(AlbumRig.Amnesiac).Id;
        Assert.Single(rig.Lidarr.Calls, c => c.StartsWith("album?albumIds=", StringComparison.Ordinal));
        Assert.Contains(rig.Lidarr.Calls, c => c == $"album?albumIds={ok.Id},{amnesiacId}" || c == $"album?albumIds={amnesiacId},{ok.Id}");
        Assert.Empty(result.Library.Songs);
        Assert.Single(result.Library.Albums);
    }

    [Fact]
    public async Task AR41_The_text_lookup_is_cached_for_an_hour()
    {
        using var rig = new AlbumRig();
        var (search, _, clock) = SearchOver(rig);

        await search.SearchAsync(SongRig.Kim.Id, "Radiohead", SearchKinds.Album, CancellationToken.None);
        await search.SearchAsync(SongRig.Kim.Id, " radiohead ", SearchKinds.All, CancellationToken.None);
        Assert.Equal(1, rig.Lidarr.Lookups);

        clock.Advance(TimeSpan.FromHours(1));
        await search.SearchAsync(SongRig.Kim.Id, "radiohead", SearchKinds.Album, CancellationToken.None);
        Assert.Equal(2, rig.Lidarr.Lookups);

        // Songs only: no Lidarr call.
        await search.SearchAsync(SongRig.Kim.Id, "something else", SearchKinds.Song, CancellationToken.None);
        Assert.Equal(2, rig.Lidarr.Lookups);
    }

    [Fact]
    public async Task AR42_A_failing_Lidarr_gives_a_warning_and_no_album_rows()
    {
        using var rig = new AlbumRig();
        var (search, _, _) = SearchOver(rig);
        rig.Lidarr.Down = new LidarrRequestException("LookupAlbums", 503);

        var result = await search.SearchAsync(SongRig.Kim.Id, "radiohead", SearchKinds.Album, CancellationToken.None);

        Assert.Empty(result.Albums);
        Assert.Equal(new[] { "lidarr: failed" }, result.Warnings);
    }

    // ---- route ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AR50_POST_requests_for_an_album_starts_it_and_DELETE_cancels_it()
    {
        using var album = new AlbumRig();
        using var rig = new ApiRig(extraServices: services =>
        {
            // The requester works on the host's request store (the route reads the row from it).
            services.RemoveAll<AlbumRequester>();
            services.AddSingleton(sp => new AlbumRequester(
                sp.GetRequiredService<RequestStore>(), album.Lidarr, album.Library, album.Settings, new ListLogger<AlbumRequester>(), TimeProvider.System));
        });

        using var bad = await rig.PostAsync("/api/v1/requests", new { kind = "album", foreignAlbumId = "nope" }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var created = await rig.PostAsync("/api/v1/requests", new { kind = "album", foreignAlbumId = AlbumRig.OkComputer }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await ApiRig.JsonOf(created);
        Assert.Equal("album", json.GetProperty("kind").GetString());
        Assert.Equal("searching", json.GetProperty("state").GetString());
        Assert.Equal("OK Computer", json.GetProperty("title").GetString());
        Assert.Equal(1997, json.GetProperty("year").GetInt32());
        var id = json.GetProperty("id").GetString();

        using var cancel = await rig.DeleteAsync($"/api/v1/requests/{id}", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("cancelled", (await ApiRig.JsonOf(cancel)).GetProperty("state").GetString());
        Assert.False(album.Lidarr.AlbumOf(AlbumRig.OkComputer).Monitored);
    }

    [Fact]
    public async Task AR52_With_a_Lidarr_URL_the_host_wires_the_album_requester_search_and_worker()
    {
        using var rig = new ApiRig(new()
        {
            ["ENCORE_LIDARR_URL"] = "http://lidarr.invalid:8686",
            ["ENCORE_LIDARR_API_KEY"] = "abcdefghij0123456789abcdefghij01",
        });

        using var config = await rig.GetAsync("/api/v1/config");
        Assert.True((await ApiRig.JsonOf(config)).GetProperty("albumRequests").GetBoolean());
        Assert.NotNull(rig.Services.GetService<AlbumRequester>());
        Assert.NotNull(rig.Services.GetService<AlbumSearch>());
        Assert.NotNull(rig.Services.GetService<AlbumWorker>());
        Assert.NotNull(rig.Services.GetService<ILidarrClient>());
    }

    [Fact]
    public async Task AR51_Without_Lidarr_an_album_request_is_503()
    {
        using var rig = new ApiRig();

        using var post = await rig.PostAsync("/api/v1/requests", new { kind = "album", foreignAlbumId = AlbumRig.OkComputer }, bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        Assert.Equal("albums_unavailable", (await ApiRig.JsonOf(post)).GetProperty("error").GetString());
        Assert.Null(rig.Services.GetService<AlbumRequester>());
    }
}
