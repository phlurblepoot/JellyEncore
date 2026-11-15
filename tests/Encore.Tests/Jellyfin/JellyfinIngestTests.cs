using System.Net;
using Encore.Tests.Discovery;
using Encore.Fetch;
using Encore.Jellyfin;
using Encore.Tests.EndToEnd;
using Encore.Tests.Watch;

namespace Encore.Tests.Jellyfin;

/// <summary>Jellyfin ingest client calls (JI01, JI03) over the scripted HTTP handler.</summary>
public sealed class JellyfinIngestClientTests
{
    private const string Key = "SECRETKEY0123456789abcdef";
    private static readonly Guid Library = Guid.Parse("32e0aa11-2233-4455-8899-aabbccddeeff");

    private static (JellyfinClient Client, ScriptedHandler Script) Build()
    {
        var script = new ScriptedHandler();
        var auth = new JellyfinAuthHandler(JellyfinAuth.BuildParameter(Key, "encore-0123456789ab", "0.2.0")) { InnerHandler = script };
        var http = new HttpClient(auth) { BaseAddress = new Uri("http://jellyfin.test:8096/jf/"), Timeout = TimeSpan.FromSeconds(10) };
        var options = new JellyfinClientOptions { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], Jitter = 0 };
        return (new JellyfinClient(http, options), script);
    }

    [Fact]
    public async Task JI01_Media_updated_sends_the_exact_body_and_accepts_204()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.NoContent);

        await client.ReportMediaUpdatedAsync(["/music-radio/soulseek/Daft Punk/Discovery (2001)/01 - One More Time.flac"]);

        var sent = Assert.Single(script.Sent);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/jf/Library/Media/Updated", sent.PathAndQuery);
        Assert.Equal(
            "{\"Updates\":[{\"Path\":\"/music-radio/soulseek/Daft Punk/Discovery (2001)/01 - One More Time.flac\",\"UpdateType\":\"Created\"}]}",
            sent.Body);
        Assert.StartsWith("application/json", sent.ContentType);
    }

    [Fact]
    public async Task JI01_Media_updated_keeps_non_ascii_characters_and_lists_every_path()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.NoContent);

        await client.ReportMediaUpdatedAsync(["/r/Beyoncé/a.mp3", "/r/b.mp3"]);

        Assert.Equal(
            "{\"Updates\":[{\"Path\":\"/r/Beyoncé/a.mp3\",\"UpdateType\":\"Created\"},{\"Path\":\"/r/b.mp3\",\"UpdateType\":\"Created\"}]}",
            script.Sent[0].Body);
    }

    [Fact]
    public async Task JI01_Media_updated_is_not_retried_on_a_5xx()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.ServiceUnavailable).Reply(HttpStatusCode.NoContent);

        await Assert.ThrowsAsync<JellyfinRequestException>(() => client.ReportMediaUpdatedAsync(["/music-radio/a.flac"]));

        Assert.Single(script.Sent);
    }

    [Fact]
    public async Task JI01_Media_updated_with_a_rejected_key_throws_an_auth_error()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<JellyfinAuthException>(() => client.ReportMediaUpdatedAsync(["/music-radio/a.flac"]));
    }

    [Fact]
    public async Task JI01_Media_updated_needs_at_least_one_path()
    {
        var (client, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ReportMediaUpdatedAsync([]));
    }

    [Fact]
    public async Task JI02b_Physical_paths_are_a_plain_list_of_strings()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, "[\"/music\",\"/music-radio\"]");

        var paths = await client.GetPhysicalPathsAsync();

        Assert.Equal("GET", script.Sent[0].Method);
        Assert.Equal("/jf/Library/PhysicalPaths", script.Sent[0].PathAndQuery);
        Assert.Equal(["/music", "/music-radio"], paths);
    }

    [Fact]
    public async Task JI02_Virtual_folders_parse_names_locations_collection_type_and_item_id()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, """
            [{"Name":"Music","Locations":["/music","/music-radio"],"CollectionType":"music","LibraryOptions":{},
              "ItemId":"32e0aa11223344558899aabbccddeeff","RefreshStatus":"Idle"},
             {"Name":"Mixed","Locations":["/misc"],"CollectionType":null,"ItemId":"00000000000000000000000000000001"}]
            """);

        var folders = await client.GetVirtualFoldersAsync();

        Assert.Equal("GET", script.Sent[0].Method);
        Assert.Equal("/jf/Library/VirtualFolders", script.Sent[0].PathAndQuery);
        Assert.Equal(2, folders.Count);
        Assert.Equal("Music", folders[0].Name);
        Assert.Equal(["/music", "/music-radio"], folders[0].Locations);
        Assert.Equal("music", folders[0].CollectionType);
        Assert.Equal(Guid.Parse("32e0aa11-2233-4455-8899-aabbccddeeff"), folders[0].ItemId);
        Assert.Null(folders[1].CollectionType);
    }

    [Fact]
    public async Task JI03_Recent_audio_query_string_is_exact()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.OK, """{"Items":[{"Id":"9d8c7b6a5f4e3d2c1b0a998877665544","Name":"One More Time","Type":"Audio","Path":"/music-radio/soulseek/a.flac","DateCreated":"2026-11-14T12:00:00.0000000Z"}],"TotalRecordCount":0,"StartIndex":0}""");

        var items = await client.GetRecentAudioAsync(Library, 50);

        Assert.Equal(
            "/jf/Items?parentId=32e0aa11223344558899aabbccddeeff&recursive=true&includeItemTypes=Audio&fields=Path,DateCreated&sortBy=DateCreated&sortOrder=Descending&limit=50&enableTotalRecordCount=false",
            script.Sent[0].PathAndQuery);
        var item = Assert.Single(items);
        Assert.Equal("/music-radio/soulseek/a.flac", item.Path);
        Assert.Equal(Guid.Parse("9d8c7b6a-5f4e-3d2c-1b0a-998877665544"), item.Id);
    }

    [Fact]
    public async Task JI08_Recent_audio_retries_a_5xx_like_any_get()
    {
        var (client, script) = Build();
        script.Reply(HttpStatusCode.BadGateway).Reply(HttpStatusCode.OK, """{"Items":[],"TotalRecordCount":0}""");

        var items = await client.GetRecentAudioAsync(Library, 50);

        Assert.Empty(items);
        Assert.Equal(2, script.Sent.Count);
    }
}

/// <summary>JellyfinIngest: library check and the shared poll loop (JI02, JI04-JI09, JI11) on a manual clock.</summary>
public sealed class JellyfinIngestTests
{
    private const string RadioDir = "/music-radio";
    private const string PathA = "/music-radio/soulseek/Daft Punk/Discovery (2001)/01 - One More Time.flac";
    private const string PathB = "/music-radio/soulseek/Daft Punk/Discovery (2001)/02 - Aerodynamic.flac";
    private static readonly Guid MusicLibrary = Guid.Parse("32e0aa11-2233-4455-8899-aabbccddeeff");

    private readonly ManualClock _clock = new();
    private readonly FakeJellyfin _jellyfin;
    private readonly ListLogger<JellyfinIngest> _log = new();
    private readonly JellyfinIngest _ingest;

    public JellyfinIngestTests()
    {
        _jellyfin = new FakeJellyfin { Clock = _clock, VirtualFolders = [Music("/music-radio")] };
        _ingest = new JellyfinIngest(_jellyfin, RadioDir, _log, _clock);
    }

    private static VirtualFolderInfo Music(params string[] locations) =>
        new() { Name = "Music", Locations = locations, CollectionType = "music", ItemId = MusicLibrary };

    /// <summary>Waits for poll number <paramref name="calls"/> to be made and the loop to be sleeping, then moves the clock 5 s.</summary>
    private async Task Tick(int calls)
    {
        await Wait.Until(() => _jellyfin.RecentCalls.Count >= calls && _clock.PendingTimers >= 1, $"poll {calls} done and the loop sleeping");
        _clock.Advance(FetchLimits.IngestPoll);
    }

    // ---- Library check (JI02) ----------------------------------------------------------------

    [Theory]
    [InlineData("/music-radio", true)]
    [InlineData("/music-radio/", true)]
    [InlineData("/music-radio2", false)]
    [InlineData("/", true)]
    [InlineData("/music", false)]
    public async Task JI02_Location_must_be_the_radio_dir_or_a_parent_by_whole_segments(string location, bool expected)
    {
        _jellyfin.VirtualFolders = [Music(location)];

        var check = await _ingest.CheckLibraryAsync();

        Assert.Equal(expected, check.InLibrary);
        Assert.Equal(expected ? MusicLibrary : null, check.LibraryItemId);
    }

    [Fact]
    public async Task JI02_A_parent_location_holds_the_radio_dir()
    {
        var ingest = new JellyfinIngest(_jellyfin, "/media/music/radio", _log, _clock);
        _jellyfin.VirtualFolders = [Music("/media/music")];

        var check = await ingest.CheckLibraryAsync();

        Assert.True(check.InLibrary);
    }

    [Fact]
    public async Task JI02_A_non_music_library_does_not_count()
    {
        _jellyfin.VirtualFolders = [new VirtualFolderInfo { Name = "Movies", Locations = ["/music-radio"], CollectionType = "movies", ItemId = MusicLibrary }];

        var check = await _ingest.CheckLibraryAsync();

        Assert.False(check.InLibrary);
        Assert.Null(check.LibraryItemId);
    }

    [Fact]
    public async Task JI02_The_library_check_is_logged_and_warns_when_the_dir_is_in_no_library()
    {
        await _ingest.CheckLibraryAsync();
        _jellyfin.VirtualFolders = [];
        await _ingest.CheckLibraryAsync();

        var entries = _log.Named("ingest.library_check").ToList();
        Assert.Equal([Microsoft.Extensions.Logging.LogLevel.Information, Microsoft.Extensions.Logging.LogLevel.Warning], entries.Select(e => e.Level));
        Assert.Equal(7360, entries[0].EventId.Id);
        Assert.Equal(true, entries[0].Fields["RadioDirInLibrary"]);
        Assert.Equal(false, entries[1].Fields["RadioDirInLibrary"]);
    }

    // ---- Waiting for the item ------------------------------------------------------------------

    [Fact]
    public async Task JI04_Item_found_on_the_third_poll_is_ready_after_about_ten_seconds()
    {
        _jellyfin.IngestDelay = TimeSpan.FromSeconds(7); // not yet listed at 0 s or 5 s, listed at 10 s
        var task = _ingest.IngestAsync("job-1", PathA);

        await Tick(1);
        await Tick(2);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10)); // the third poll runs at t = 10 s
        Assert.True(result.IsReady);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Waited);
        Assert.Equal(_jellyfin.ItemIdOf(PathA), result.ItemId);
        Assert.Equal(3, _jellyfin.RecentCalls.Count);
        Assert.Equal([PathA], _jellyfin.MediaUpdatedPaths);
        var ready = Assert.Single(_log.Named("ingest.ready"));
        Assert.Equal(10_000L, ready.Fields["WaitedMs"]);
        Assert.Single(_log.Named("ingest.reported"));
    }

    [Fact]
    public async Task JI05_Not_found_within_three_minutes_is_a_timeout()
    {
        _jellyfin.IngestDelay = null; // Jellyfin never lists it
        var task = _ingest.IngestAsync("job-1", PathA);

        for (var polls = 1; polls < 37; polls++)
        {
            await Tick(polls);
        }

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(IngestOutcome.Timeout, result.Outcome);
        Assert.Null(result.ItemId);
        Assert.Equal(FetchLimits.IngestWait, result.Waited);
        Assert.Equal(37, _jellyfin.RecentCalls.Count); // at 0, 5, ... 180 s
        Assert.Single(_log.Named("ingest.timeout"));
    }

    [Fact]
    public async Task JI06_Two_awaited_paths_share_one_http_call_per_poll()
    {
        _jellyfin.IngestDelay = TimeSpan.FromSeconds(7);
        var a = _ingest.IngestAsync("job-a", PathA);
        await Wait.Until(() => _jellyfin.RecentCalls.Count >= 1 && _clock.PendingTimers >= 1, "first poll done");
        var b = _ingest.IngestAsync("job-b", PathB);
        await Wait.Until(() => _jellyfin.MediaUpdatedPaths.Count == 2, "second path reported");
        await Task.Delay(100);

        await Tick(1);
        await Tick(2);

        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, r => Assert.True(r.IsReady));
        Assert.NotEqual(results[0].ItemId, results[1].ItemId);
        Assert.Equal(3, _jellyfin.RecentCalls.Count); // one per poll, not two
    }

    [Fact]
    public async Task JI07_Path_match_is_exact_and_case_sensitive()
    {
        _jellyfin.IngestDelay = null;
        var lookalike = Guid.NewGuid();
        _jellyfin.RecentExtra.Add(new BaseItemDto { Id = lookalike, Type = "Audio", Path = PathA.ToUpperInvariant() });
        var task = _ingest.IngestAsync("job-1", PathA);

        await Tick(1);
        await Tick(2);
        Assert.False(task.IsCompleted);

        var exact = Guid.NewGuid();
        _jellyfin.RecentExtra.Add(new BaseItemDto { Id = exact, Type = "Audio", Path = PathA });
        await Tick(3);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(exact, result.ItemId);
    }

    [Fact]
    public async Task JI08_A_failed_poll_does_not_stop_polling()
    {
        _jellyfin.IngestDelay = TimeSpan.Zero;
        _jellyfin.RecentFailures.Enqueue(new JellyfinRequestException("GetRecentAudio", 503));
        var task = _ingest.IngestAsync("job-1", PathA);

        await Tick(1);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsReady);
        Assert.Equal(2, _jellyfin.RecentCalls.Count);
        Assert.Single(_log.Named("ingest.poll_failed"));
    }

    [Fact]
    public async Task JI08_An_unexpected_poll_error_does_not_end_the_shared_loop()
    {
        _jellyfin.IngestDelay = TimeSpan.Zero;
        _jellyfin.RecentFailures.Enqueue(new HttpRequestException("boom"));
        var task = _ingest.IngestAsync("job-1", PathA);

        await Tick(1);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsReady);
        Assert.Equal(2, _jellyfin.RecentCalls.Count);
        Assert.Single(_log.Named("ingest.poll_failed"));
    }

    [Fact]
    public async Task JI09_Radio_dir_in_no_library_is_refused_without_reporting_or_polling()
    {
        _jellyfin.VirtualFolders = [Music("/music")];

        var result = await _ingest.IngestAsync("job-1", PathA);

        Assert.Equal(IngestOutcome.NotInLibrary, result.Outcome);
        Assert.Empty(_jellyfin.MediaUpdatedPaths);
        Assert.Empty(_jellyfin.RecentCalls);
    }

    [Fact]
    public async Task JI10_A_radio_dir_Jellyfin_has_not_scanned_is_not_reported_or_polled()
    {
        _jellyfin.PhysicalPaths = ["/music"];

        var result = await _ingest.IngestAsync("job-1", PathA);

        Assert.Equal(IngestOutcome.NotScanned, result.Outcome);
        Assert.Null(result.ItemId);
        Assert.False(result.IsReady);
        Assert.Empty(_jellyfin.MediaUpdatedPaths);
        Assert.Empty(_jellyfin.RecentCalls);
        var warn = Assert.Single(_log.Named("ingest.not_scanned"));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warn.Level);
    }

    [Theory]
    [InlineData("/music-radio", true)]
    [InlineData("/music-radio/", true)]
    [InlineData("/", true)]
    [InlineData("/music-radio2", false)]
    [InlineData("/music-radio/soulseek", false)]
    public async Task JI10_Scanned_means_the_radio_dir_or_a_parent_is_a_physical_path(string physical, bool scanned)
    {
        _jellyfin.PhysicalPaths = ["/movies", physical];

        var check = await _ingest.CheckLibraryAsync();

        Assert.True(check.InLibrary);
        Assert.Equal(scanned, check.Scanned);
    }

    [Fact]
    public async Task JI10_A_dir_in_no_library_is_not_asked_about_scanning()
    {
        _jellyfin.VirtualFolders = [];

        var check = await _ingest.CheckLibraryAsync();

        Assert.False(check.InLibrary);
        Assert.True(check.Scanned);
        Assert.Equal(0, _jellyfin.PhysicalPathsCalls);
    }

    [Fact]
    public async Task JI10_Physical_paths_that_cannot_be_read_do_not_block_the_ingest()
    {
        _jellyfin.PhysicalPathsFailure = new JellyfinRequestException("GetPhysicalPaths", 500);

        var check = await _ingest.CheckLibraryAsync();

        Assert.True(check.Scanned);
        Assert.Single(_log.Named("ingest.physical_paths_failed"));
    }

    [Fact]
    public async Task A_failed_media_updated_call_is_thrown_and_nothing_is_polled()
    {
        _jellyfin.MediaUpdatedFailure = new JellyfinRequestException("ReportMediaUpdated", 500);

        await Assert.ThrowsAsync<JellyfinRequestException>(() => _ingest.IngestAsync("job-1", PathA));

        Assert.Empty(_jellyfin.RecentCalls);
    }

    [Fact]
    public async Task Cancelling_the_wait_ends_that_ingest_and_the_loop_stops()
    {
        _jellyfin.IngestDelay = null;
        using var cts = new CancellationTokenSource();
        var task = _ingest.IngestAsync("job-1", PathA, cts.Token);
        await Wait.Until(() => _jellyfin.RecentCalls.Count >= 1, "first poll");

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await Tick(1); // the loop wakes, sees nobody waiting and stops
        await Task.Delay(100);
        Assert.Single(_jellyfin.RecentCalls);
    }

    // ---- The fake itself (JI11) ------------------------------------------------------------------

    [Fact]
    public async Task JI11_FakeJellyfin_records_paths_and_lists_items_only_after_the_scripted_delay()
    {
        _jellyfin.IngestDelay = TimeSpan.FromSeconds(30);

        await _jellyfin.ReportMediaUpdatedAsync([PathA]);
        var early = await _jellyfin.GetRecentAudioAsync(MusicLibrary, 50);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var late = await _jellyfin.GetRecentAudioAsync(MusicLibrary, 50);

        Assert.Equal([PathA], _jellyfin.MediaUpdatedPaths);
        Assert.Empty(early);
        Assert.Equal(PathA, Assert.Single(late).Path);
    }
}
