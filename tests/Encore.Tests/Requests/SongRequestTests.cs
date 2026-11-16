using System.Net;
using System.Text.Json;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Requests.Auth;
using Encore.Requests.Songs;
using Encore.Store;
using Encore.Tests.Discovery;
using Encore.Tests.Picks;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Tests.Requests;

/// <summary>A song-request service over real stores (temp folder), a fake fetch side, owned index and Deezer, and a manual clock.</summary>
internal sealed class SongRig : IDisposable
{
    public static readonly RequestUser Kim = new(Guid.Parse("bbbb0000bbbb0000bbbb0000bbbb0000"), "kim", false, false);
    public static readonly RequestUser Lee = new(Guid.Parse("cccc0000cccc0000cccc0000cccc0000"), "lee", false, false);
    public static readonly RequestUser Nate = new(Guid.Parse("aaaa0000aaaa0000aaaa0000aaaa0000"), "nate", true, false);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-songreq-" + Guid.NewGuid().ToString("N"));
    private readonly EncoreStore _main;

    public SongRig(FetchMode mode = FetchMode.Live, int perDay = 25)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "encore.db");
        _main = EncoreStore.Open(path, Clock);
        FetchStore = FetchStore.Open(path);
        Store = RequestStore.Open(path);
        Settings = new SongRequestSettings(mode, perDay);
        Service = NewService();
    }

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    public FakeFetchRequests Fetch { get; } = new();

    public TestOwned Owned { get; } = new();

    public FakeDeezer Deezer { get; } = new();

    public FetchStore FetchStore { get; }

    public RequestStore Store { get; }

    public SongRequestSettings Settings { get; }

    public ListLogger<RequestService> Log { get; } = new();

    public RequestService Service { get; private set; }

    /// <summary>A fresh service on the same database (what a restart sees: no pick is held).</summary>
    public RequestService NewService()
    {
        Service = new RequestService(Store, Fetch, Owned, Deezer, FetchStore, Settings, Log, Clock);
        return Service;
    }

    public DeezerTrackDetail Track(long id, string artist, string title, int seconds = 200, string? cover = "https://cdn-images.dzcdn.net/c.jpg")
    {
        var detail = new DeezerTrackDetail(
            id, title, title, null, seconds, 1, 1, "2001-03-07",
            new DeezerArtist(id * 10, artist, 100, true), new DeezerAlbumRef(id * 100, "Album " + id, "2001-03-12", cover), []);
        Deezer.TrackDetails[id] = detail;
        return detail;
    }

    public void Own(string artist, string title, Guid item) =>
        Owned.Add(new OwnedEntry(item, TrackKey.Create(artist, title)!.Value, title, null, artist, 200, "/music/x.flac", null, false));

    public async Task<RequestRow> CreateAsync(long id, RequestUser? user = null, HttpStatusCode expect = HttpStatusCode.Created)
    {
        var (outcome, _) = await Service.CreateSongAsync(user ?? Kim, id, CancellationToken.None);
        Assert.Equal((int)expect, outcome.Status);
        return outcome.Row!;
    }

    public RequestRow Row(string id) => Store.Get(id)!;

    public FetchRequest LastRequest => Fetch.Requests.Last();

    public void SetReadiness(RequestRow row, FetchReadiness readiness) => Fetch.Readiness[RequestService.PickId(row.Id)] = readiness;

    public RequestTickResult Tick() => Service.Tick();

    public long Scalar(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, "encore.db"), Pooling = false }.ConnectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        Store.Dispose();
        FetchStore.Dispose();
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

/// <summary>v03 section 15, T87: song requests (create, lifecycle worker, cancel, retry, restart, daily cap, modes, routes).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SongRequestTests
{
    private static readonly Guid OwnedItem = Guid.Parse("33333333333333333333333333333333");
    private static readonly Guid FetchedItem = Guid.Parse("55555555555555555555555555555555");

    // ---- create ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SR01_An_owned_song_is_ready_at_once_with_its_item_and_starts_no_fetch()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "One More Time");
        rig.Own("Daft Punk", "One More Time", OwnedItem);

        var (outcome, notify) = await rig.Service.CreateSongAsync(SongRig.Kim, 1, CancellationToken.None);

        Assert.Equal(201, outcome.Status);
        var row = outcome.Row!;
        Assert.Equal(RequestState.Ready, row.State);
        Assert.Equal(OwnedItem.ToString("N"), row.JellyfinItemId);
        Assert.Equal(rig.Clock.GetUtcNow(), row.ReadyUtc);
        Assert.Empty(rig.Fetch.Requests);
        Assert.Equal(row.Id, Assert.Single(notify).Id);
        Assert.Single(rig.Log.Named("request.ready"));
    }

    [Fact]
    public async Task SR02_A_song_the_radio_fetched_is_marked_requested_and_kept()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "One More Time");
        rig.Own("Daft Punk", "One More Time", FetchedItem);
        var key = TrackKey.Create("Daft Punk", "One More Time")!.Value.Value;
        var fileId = rig.FetchStore.InsertFetched(new NewFetchedFile(
            "job-1", key, "Daft Punk", "One More Time", "deezer_radio", "soulseek", "run-1", "sess", Guid.NewGuid(), rig.Clock.GetUtcNow()));
        rig.FetchStore.UpdateFetched(fileId, FetchState.Ready);

        var row = await rig.CreateAsync(1);

        Assert.Equal(RequestState.Ready, row.State);
        Assert.Equal(1, rig.Scalar($"SELECT requested FROM fetched_files WHERE id = {fileId}"));
        Assert.Equal(1, rig.Scalar($"SELECT COUNT(*) FROM fetched_files WHERE id = {fileId} AND kept_reason = 'requested'"));
        Assert.Contains(rig.Log.Named("request.joined"), e => Equals(e.Fields["With"], "fetched_file"));
    }

    [Fact]
    public async Task SR03_A_new_song_becomes_a_request_job_with_the_60_min_request_deadline_flac_first_and_request_priority()
    {
        using var rig = new SongRig();
        rig.Track(7, "Daft Punk", "Aerodynamic", seconds: 212);

        var row = await rig.CreateAsync(7);

        Assert.Equal(RequestState.Queued, row.State);
        Assert.Equal("Daft Punk", row.Artist);
        Assert.Equal("Aerodynamic", row.Title);
        Assert.Equal("Album 7", row.Album);
        Assert.Equal(2001, row.Year);
        Assert.Equal("https://cdn-images.dzcdn.net/c.jpg", row.CoverUrl);
        Assert.Equal(212, row.DurationSeconds);
        var request = rig.LastRequest;
        Assert.Equal("req:" + row.Id, request.PickId);
        Assert.Equal("request", request.RunId);
        Assert.Equal(string.Empty, request.SessionId);
        Assert.Equal(SongRig.Kim.Id, request.UserId);
        Assert.False(request.DryRun);
        Assert.Equal(rig.Clock.GetUtcNow() + FetchLimits.RequestDeadline, request.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(60), FetchLimits.RequestDeadline);
        Assert.Equal(FetchPriority.Request, request.Priority);
        Assert.Equal(FetchPreference.FlacFirst, request.Preference);
        Assert.Equal(CandidateOrigin.Request, request.Candidate.Origin);
        Assert.Equal("request", request.Candidate.Origin.ToWire());
        Assert.Equal(TrackKey.Create("Daft Punk", "Aerodynamic"), request.Candidate.Key);
        Assert.Equal(7, request.Candidate.DeezerTrackId);
        Assert.Equal(212, request.Candidate.DurationS);
    }

    [Fact]
    public async Task SR04_Two_people_requesting_one_song_get_two_rows_on_one_track_key_and_the_same_person_gets_theirs_back()
    {
        using var rig = new SongRig();
        rig.Track(7, "Daft Punk", "Aerodynamic");

        var kim = await rig.CreateAsync(7, SongRig.Kim);
        var lee = await rig.CreateAsync(7, SongRig.Lee);
        var again = await rig.CreateAsync(7, SongRig.Kim, HttpStatusCode.OK);

        Assert.NotEqual(kim.Id, lee.Id);
        Assert.Equal(kim.Id, again.Id);
        Assert.Equal(2, rig.Fetch.Requests.Count);
        Assert.Single(rig.Fetch.Requests.Select(r => r.Candidate.Key).Distinct());
        Assert.Contains(rig.Log.Named("request.joined"), e => Equals(e.Fields["RequestId"], lee.Id) && Equals(e.Fields["With"], "request"));
        Assert.Equal(2, rig.Store.ActiveForKey(kim.TrackKey!).Count);
    }

    [Fact]
    public async Task SR05_Unknown_Deezer_track_is_404_and_a_Deezer_failure_is_503()
    {
        using var rig = new SongRig();
        var (missing, _) = await rig.Service.CreateSongAsync(SongRig.Kim, 404, CancellationToken.None);
        Assert.Equal((404, RequestCodes.NotFound), (missing.Status, missing.Error));

        rig.Deezer.Failure = new DeezerException("track", DiscoveryFailure.Timeout, "track timed out");
        var (down, _) = await rig.Service.CreateSongAsync(SongRig.Kim, 1, CancellationToken.None);
        Assert.Equal((503, RequestCodes.DeezerUnavailable), (down.Status, down.Error));
        Assert.Empty(rig.Store.ListAll());
    }

    [Fact]
    public async Task SR06_The_daily_cap_counts_created_requests_cancelled_ones_too_and_admins_have_none()
    {
        using var rig = new SongRig(perDay: 2);
        for (var i = 1; i <= 4; i++)
        {
            rig.Track(i, "Artist", "Song " + i);
        }

        var first = await rig.CreateAsync(1);
        Assert.True(rig.Service.Cancel(SongRig.Kim, first.Id).Ok);
        await rig.CreateAsync(2);
        var (over, _) = await rig.Service.CreateSongAsync(SongRig.Kim, 3, CancellationToken.None);
        Assert.Equal((429, RequestCodes.DailyLimit), (over.Status, over.Error));
        Assert.Single(rig.Log.Named("request.limit"));

        await rig.CreateAsync(3, SongRig.Nate);
        await rig.CreateAsync(4, SongRig.Nate);
        await rig.CreateAsync(1, SongRig.Nate);

        rig.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        await rig.CreateAsync(3);
    }

    [Fact]
    public async Task SR07_Fetch_mode_off_answers_503_songs_unavailable_and_dry_run_ends_failed_with_the_would_download_detail()
    {
        using (var off = new SongRig(FetchMode.Off))
        {
            off.Track(1, "Daft Punk", "Aerodynamic");
            var (outcome, _) = await off.Service.CreateSongAsync(SongRig.Kim, 1, CancellationToken.None);
            Assert.Equal((503, RequestCodes.SongsUnavailable), (outcome.Status, outcome.Error));
        }

        using var rig = new SongRig(FetchMode.DryRun);
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);
        Assert.True(rig.LastRequest.DryRun);

        rig.SetReadiness(row, new FetchReadiness.Failed(FetchReasons.DryRun));
        var tick = rig.Tick();

        var failed = rig.Row(row.Id);
        Assert.Equal(RequestState.Failed, failed.State);
        Assert.Equal("dry run: would download Daft Punk - Aerodynamic", failed.Detail);
        Assert.Equal(FetchReasons.DryRun, failed.FailReason);
        Assert.Equal(row.Id, Assert.Single(tick.Notify).Id);
        Assert.Contains("req:" + row.Id, rig.Fetch.Forgotten);
    }

    [Fact]
    public async Task SR08_Fetching_unavailable_at_create_queues_the_request_and_a_later_pass_submits_it()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        rig.Fetch.Availability = new FetchAvailability(false, AvailabilityReasons.SlskdUnreachable);

        var row = await rig.CreateAsync(1);

        Assert.Equal(RequestState.Queued, row.State);
        Assert.Contains("slskd_unreachable", rig.Row(row.Id).Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Fetch.Requests);

        rig.Fetch.Availability = FetchAvailability.Yes;
        Assert.Equal(1, rig.Tick().Submitted);
        Assert.Equal("req:" + row.Id, rig.LastRequest.PickId);
        Assert.Equal(RequestService.Details.Queued, rig.Row(row.Id).Detail);
        Assert.Equal(0, rig.Tick().Submitted);
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SR10_Readiness_stages_map_to_request_states_and_details()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);

        (FetchReadiness Readiness, string State, string Detail)[] table =
        [
            (new FetchReadiness.Pending(FetchStage.Queued) { Waiting = FetchWaiting.HourlyCap }, RequestState.Queued, "Waiting: hourly download limit"),
            (new FetchReadiness.Pending(FetchStage.Searching), RequestState.Searching, "Searching Soulseek"),
            (new FetchReadiness.Pending(FetchStage.Downloading), RequestState.Downloading, "Downloading from a peer"),
            (new FetchReadiness.Pending(FetchStage.Importing), RequestState.Importing, "Adding to the library"),
            (new FetchReadiness.Pending(FetchStage.Ingesting), RequestState.Importing, "Waiting for Jellyfin to list it"),
        ];
        foreach (var (readiness, state, detail) in table)
        {
            rig.SetReadiness(row, readiness);
            rig.Tick();
            var now = rig.Row(row.Id);
            Assert.Equal((state, detail), (now.State, now.Detail));
        }

        rig.SetReadiness(row, new FetchReadiness.Ready(FetchedItem));
        var tick = rig.Tick();
        var ready = rig.Row(row.Id);
        Assert.Equal(RequestState.Ready, ready.State);
        Assert.Equal(FetchedItem.ToString("N"), ready.JellyfinItemId);
        Assert.NotNull(ready.ReadyUtc);
        Assert.Contains("req:" + row.Id, rig.Fetch.Forgotten);
        Assert.Equal(row.Id, Assert.Single(tick.Notify).Id);
        Assert.Contains(rig.Log.Named("request.state"), e => Equals(e.Fields["To"], RequestState.Downloading));
    }

    [Fact]
    public async Task SR11_Ready_marks_the_fetched_file_requested()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);
        var fileId = rig.FetchStore.InsertFetched(new NewFetchedFile(
            "job-9", row.TrackKey!, "Daft Punk", "Aerodynamic", "request", "soulseek", "request", "", SongRig.Kim.Id, rig.Clock.GetUtcNow()));
        rig.FetchStore.UpdateFetched(fileId, FetchState.Ready);

        rig.SetReadiness(row, new FetchReadiness.Ready(FetchedItem));
        rig.Tick();

        Assert.Equal(1, rig.Scalar($"SELECT requested FROM fetched_files WHERE id = {fileId}"));
    }

    [Fact]
    public async Task SR12_A_failed_try_waits_1_h_then_6_h_and_the_third_failure_fails_the_request()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);
        var start = rig.Clock.GetUtcNow();

        rig.SetReadiness(row, new FetchReadiness.Failed(FetchFailure.NoResults));
        rig.Tick();
        var waiting = rig.Row(row.Id);
        Assert.Equal((RequestState.Queued, 1, start.AddHours(1)), (waiting.State, waiting.Attempts, waiting.NextTryUtc));
        Assert.Equal("Not found yet, trying again at 19:00 UTC", waiting.Detail);
        Assert.Equal(FetchFailure.NoResults, waiting.FailReason);
        Assert.Single(rig.Log.Named("request.retry_scheduled"));

        rig.Clock.Advance(TimeSpan.FromMinutes(59));
        rig.Tick();
        Assert.Single(rig.Fetch.Requests);

        rig.Fetch.Readiness.Clear();
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, rig.Tick().Submitted);
        Assert.Equal(2, rig.Fetch.Requests.Count);
        Assert.Null(rig.Row(row.Id).NextTryUtc);

        rig.SetReadiness(row, new FetchReadiness.Failed(FetchFailure.AllPeersFailed));
        rig.Tick();
        var second = rig.Row(row.Id);
        Assert.Equal((2, rig.Clock.GetUtcNow().AddHours(6)), (second.Attempts, second.NextTryUtc));

        rig.Fetch.Readiness.Clear();
        rig.Clock.Advance(TimeSpan.FromHours(6));
        rig.Tick();
        Assert.Equal(3, rig.Fetch.Requests.Count);
        rig.SetReadiness(row, new FetchReadiness.Failed(FetchFailure.Deadline));
        var tick = rig.Tick();

        var failed = rig.Row(row.Id);
        Assert.Equal((RequestState.Failed, 3, FetchFailure.Deadline), (failed.State, failed.Attempts, failed.FailReason));
        Assert.Null(failed.NextTryUtc);
        Assert.Single(tick.Notify);
        Assert.Single(rig.Log.Named("request.failed"));
    }

    [Theory]
    [InlineData(FetchReasons.ImportFailed)]
    [InlineData(FetchReasons.NotScanned)]
    public async Task SR13_Import_failed_and_radio_dir_not_scanned_fail_at_once(string reason)
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);

        rig.SetReadiness(row, new FetchReadiness.Failed(reason));
        rig.Tick();

        var failed = rig.Row(row.Id);
        Assert.Equal((RequestState.Failed, reason, 1), (failed.State, failed.FailReason, failed.Attempts));
    }

    [Fact]
    public async Task SR14_An_ingest_timeout_waits_on_the_owned_index_and_is_ready_when_the_refresh_finds_the_song()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);

        rig.SetReadiness(row, new FetchReadiness.Failed(FetchReasons.IngestTimeout));
        rig.Tick();
        var waiting = rig.Row(row.Id);
        Assert.Equal((RequestState.Importing, FetchReasons.IngestTimeout), (waiting.State, waiting.FailReason));

        rig.Clock.Advance(TimeSpan.FromMinutes(30));
        rig.Own("Daft Punk", "Aerodynamic", FetchedItem);
        var tick = rig.Tick();

        var ready = rig.Row(row.Id);
        Assert.Equal(RequestState.Ready, ready.State);
        Assert.Equal(FetchedItem.ToString("N"), ready.JellyfinItemId);
        Assert.Null(ready.FailReason);
        Assert.Single(rig.Fetch.Requests);
        Assert.Single(tick.Notify);
    }

    [Fact]
    public async Task SR15_A_pick_the_fetch_side_lost_is_submitted_again_without_counting_a_try()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);

        rig.SetReadiness(row, new FetchReadiness.Failed(FetchReasons.Unknown));
        rig.Tick();
        rig.Fetch.Readiness.Clear();
        rig.Tick();

        Assert.Equal(2, rig.Fetch.Requests.Count);
        Assert.Equal(0, rig.Row(row.Id).Attempts);
    }

    [Fact]
    public async Task SR16_A_start_resubmits_every_active_request_and_an_owned_one_goes_to_ready()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        rig.Track(2, "Daft Punk", "Digital Love");
        rig.Track(3, "Daft Punk", "Voyager");
        var searching = await rig.CreateAsync(1);
        var downloading = await rig.CreateAsync(2);
        var nowOwned = await rig.CreateAsync(3);
        rig.Store.Update(searching.Id, RequestState.Searching, rig.Clock.GetUtcNow());
        rig.Store.Update(downloading.Id, RequestState.Downloading, rig.Clock.GetUtcNow());
        rig.Own("Daft Punk", "Voyager", OwnedItem);
        rig.Fetch.Requests.Clear();

        var service = rig.NewService();
        var log = new ListLogger<RequestWorker>();
        var worker = new RequestWorker(service, NullRequestNotifier.Instance, log, rig.Clock);
        var count = await worker.ResubmitAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(
            new[] { "req:" + searching.Id, "req:" + downloading.Id }.Order(StringComparer.Ordinal),
            rig.Fetch.Requests.Select(r => r.PickId).Order(StringComparer.Ordinal));
        Assert.Equal(RequestState.Queued, rig.Row(searching.Id).State);
        Assert.Equal(RequestState.Ready, rig.Row(nowOwned.Id).State);
        var entry = Assert.Single(log.Named("request.resubmitted"));
        Assert.Equal(2, entry.Fields["Count"]);
    }

    // ---- cancel and retry ----------------------------------------------------------------------------------------

    [Fact]
    public async Task SR20_Cancel_abandons_the_pick_and_only_an_admin_may_cancel_after_searching()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        rig.Track(2, "Daft Punk", "Digital Love");
        var queued = await rig.CreateAsync(1);
        var downloading = await rig.CreateAsync(2);
        rig.SetReadiness(downloading, new FetchReadiness.Pending(FetchStage.Downloading));
        rig.Tick();

        var cancelled = rig.Service.Cancel(SongRig.Kim, queued.Id);
        Assert.Equal(200, cancelled.Status);
        Assert.Equal(RequestState.Cancelled, cancelled.Row!.State);
        Assert.Contains(("req:" + queued.Id, "request_cancelled"), rig.Fetch.Abandoned);
        Assert.Contains("req:" + queued.Id, rig.Fetch.Forgotten);
        Assert.Single(rig.Log.Named("request.cancelled"));

        Assert.Equal(409, rig.Service.Cancel(SongRig.Kim, queued.Id).Status);
        Assert.Equal(409, rig.Service.Cancel(SongRig.Kim, downloading.Id).Status);
        Assert.Equal(404, rig.Service.Cancel(SongRig.Lee, downloading.Id).Status);
        var byAdmin = rig.Service.Cancel(SongRig.Nate, downloading.Id);
        Assert.Equal(RequestState.Cancelled, byAdmin.Row!.State);
        Assert.Equal(SongRig.Nate.Id.ToString("N"), byAdmin.Row.CancelledBy);

        rig.Tick();
        Assert.Equal(2, rig.Fetch.Requests.Count);
    }

    [Fact]
    public async Task SR21_Retry_puts_a_failed_request_back_in_the_queue_with_attempts_reset()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);
        Assert.Equal(409, rig.Service.Retry(SongRig.Kim, row.Id).Outcome.Status);

        rig.SetReadiness(row, new FetchReadiness.Failed(FetchReasons.ImportFailed));
        rig.Tick();
        rig.Fetch.Readiness.Clear();
        Assert.Equal(404, rig.Service.Retry(SongRig.Lee, row.Id).Outcome.Status);

        var (outcome, _) = rig.Service.Retry(SongRig.Kim, row.Id);

        Assert.Equal(200, outcome.Status);
        Assert.Equal((RequestState.Queued, 0), (outcome.Row!.State, outcome.Row.Attempts));
        Assert.Null(outcome.Row.FailReason);
        Assert.Equal(2, rig.Fetch.Requests.Count);
    }

    [Fact]
    public async Task SR22_Get_shows_a_request_to_its_owner_and_admins_only()
    {
        using var rig = new SongRig();
        rig.Track(1, "Daft Punk", "Aerodynamic");
        var row = await rig.CreateAsync(1);

        Assert.Equal(200, rig.Service.Get(SongRig.Kim, row.Id).Status);
        Assert.Equal(200, rig.Service.Get(SongRig.Nate, row.Id).Status);
        Assert.Equal(404, rig.Service.Get(SongRig.Lee, row.Id).Status);
        Assert.Equal(404, rig.Service.Get(SongRig.Kim, "nope").Status);
    }

    // ---- routes --------------------------------------------------------------------------------------------------

    private static DeezerTrackDetail Detail(long id, string artist, string title) => new(
        id, title, title, null, 200, 1, 1, "2001-03-07", new DeezerArtist(1, artist, 1, true),
        new DeezerAlbumRef(2, "Discovery", "2001-03-12", "https://cdn-images.dzcdn.net/c.jpg"), []);

    [Fact]
    public async Task SR30_POST_requests_creates_from_Deezer_data_only_and_returns_the_request_json()
    {
        using var rig = new ApiRig();
        rig.Deezer.TrackDetails[7] = Detail(7, "Daft Punk", "Aerodynamic");

        using var response = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 7, artist = "Evil", title = "Injected" }, bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ApiRig.JsonOf(response);
        Assert.Equal("song", json.GetProperty("kind").GetString());
        Assert.Equal("queued", json.GetProperty("state").GetString());
        Assert.Equal("Daft Punk", json.GetProperty("artist").GetString());
        Assert.Equal("Aerodynamic", json.GetProperty("title").GetString());
        Assert.Equal("Discovery", json.GetProperty("album").GetString());
        Assert.Equal(2001, json.GetProperty("year").GetInt32());
        Assert.Equal("kim", json.GetProperty("userName").GetString());
        Assert.Equal(0, json.GetProperty("attempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("progress").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("itemId").ValueKind);
        foreach (var name in new[] { "id", "detail", "coverUrl", "createdUtc", "updatedUtc", "readyUtc", "failReason" })
        {
            Assert.True(json.TryGetProperty(name, out _), name);
        }

        Assert.Equal("req:" + json.GetProperty("id").GetString(), rig.Fetch.Requests.Single().PickId);

        using var again = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 7 }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task SR31_POST_requests_refuses_bad_bodies_people_not_allowed_and_albums_for_now()
    {
        using var rig = new ApiRig();
        rig.Deezer.TrackDetails[7] = Detail(7, "Daft Punk", "Aerodynamic");

        using var notAllowed = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 7 }, bearer: ApiRig.LeeToken);
        Assert.Equal(HttpStatusCode.Forbidden, notAllowed.StatusCode);
        Assert.Equal("not_allowed", (await ApiRig.JsonOf(notAllowed)).GetProperty("error").GetString());

        using var noId = await rig.PostAsync("/api/v1/requests", new { kind = "song" }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        using var badKind = await rig.PostAsync("/api/v1/requests", new { kind = "video", deezerTrackId = 7 }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        using var album = await rig.PostAsync("/api/v1/requests", new { kind = "album", foreignAlbumId = "abc" }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, album.StatusCode);
        Assert.Equal("albums_unavailable", (await ApiRig.JsonOf(album)).GetProperty("error").GetString());
        using var noToken = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 7 });
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    [Fact]
    public async Task SR32_The_daily_cap_answers_429_daily_limit()
    {
        using var rig = new ApiRig(new() { ["ENCORE_REQUEST_SONGS_PER_DAY"] = "1" });
        rig.Deezer.TrackDetails[7] = Detail(7, "Daft Punk", "Aerodynamic");
        rig.Deezer.TrackDetails[8] = Detail(8, "Daft Punk", "Voyager");

        using var first = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 7 }, bearer: ApiRig.KimToken);
        using var second = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 8 }, bearer: ApiRig.KimToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("daily_limit", (await ApiRig.JsonOf(second)).GetProperty("error").GetString());
        using var me = await rig.GetAsync("/api/v1/me", bearer: ApiRig.KimToken);
        Assert.Equal(0, (await ApiRig.JsonOf(me)).GetProperty("songsLeftToday").GetInt32());
    }

    [Fact]
    public async Task SR33_Mode_off_answers_503_for_a_song_but_the_GET_routes_still_work()
    {
        using var rig = new ApiRig(songMode: FetchMode.Off);
        rig.Deezer.TrackDetails[7] = Detail(7, "Daft Punk", "Aerodynamic");
        var store = rig.Services.GetRequiredService<RequestStore>();
        store.Insert(new NewRequest("old0000000000000000000000000000a", "song", ApiRig.Kim.ToString("N"), "kim", "Daft Punk", DateTimeOffset.UtcNow, Title: "Voyager"));

        using var post = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 7 }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        Assert.Equal("songs_unavailable", (await ApiRig.JsonOf(post)).GetProperty("error").GetString());

        using var list = await rig.GetAsync("/api/v1/requests?scope=mine", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await ApiRig.JsonOf(list)).EnumerateArray());
        using var one = await rig.GetAsync("/api/v1/requests/old0000000000000000000000000000a", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
    }

    [Fact]
    public async Task SR34_GET_lists_respect_scope_owner_and_admin_rules()
    {
        using var rig = new ApiRig();
        var store = rig.Services.GetRequiredService<RequestStore>();
        var now = DateTimeOffset.UtcNow;
        store.Insert(new NewRequest("kim0000000000000000000000000000a", "song", ApiRig.Kim.ToString("N"), "kim", "A", now, Title: "One"));
        store.Insert(new NewRequest("lee0000000000000000000000000000a", "song", ApiRig.Lee.ToString("N"), "lee", "B", now.AddSeconds(1), Title: "Two"));
        store.Update("lee0000000000000000000000000000a", RequestState.Failed, now);

        using var mine = await rig.GetAsync("/api/v1/requests", bearer: ApiRig.KimToken);
        Assert.Equal(new string?[] { "kim0000000000000000000000000000a" }, (await ApiRig.JsonOf(mine)).EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToArray());

        using var allByKim = await rig.GetAsync("/api/v1/requests?scope=all", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.Forbidden, allByKim.StatusCode);
        Assert.Equal("admin_only", (await ApiRig.JsonOf(allByKim)).GetProperty("error").GetString());

        using var all = await rig.GetAsync("/api/v1/requests?scope=all", bearer: ApiRig.NateToken);
        Assert.Equal(2, (await ApiRig.JsonOf(all)).GetArrayLength());
        using var active = await rig.GetAsync("/api/v1/requests?scope=all&active=true", bearer: ApiRig.NateToken);
        Assert.Equal("kim0000000000000000000000000000a", (await ApiRig.JsonOf(active)).EnumerateArray().Single().GetProperty("id").GetString());

        using var others = await rig.GetAsync("/api/v1/requests/lee0000000000000000000000000000a", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.NotFound, others.StatusCode);
        using var badScope = await rig.GetAsync("/api/v1/requests?scope=everyone", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
    }

    [Fact]
    public async Task SR35_DELETE_cancels_and_retry_requeues_through_the_routes()
    {
        using var rig = new ApiRig();
        rig.Deezer.TrackDetails[7] = Detail(7, "Daft Punk", "Aerodynamic");
        using var created = await rig.PostAsync("/api/v1/requests", new { kind = "song", deezerTrackId = 7 }, bearer: ApiRig.KimToken);
        var id = (await ApiRig.JsonOf(created)).GetProperty("id").GetString();

        using var retryActive = await rig.PostAsync($"/api/v1/requests/{id}/retry", new { }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.Conflict, retryActive.StatusCode);

        using var cancel = await rig.DeleteAsync($"/api/v1/requests/{id}", bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("cancelled", (await ApiRig.JsonOf(cancel)).GetProperty("state").GetString());
        Assert.Contains(("req:" + id, "request_cancelled"), rig.Fetch.Abandoned);

        var store = rig.Services.GetRequiredService<RequestStore>();
        store.Update(id!, RequestState.Failed, DateTimeOffset.UtcNow, new RequestUpdate(FailReason: "no_results", Attempts: 3));
        using var retry = await rig.PostAsync($"/api/v1/requests/{id}/retry", new { }, bearer: ApiRig.KimToken);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var json = await ApiRig.JsonOf(retry);
        Assert.Equal(("queued", 0), (json.GetProperty("state").GetString(), json.GetProperty("attempts").GetInt32()));
    }
}
