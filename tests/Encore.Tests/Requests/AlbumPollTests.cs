using System.Net;
using System.Text;
using Encore.Config;
using Encore.Lidarr;
using Encore.Requests;
using Encore.Requests.Albums;
using Encore.Store;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Requests;

/// <summary>
/// Lidarr 3.1.6 over HTTP for the album poll (T108): fixed replies per path and query, built from the resource classes in
/// Lidarr's source (AlbumResource with the full ArtistResource for album/{id}, TrackFileResource rows). Nulls are left out,
/// as Lidarr's serializer does (WhenWritingNull). Every request is recorded.
/// </summary>
internal sealed class RoutedLidarr : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Sent { get; } = [];

    public void Set(string pathAndQuery, HttpStatusCode status, string? json = null) =>
        _routes[pathAndQuery] = () =>
        {
            var response = new HttpResponseMessage(status);
            if (json is not null)
            {
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return response;
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.PathAndQuery;
        Sent.Add(request.Method.Method + " " + key);
        return Task.FromResult(_routes.TryGetValue(key, out var reply)
            ? reply()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Pendulum - Immersion as Lidarr returned it on the live server (album 3431, artist 270, 15 tracks).</summary>
internal static class PendulumJson
{
    public const int AlbumId = 3431;
    public const int ArtistId = 270;
    public const string AlbumFid = "3f6f7d2c-5b1d-3c1e-9d43-6a0c0e1a2b10";
    public const string ArtistFid = "2030e8bd-8d4a-4b2f-9a3c-1c7d2f0e7b5a";

    public const string AlbumPath = "/api/v1/album/3431";
    public const string TrackFilePath = "/api/v1/trackfile?albumId=3431";
    public const string ArtistPath = "/api/v1/artist/270";

    /// <summary>GET album/{id}: full artist object, releases with media; <paramref name="statistics"/> is the raw object or null (left out).</summary>
    public static string Album(string? statistics) => $$"""
        {"title":"Immersion","disambiguation":"","overview":"","artistId":270,"foreignAlbumId":"{{AlbumFid}}","monitored":true,"anyReleaseOk":true,"profileId":1,"duration":4380000,"albumType":"Album","secondaryTypes":[],"ratings":{"votes":12,"value":8.4},"releaseDate":"2010-05-21T00:00:00Z","releases":[{"id":51001,"albumId":3431,"foreignReleaseId":"8b1e7c4a-0d55-4f0e-8c51-2f6d3e9a7c01","title":"Immersion","status":"Official","duration":4380000,"trackCount":15,"media":[{"mediumNumber":1,"mediumName":"","mediumFormat":"CD"}],"mediumCount":1,"disambiguation":"","country":["United Kingdom"],"label":["Warner Music UK"],"format":"CD","monitored":true},{"id":51002,"albumId":3431,"foreignReleaseId":"8b1e7c4a-0d55-4f0e-8c51-2f6d3e9a7c02","title":"Immersion","status":"Official","duration":4510000,"trackCount":16,"media":[{"mediumNumber":1,"mediumName":"","mediumFormat":"Digital Media"}],"mediumCount":1,"disambiguation":"","country":["Japan"],"label":[],"format":"Digital Media","monitored":false}],"genres":["Drum and bass"],"mediumCount":1,"media":[{"mediumNumber":1,"mediumName":"","mediumFormat":"CD"}],"artist":{"artistMetadataId":301,"status":"continuing","ended":false,"artistName":"Pendulum","foreignArtistId":"{{ArtistFid}}","tadbId":0,"discogsId":0,"overview":"","artistType":"Group","disambiguation":"","links":[],"images":[],"path":"/media/music/Pendulum","qualityProfileId":1,"metadataProfileId":1,"monitored":true,"monitorNewItems":"none","rootFolderPath":"/media/music","folder":"Pendulum","genres":[],"cleanName":"pendulum","sortName":"pendulum","tags":[],"added":"2026-11-16T05:13:02Z","ratings":{"votes":0,"value":0},"id":270},"images":[{"url":"/MediaCover/Albums/3431/cover.jpg?lastWrite=639000000000000000","coverType":"cover","extension":".jpg","remoteUrl":"https://example.invalid/immersion.jpg"}],"links":[],"lastSearchTime":"2026-11-16T05:13:03Z"{{(statistics is null ? "" : ",\"statistics\":" + statistics)}},"id":3431}
        """;

    /// <summary>The live statistics after Soularr's import: 15 of 15.</summary>
    public const string Complete = """{"trackFileCount":15,"trackCount":15,"totalTrackCount":15,"sizeOnDisk":512340000,"percentOfTracks":100}""";

    /// <summary>Before any file (and what Lidarr's per-artist statistics cache can keep showing after an import).</summary>
    public const string Empty = """{"trackFileCount":0,"trackCount":15,"totalTrackCount":15,"sizeOnDisk":0,"percentOfTracks":0}""";

    public const string Artist = """
        {"artistMetadataId":301,"status":"continuing","ended":false,"artistName":"Pendulum","foreignArtistId":"2030e8bd-8d4a-4b2f-9a3c-1c7d2f0e7b5a","path":"/media/music/Pendulum","qualityProfileId":1,"metadataProfileId":1,"monitored":true,"monitorNewItems":"none","rootFolderPath":"/media/music","folder":"Pendulum","tags":[],"added":"2026-11-16T05:13:02Z","id":270}
        """;

    /// <summary>GET trackfile?albumId= : <paramref name="count"/> TrackFileResource rows (the files Soularr's DownloadedAlbumsScan imported).</summary>
    public static string TrackFiles(int count) => "[" + string.Join(",", Enumerable.Range(1, count).Select(i => $$$"""
        {"artistId":270,"albumId":3431,"path":"/media/music/Pendulum/Immersion (2010)/{{{i:00}}} - Track {{{i}}}.flac","size":34156000,"dateAdded":"2026-11-16T05:18:{{{i:00}}}Z","quality":{"quality":{"id":6,"name":"FLAC"},"revision":{"version":1,"real":0,"isRepack":false}},"qualityWeight":601,"customFormats":[],"customFormatScore":0,"indexerFlags":0,"mediaInfo":{"audioChannels":2,"audioBitRate":"1000 kbps","audioCodec":"FLAC","audioBits":"16bit","audioSampleRate":"44.1kHz"},"qualityCutoffNotMet":false,"audioTags":{"title":"Track {{{i}}}","cleanTitle":"track {{{i}}}","artistTitle":"Pendulum","albumTitle":"Immersion","discNumber":1,"discCount":1,"year":2010,"duration":"00:04:52"},"id":{{{90000 + i}}} }
        """)) + "]";
}

/// <summary>An album requester over the real <see cref="LidarrClient"/> (HTTP), real stores and a manual clock.</summary>
internal sealed class PollRig : IDisposable
{
    public static readonly Guid Jellyfin = Guid.Parse("77777777777777777777777777777777");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-albumpoll-" + Guid.NewGuid().ToString("N"));
    private readonly EncoreStore _main;

    public PollRig()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "encore.db");
        _main = EncoreStore.Open(path, Clock);
        Store = RequestStore.Open(path);
        var options = new LidarrClientOptions { Timeout = TimeSpan.FromSeconds(5), GetRetryDelay = TimeSpan.FromMilliseconds(1) };
        var http = new HttpClient(new LidarrAuthHandler("LidarrKey0123456789abcdefAB") { InnerHandler = Lidarr })
        {
            BaseAddress = new Uri("http://lidarr:8686/"),
            Timeout = options.Timeout,
        };
        Client = new LidarrClient(http, options);
        Lidarr.Set(PendulumJson.ArtistPath, HttpStatusCode.OK, PendulumJson.Artist);
        Requester = NewRequester();
    }

    /// <summary>05:13Z on the live day: the request was sent to Lidarr and is searching.</summary>
    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 16, 5, 13, 0, TimeSpan.Zero));

    public RoutedLidarr Lidarr { get; } = new();

    public LidarrClient Client { get; }

    public FakeAlbumLibrary Library { get; } = new();

    public RequestStore Store { get; }

    public ListLogger<AlbumRequester> Log { get; } = new();

    public AlbumRequester Requester { get; private set; }

    /// <summary>A new requester over the same store and Lidarr: what a container restart gives (nothing in memory).</summary>
    public AlbumRequester Restart() => Requester = NewRequester();

    /// <summary>The live row: album request, searching, Lidarr album 3431 and artist 270.</summary>
    public string InsertSearching(string id = "p0000000000000000000000000000001")
    {
        var now = Clock.GetUtcNow();
        Store.Insert(new NewRequest(id, "album", SongRig.Kim.Id.ToString("N"), "kim", "Pendulum", now,
            Title: "Immersion", Year: 2010, ForeignAlbumId: PendulumJson.AlbumFid, ForeignArtistId: PendulumJson.ArtistFid));
        Store.Update(id, RequestState.Searching, now, new RequestUpdate(
            Detail: AlbumRequester.Details.Searching, LidarrAlbumId: PendulumJson.AlbumId, LidarrArtistId: PendulumJson.ArtistId,
            AlbumWasMonitored: false, ArtistWasMonitored: false));
        return id;
    }

    public Task<IReadOnlyList<RequestRow>> TickAsync() => Requester.TickAsync(CancellationToken.None);

    public async Task<IReadOnlyList<RequestRow>> PollAsync()
    {
        Clock.Advance(RequestLimits.AlbumPoll + TimeSpan.FromSeconds(1));
        return await TickAsync();
    }

    public RequestRow Row(string id) => Store.Get(id)!;

    public void JellyfinLists() => Library.Albums.Add(("Pendulum", "Immersion", Jellyfin));

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

    private AlbumRequester NewRequester() => new(
        Store, Client, Library, new AlbumRequestSettings(2, null, new PathMapping("/media/music", "/media/music")), Log, Clock);
}

/// <summary>
/// T108: album requests stayed in "searching" after Lidarr had the album (Pendulum - Immersion, imported by Soularr through a
/// DownloadedAlbumsScan: no queue entry, no grab history). Lidarr-shaped JSON through the real <see cref="LidarrClient"/>.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AlbumPollTests
{
    [Fact]
    public async Task AP01_Soularr_import_with_statistics_15_of_15_and_no_queue_moves_searching_to_importing_then_ready()
    {
        using var rig = new PollRig();
        var id = rig.InsertSearching();
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.OK, PendulumJson.Album(PendulumJson.Complete));

        await rig.PollAsync();

        var importing = rig.Row(id);
        Assert.Equal(RequestState.Importing, importing.State);
        Assert.Equal(AlbumRequester.Details.Importing, importing.Detail);
        Assert.Equal((15, 15), (importing.TracksDone!.Value, importing.TracksTotal!.Value));
        var seen = Assert.Single(rig.Log.Named("lidarr.album_files_seen"));
        Assert.Equal(LogLevel.Information, seen.Level);
        Assert.Equal(15, seen.Fields["Files"]);
        Assert.Equal(15, seen.Fields["StatisticsFiles"]);
        Assert.Equal((long)PendulumJson.AlbumId, seen.Fields["LidarrAlbumId"]);

        // Complete statistics need no track-file call; the artist folder gets its one nudge.
        Assert.DoesNotContain(rig.Lidarr.Sent, s => s.Contains("trackfile", StringComparison.Ordinal));
        Assert.Equal(new[] { "/media/music/Pendulum" }, rig.Library.Nudges);
        Assert.Contains(rig.Log.Named("request.state"), e => e.Message.Contains("searching -> importing", StringComparison.Ordinal));

        rig.JellyfinLists();
        var notify = await rig.PollAsync();

        var ready = rig.Row(id);
        Assert.Equal(RequestState.Ready, ready.State);
        Assert.Equal(PollRig.Jellyfin.ToString("N"), ready.JellyfinItemId);
        Assert.Equal(id, notify.Single().Id);
        Assert.Single(rig.Log.Named("lidarr.album_files_seen"));
        Assert.All(rig.Lidarr.Sent, s => Assert.StartsWith("GET ", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AP02_Stale_statistics_with_15_track_files_in_Lidarrs_database_still_reach_ready()
    {
        using var rig = new PollRig();
        var id = rig.InsertSearching();

        // Lidarr's per-artist statistics cache still says 0 files; the track-file list (read from its database) has all 15.
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.OK, PendulumJson.Album(PendulumJson.Empty));
        rig.Lidarr.Set(PendulumJson.TrackFilePath, HttpStatusCode.OK, PendulumJson.TrackFiles(15));
        rig.JellyfinLists();

        var notify = await rig.PollAsync();

        var ready = rig.Row(id);
        Assert.Equal(RequestState.Ready, ready.State);
        Assert.Equal(id, notify.Single().Id);
        var seen = Assert.Single(rig.Log.Named("lidarr.album_files_seen"));
        Assert.Equal(0, seen.Fields["StatisticsFiles"]);
        Assert.Equal(15, seen.Fields["TrackFiles"]);
        Assert.Contains("GET " + PendulumJson.TrackFilePath, rig.Lidarr.Sent);
    }

    [Fact]
    public async Task AP03_Without_statistics_the_track_files_and_the_monitored_release_give_the_progress()
    {
        using var rig = new PollRig();
        var id = rig.InsertSearching();
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.OK, PendulumJson.Album(statistics: null));
        rig.Lidarr.Set(PendulumJson.TrackFilePath, HttpStatusCode.OK, PendulumJson.TrackFiles(6));

        await rig.PollAsync();

        var downloading = rig.Row(id);
        Assert.Equal(RequestState.Downloading, downloading.State);
        Assert.Equal("6 of 15 tracks", downloading.Detail);

        rig.Lidarr.Set(PendulumJson.TrackFilePath, HttpStatusCode.OK, PendulumJson.TrackFiles(15));
        await rig.PollAsync();
        Assert.Equal(RequestState.Importing, rig.Row(id).State);
        Assert.Single(rig.Log.Named("lidarr.album_files_seen"));
    }

    [Fact]
    public async Task AP04_After_a_restart_a_stuck_searching_row_is_polled_at_once_and_recovers_with_no_manual_step()
    {
        using var rig = new PollRig();
        var id = rig.InsertSearching();

        // Before the import: polled, nothing yet.
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.OK, PendulumJson.Album(PendulumJson.Empty));
        rig.Lidarr.Set(PendulumJson.TrackFilePath, HttpStatusCode.OK, "[]");
        await rig.PollAsync();
        Assert.Equal(RequestState.Searching, rig.Row(id).State);

        // 05:18 Soularr imports; Jellyfin lists it at 05:20; Encore restarts hours later (nothing in memory).
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.OK, PendulumJson.Album(PendulumJson.Complete));
        rig.JellyfinLists();
        rig.Clock.Advance(TimeSpan.FromHours(10));
        rig.Restart();
        var sentBefore = rig.Lidarr.Sent.Count;

        // The first pass after the start polls every active row without waiting for the 2-min poll.
        var notify = await rig.TickAsync();

        var ready = rig.Row(id);
        Assert.Equal(RequestState.Ready, ready.State);
        Assert.Equal(id, notify.Single().Id);
        Assert.Contains("GET " + PendulumJson.AlbumPath, rig.Lidarr.Sent.Skip(sentBefore));
        var states = rig.Log.Named("request.state").Select(e => e.Message).ToList();
        Assert.Contains(states, m => m.Contains("searching -> importing", StringComparison.Ordinal));
        Assert.Contains(states, m => m.Contains("importing -> ready", StringComparison.Ordinal));

        // Ready rows are no longer polled.
        var sentAfter = rig.Lidarr.Sent.Count;
        await rig.PollAsync();
        Assert.Equal(sentAfter, rig.Lidarr.Sent.Count);
    }

    [Fact]
    public async Task AP05_Every_poll_failure_is_a_warning_with_ids_and_counts_and_the_state_is_kept()
    {
        using var rig = new PollRig();
        var id = rig.InsertSearching();
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.ServiceUnavailable);

        await rig.PollAsync();
        await rig.PollAsync();

        var failures = rig.Log.Named("lidarr.album_poll_failed").ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, f => Assert.Equal(LogLevel.Warning, f.Level));
        Assert.Equal(new object?[] { 1, 2 }, failures.Select(f => f.Fields["FailuresInARow"]).ToArray());
        Assert.Equal("GetAlbum", failures[0].Fields["Operation"]);
        Assert.Equal(503, failures[0].Fields["Status"]);
        Assert.Equal(id, failures[0].Fields["RequestId"]);
        Assert.All(failures, f => Assert.DoesNotContain("http", f.Message, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RequestState.Searching, rig.Row(id).State);

        // The album answers but the track-file list fails: still a warning, the statistics alone decide.
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.OK, PendulumJson.Album(PendulumJson.Empty));
        rig.Lidarr.Set(PendulumJson.TrackFilePath, HttpStatusCode.InternalServerError);
        await rig.PollAsync();
        var third = rig.Log.Named("lidarr.album_poll_failed").Last();
        Assert.Equal("CountTrackFiles", third.Fields["Operation"]);
        Assert.Equal(3, third.Fields["FailuresInARow"]);
        Assert.Equal(RequestState.Searching, rig.Row(id).State);

        // A clean poll resets the count.
        rig.Lidarr.Set(PendulumJson.TrackFilePath, HttpStatusCode.OK, "[]");
        await rig.PollAsync();
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.BadGateway);
        await rig.PollAsync();
        Assert.Equal(1, rig.Log.Named("lidarr.album_poll_failed").Last().Fields["FailuresInARow"]);
    }

    [Fact]
    public async Task AP06_The_client_counts_track_file_rows_and_a_404_is_none()
    {
        using var rig = new PollRig();
        rig.Lidarr.Set(PendulumJson.TrackFilePath, HttpStatusCode.OK, PendulumJson.TrackFiles(15));

        Assert.Equal(15, await rig.Client.CountTrackFilesAsync(PendulumJson.AlbumId, CancellationToken.None));
        Assert.Equal(0, await rig.Client.CountTrackFilesAsync(9999, CancellationToken.None));
        Assert.Equal(new[] { "GET " + PendulumJson.TrackFilePath, "GET /api/v1/trackfile?albumId=9999" }, rig.Lidarr.Sent);

        var album = await rig.Client.GetAlbumAsync(PendulumJson.AlbumId, CancellationToken.None);
        Assert.Null(album);
        rig.Lidarr.Set(PendulumJson.AlbumPath, HttpStatusCode.OK, PendulumJson.Album(PendulumJson.Complete));
        album = await rig.Client.GetAlbumAsync(PendulumJson.AlbumId, CancellationToken.None);
        Assert.Equal(new LidarrAlbumStatistics(15, 15, 15, 512340000, 100), album!.Statistics);
        Assert.True(album.Monitored);
        Assert.True(album.Artist!.Monitored);
        Assert.Equal(PendulumJson.ArtistId, album.ArtistId);
    }
}
