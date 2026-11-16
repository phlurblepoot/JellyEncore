using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Jellyfin;
using Encore.Requests;
using Encore.Requests.Albums;
using Encore.Requests.Auth;
using Encore.Requests.Notify;
using Encore.Requests.Search;
using Encore.Requests.Songs;
using Encore.Store;
using Encore.Tests.Discovery;
using Encore.Tests.Fetch;
using Encore.Tests.Requests;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator = Encore.Orchestration.Orchestrator;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// The v0.3 request side around a <see cref="FetchWorld"/> (v03 design section 15, T94): the real request store,
/// song service, worker, notifier and search over the real fetch side and the harness's fake Jellyfin. The request side
/// runs on the fetch side's fast clock. Tests drive the request worker by hand with <see cref="UntilAsync"/>.
/// </summary>
internal sealed class RequestWorld : IAsyncDisposable
{
    public static readonly Guid Kim = SongRig.Kim.Id;

    private readonly int _perDay;

    public RequestWorld(OrchestratorHarness h, FetchWorld w, int perDay = 25)
    {
        H = h;
        W = w;
        _perDay = perDay;
        Build();
        H.Jellyfin.Sessions = [new SessionInfoDto { Id = "kim-phone", UserId = Kim, SupportsRemoteControl = true, Client = "Finamp", DeviceId = "dev-kim" }];
    }

    public OrchestratorHarness H { get; }

    public FetchWorld W { get; private set; }

    public RequestStore Store { get; private set; } = null!;

    public RequestService Service { get; private set; } = null!;

    public RequestWorker Worker { get; private set; } = null!;

    public SearchService Search { get; private set; } = null!;

    public ListLogger<RequestService> Log { get; } = new();

    public IReadOnlyList<(string SessionId, string Header, string Text, int TimeoutMs)> Messages => H.Jellyfin.Messages.ToList();

    private void Build()
    {
        Store = RequestStore.Open(H.DbPath);
        Service = new RequestService(Store, W.Fetch, W.Owned, W.Deezer, W.Store, new SongRequestSettings(W.Mode, _perDay), Log, W.Clock);
        var notifier = new RequestNotifier(H.Jellyfin, Store, new ListLogger<RequestNotifier>(), W.Clock, delay: (_, _) => Task.CompletedTask);
        Worker = new RequestWorker(Service, notifier, new ListLogger<RequestWorker>(), W.Clock, Timeout.InfiniteTimeSpan);
        Search = new SearchService(H.Jellyfin, W.Deezer, W.Owned, Store, new ListLogger<SearchService>());
    }

    /// <summary>A Deezer track the search lists and the request reads (details), with the same ids as <paramref name="track"/> when given.</summary>
    public DeezerSearchTrack Offer(long id, string artist, string title, int seconds = 200)
    {
        W.Deezer.TrackDetails[id] = new DeezerTrackDetail(
            id, title, title, null, seconds, 1, 1, "2001-03-07",
            new DeezerArtist(id * 10, artist, 100, true), new DeezerAlbumRef(id * 100, "Album " + id, "2001-03-12", "https://cdn-images.dzcdn.net/c.jpg"), []);
        var row = new DeezerSearchTrack(id, title, null, artist, "Album " + id, seconds, "https://cdn-images.dzcdn.net/c.jpg", false);
        W.Deezer.SearchRows.Add(row);
        return row;
    }

    public async Task<RequestRow> CreateAsync(long deezerId, RequestUser? user = null)
    {
        var (outcome, notify) = await Service.CreateSongAsync(user ?? SongRig.Kim, deezerId, CancellationToken.None);
        Assert.Equal(201, outcome.Status);
        _ = notify;
        return outcome.Row!;
    }

    public RequestRow Row(string id) => Store.Get(id)!;

    /// <summary>Runs request passes (with their notifications) until the request has <paramref name="state"/>.</summary>
    public async Task UntilAsync(string id, string state, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Row(id).State != state)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for request {id} to be {state}; it is {Row(id).State} ({Row(id).Detail})");
            }

            await Worker.TickAsync(CancellationToken.None);
            await Task.Delay(10);
        }
    }

    /// <summary>The process restarts: the fetch side and the request side stop and start again on the same database and folders.</summary>
    public async Task RestartAsync()
    {
        Store.Dispose();
        W = await W.RestartAsync();
        H.RestartStore();
        Build();
    }

    public async ValueTask DisposeAsync()
    {
        Store.Dispose();
        await W.DisposeAsync();
        H.Dispose();
    }
}

/// <summary>v03 section 15, T94: song and album requests end to end over the fakes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RequestTests
{
    private static readonly Guid JellyfinAlbum = Guid.Parse("66666666666666666666666666666666");

    private static async Task<RequestWorld> CreateAsync(double scale = 100, Action<FetchWorld>? setup = null, int perDay = 25)
    {
        var h = new OrchestratorHarness();
        var w = new FetchWorld(h, FetchMode.Live, scale);
        setup?.Invoke(w);
        await w.StartAsync();
        return new RequestWorld(h, w, perDay);
    }

    /// <summary>A peer whose download makes steady progress until <paramref name="release"/> completes, then succeeds.</summary>
    private static TransferScript Held(Task release) => new(e => release.IsCompleted
        ? new TransferStep("Completed, Succeeded", 8_000_000, 1_000_000)
        : new TransferStep("InProgress", Math.Min(7_999_999, 1 + (long)(e.TotalSeconds * 1000)), 1_000_000));

    // ---- songs ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RQ01_A_song_found_by_search_is_requested_fetched_ingested_ready_and_the_requester_is_messaged()
    {
        await using var r = await CreateAsync(setup: w => w.OfferGood("Daft Punk", "Aerodynamic"));
        var found = r.Offer(501, "Daft Punk", "Aerodynamic");

        var search = await r.Search.SearchAsync(SongRig.Kim.Id, "aerodynamic", SearchKinds.Song, CancellationToken.None);
        var hit = Assert.Single(search.Songs);
        Assert.Equal(found.Id, hit.DeezerTrackId);
        Assert.True(hit.Requestable);
        Assert.False(hit.Owned);
        Assert.Null(hit.Request);

        var row = await r.CreateAsync(hit.DeezerTrackId);
        Assert.Equal(RequestState.Queued, row.State);
        var again = await r.Search.SearchAsync(SongRig.Kim.Id, "aerodynamic", SearchKinds.Song, CancellationToken.None);
        Assert.Equal(row.Id, Assert.Single(again.Songs).Request!.Id);

        await r.UntilAsync(row.Id, RequestState.Ready);

        var ready = r.Row(row.Id);
        var fetched = r.W.FetchedId("Daft Punk", "Aerodynamic");
        Assert.NotNull(fetched);
        Assert.Equal(fetched!.Value.ToString("N"), ready.JellyfinItemId);
        Assert.Contains(FetchWorld.JellyfinPathOf("Daft Punk", "Aerodynamic"), r.H.Jellyfin.MediaUpdatedPaths);
        Assert.Single(r.W.Slskd.Enqueued);
        Assert.Equal(["ready"], r.W.Query("SELECT state FROM fetched_files"));
        Assert.Equal(["1"], r.W.Query("SELECT requested FROM fetched_files"));
        var message = Assert.Single(r.Messages);
        Assert.Equal(("kim-phone", "Encore", "Ready: Daft Punk - Aerodynamic"), (message.SessionId, message.Header, message.Text));

        // More passes change nothing: no second message, no second download.
        await r.Worker.TickAsync(CancellationToken.None);
        Assert.Single(r.Messages);
        Assert.Single(r.W.Slskd.Enqueued);
    }

    [Fact]
    public async Task RQ02_A_request_for_a_song_the_radio_is_fetching_joins_it_with_one_download_and_both_end_ready()
    {
        var h = new OrchestratorHarness();
        var w = new FetchWorld(h, FetchMode.Live, 5);
        DeezerTrack radioTrack = null!;
        radioTrack = w.Track("Radio B", "New Song");
        w.Radio(radioTrack);
        w.OfferGood("Radio B", "New Song");
        await w.StartAsync();
        var o = h.CreateOrchestrator();
        await o.RefreshAllowListAsync();
        var l = new Listener(h, o);
        await using var r = new RequestWorld(h, w);
        r.Offer(radioTrack.Id, "Radio B", "New Song");

        await l.StartAsync();
        var radioPick = w.Fetch.For("New Song").PickId;
        Assert.IsType<FetchReadiness.Pending>(w.Coordinator.GetReadiness(radioPick));

        var row = await r.CreateAsync(radioTrack.Id);
        var requestPick = RequestService.PickId(row.Id);
        Assert.True(FetchCoordinator.IsRequestPick(requestPick));
        Assert.False(FetchCoordinator.IsRequestPick(radioPick));
        await r.UntilAsync(row.Id, RequestState.Ready);

        // One job served both picks: one download, one file, kept for the requester.
        Assert.Single(w.Slskd.Enqueued);
        Assert.Equal(["ready"], w.Query("SELECT state FROM fetched_files"));
        Assert.Equal(["1"], w.Query("SELECT requested FROM fetched_files"));
        var item = w.FetchedId("Radio B", "New Song")!.Value;
        Assert.Equal(item.ToString("N"), r.Row(row.Id).JellyfinItemId);
        Assert.Equal(item, Assert.IsType<FetchReadiness.Ready>(w.Coordinator.GetReadiness(radioPick)).ItemId);
        Assert.Equal("Ready: Radio B - New Song", Assert.Single(r.Messages).Text);

        // The radio still gets its song.
        var topUp = await l.TopUpAsync();
        Assert.Contains(item, topUp);
        o.Dispose();
    }

    [Fact]
    public async Task RQ03_A_request_at_the_hourly_download_cap_waits_queued_and_starts_when_the_window_frees()
    {
        await using var r = await CreateAsync(scale: 20, setup: w => w.OfferGood("Daft Punk", "Aerodynamic"));
        r.Offer(501, "Daft Punk", "Aerodynamic");
        var now = r.W.Clock.GetUtcNow();

        // Thirty downloads in the last hour, the oldest ones leaving the window in about 3 s of real time.
        r.W.Coordinator.SeedWindow(Enumerable.Range(0, FetchLimits.FetchesPerHour).Select(i => now - TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(60 + i)));

        var row = await r.CreateAsync(501);
        await r.Worker.TickAsync(CancellationToken.None);

        var waiting = r.Row(row.Id);
        Assert.Equal(RequestState.Queued, waiting.State);
        Assert.Equal("Waiting: hourly download limit", waiting.Detail);
        Assert.Empty(r.W.Slskd.Enqueued);

        await r.UntilAsync(row.Id, RequestState.Ready, timeoutMs: 30_000);

        Assert.Single(r.W.Slskd.Enqueued);
        Assert.Equal(0, r.Row(row.Id).Attempts);
        Assert.Equal("Ready: Daft Punk - Aerodynamic", Assert.Single(r.Messages).Text);
    }

    [Fact]
    public async Task RQ04_A_restart_mid_download_fetches_the_song_again_without_counting_a_try_and_it_still_ends_ready()
    {
        var release = new TaskCompletionSource();
        await using var r = await CreateAsync(scale: 50, setup: w =>
        {
            w.Offer("Daft Punk", "Aerodynamic", ("carol", 2_000_000, FetchWorld.Mp3("Daft Punk", "Aerodynamic")));
            w.Slskd.Peers["carol"] = Held(release.Task);
        });
        r.Offer(501, "Daft Punk", "Aerodynamic");
        var row = await r.CreateAsync(501);
        await r.UntilAsync(row.Id, RequestState.Downloading);
        Assert.Single(r.W.Slskd.Enqueued);

        // The process restarts while the file is coming in. Reconciliation fails the old try; the start pass submits the request again.
        await r.RestartAsync();
        release.SetResult();
        Assert.Equal(1, await r.Worker.ResubmitAsync(CancellationToken.None));
        Assert.Equal(["failed|restart"], r.W.Query("SELECT state || '|' || fail_reason FROM fetched_files ORDER BY id").Take(1).ToList());

        await r.UntilAsync(row.Id, RequestState.Ready);

        var ready = r.Row(row.Id);
        Assert.Equal(0, ready.Attempts);
        Assert.Equal(2, r.W.Slskd.Enqueued.Count);
        Assert.Equal(["failed|restart", "ready|"], r.W.Query("SELECT state || '|' || COALESCE(fail_reason, '') FROM fetched_files ORDER BY id"));
        Assert.Equal(r.W.FetchedId("Daft Punk", "Aerodynamic")!.Value.ToString("N"), ready.JellyfinItemId);
        Assert.Equal("Ready: Daft Punk - Aerodynamic", Assert.Single(r.Messages).Text);
    }

    // ---- albums -----------------------------------------------------------------------------------------------

    private static async Task<(AlbumRig Rig, FakeJellyfin Jellyfin, AlbumWorker Worker)> AlbumSetupAsync()
    {
        var rig = new AlbumRig();
        var jellyfin = new FakeJellyfin
        {
            Sessions = [new SessionInfoDto { Id = "kim-phone", UserId = SongRig.Kim.Id, SupportsRemoteControl = true, Client = "Finamp", DeviceId = "dev-kim" }],
        };
        var notifier = new RequestNotifier(jellyfin, rig.Store, new ListLogger<RequestNotifier>(), rig.Clock, delay: (_, _) => Task.CompletedTask);
        var worker = new AlbumWorker(rig.Requester, notifier, new ListLogger<AlbumWorker>(), rig.Clock, Timeout.InfiniteTimeSpan);
        await Task.CompletedTask;
        return (rig, jellyfin, worker);
    }

    private static async Task PollAsync(AlbumRig rig, AlbumWorker worker)
    {
        rig.Clock.Advance(RequestLimits.AlbumPoll + TimeSpan.FromSeconds(1));
        await worker.TickAsync(CancellationToken.None);
    }

    /// <summary>Lidarr finds nothing, then some files, then all of them; Jellyfin lists the album last; the album request ends ready with one message.</summary>
    private static async Task WalkToReadyAsync(AlbumRig rig, FakeJellyfin jellyfin, AlbumWorker worker, RequestRow row, string fid, string artist, string title)
    {
        var album = rig.Lidarr.AlbumOf(fid);

        await PollAsync(rig, worker);
        Assert.Equal(RequestState.Searching, rig.Row(row.Id).State);

        album.Stats = AlbumRig.Stats(5, 12, 12);
        await PollAsync(rig, worker);
        var downloading = rig.Row(row.Id);
        Assert.Equal(RequestState.Downloading, downloading.State);
        Assert.Equal((5, 12), (downloading.TracksDone!.Value, downloading.TracksTotal!.Value));
        Assert.Equal(["/media/music/" + artist], rig.Library.Nudges);

        album.Stats = AlbumRig.Stats(12, 12, 12);
        await PollAsync(rig, worker);
        Assert.Equal(RequestState.Importing, rig.Row(row.Id).State);
        Assert.Empty(jellyfin.Messages);

        rig.Library.Albums.Add((artist, title, JellyfinAlbum));
        await PollAsync(rig, worker);

        var ready = rig.Row(row.Id);
        Assert.Equal(RequestState.Ready, ready.State);
        Assert.Equal(JellyfinAlbum.ToString("N"), ready.JellyfinItemId);
        var message = Assert.Single(jellyfin.Messages);
        Assert.Equal(("kim-phone", "Encore", $"Ready: {artist} - {title}"), (message.SessionId, message.Header, message.Text));
        Assert.Single(rig.Library.Nudges);
    }

    [Fact]
    public async Task RQ05_An_album_by_a_new_artist_is_added_through_one_POST_album_and_ends_ready_with_a_message()
    {
        var (rig, jellyfin, worker) = await AlbumSetupAsync();
        using var _ = rig;

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer }, rig.Lidarr.Writes);
        await WalkToReadyAsync(rig, jellyfin, worker, row, AlbumRig.OkComputer, "Radiohead", "OK Computer");
        Assert.Equal(new[] { "POST album " + AlbumRig.OkComputer }, rig.Lidarr.Writes);
    }

    [Fact]
    public async Task RQ06_An_album_of_an_artist_Lidarr_already_has_is_monitored_with_PUTs_and_ends_ready_with_a_message()
    {
        var (rig, jellyfin, worker) = await AlbumSetupAsync();
        using var _ = rig;
        var artist = rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: false);
        var album = rig.Lidarr.AddAlbum(AlbumRig.OkComputer, artist, monitored: false);

        var row = await rig.CreateAsync(AlbumRig.OkComputer);

        Assert.Equal(new[] { $"PUT album/monitor [{album.Id}] true", $"PUT artist/{artist.Id}" }, rig.Lidarr.Writes);
        await WalkToReadyAsync(rig, jellyfin, worker, row, AlbumRig.OkComputer, "Radiohead", "OK Computer");
        Assert.Equal(2, rig.Lidarr.Writes.Count);
    }

    [Fact]
    public async Task RQ07_A_missing_album_of_a_monitored_artist_is_added_with_one_POST_and_ends_ready_with_a_message()
    {
        var (rig, jellyfin, worker) = await AlbumSetupAsync();
        using var _ = rig;
        rig.Lidarr.AddArtist(AlbumRig.Radiohead, "Radiohead", monitored: true);

        var row = await rig.CreateAsync(AlbumRig.KidA);

        Assert.Equal(new[] { "POST album " + AlbumRig.KidA }, rig.Lidarr.Writes);
        await WalkToReadyAsync(rig, jellyfin, worker, row, AlbumRig.KidA, "Radiohead", "Kid A");
        Assert.Single(rig.Lidarr.Writes);
    }

    // ---- requests off -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RQ08_With_requests_off_the_radio_fetches_as_in_v0_2_and_no_request_side_exists()
    {
        var h = new OrchestratorHarness();
        var w = new FetchWorld(h, FetchMode.Live);
        w.Radio(w.Track("Radio B", "New Song"));
        w.OfferGood("Radio B", "New Song");
        await w.StartAsync();
        var o = h.CreateOrchestrator();
        await o.RefreshAllowListAsync();
        var l = new Listener(h, o);
        try
        {
            await l.StartAsync();
            var pick = w.Fetch.For("New Song");
            Assert.False(FetchCoordinator.IsRequestPick(pick.PickId));
            Assert.Equal(Encore.Fetch.FetchPriority.Radio, pick.Priority);

            await w.UntilReadyAsync("New Song");
            var fetched = Assert.IsType<FetchReadiness.Ready>(w.Readiness("New Song")).ItemId;
            var topUp = await l.TopUpAsync();

            Assert.Equal(fetched, topUp[0]);
            Assert.Equal(["ready"], w.Query("SELECT state FROM fetched_files"));
            Assert.Equal(["0"], w.Query("SELECT requested FROM fetched_files"));
            Assert.Equal(["0"], w.Query("SELECT COUNT(*) FROM requests"));
            Assert.Single(w.Slskd.Enqueued);
        }
        finally
        {
            o.Dispose();
            await w.DisposeAsync();
            h.Dispose();
        }

        // The host registers nothing of the request side and the API routes do not exist.
        using var rig = new ApiRig(on: false);
        using var config = await rig.GetAsync("/api/v1/config");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, config.StatusCode);
        using var search = await rig.GetAsync("/api/v1/search?q=daft");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, search.StatusCode);
        using var requests = await rig.GetAsync("/api/v1/requests");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, requests.StatusCode);
        Assert.Null(rig.Services.GetService<RequestService>());
        Assert.Null(rig.Services.GetService<IRequestNotifier>());
    }
}
