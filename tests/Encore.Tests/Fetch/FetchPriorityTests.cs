using Encore.Config;
using Encore.Fetch;
using Encore.Fetch.Scoring;
using Encore.Slskd;
using Encore.Tests.Discovery;
using Encore.Tests.Watch;

namespace Encore.Tests.Fetch;

/// <summary>
/// Fetch-side request priority (v0.3 design section 6.2, T86): the two-level queue, the request slot, the hourly cap wait,
/// join upgrades, the negative cache, request pick readiness, and the scorer's FlacFirst ranking.
/// </summary>
public class FetchPriorityTests
{
    private readonly ManualClock _clock = new();
    private readonly FakeOwned _owned = new();
    private readonly ListLogger<FetchCoordinator> _log = new();

    private static bool IsReady(FetchReadiness r) => r is FetchReadiness.Ready;

    private FetchCoordinator Create()
    {
        var c = new FetchCoordinator(FetchMode.Live, _owned, _log, _clock);
        c.SetSlskdState(true, true, null);
        c.SetLibrary(true, "Music");
        c.SetFreeBytes(100_000_000_000);
        return c;
    }

    private FetchRequest Radio(string pick, string title, string run = "run1") =>
        WorkerHarness.RequestFor(pick, "Radiohead", title, run) with { Deadline = _clock.GetUtcNow() + FetchLimits.JobDeadline };

    private FetchRequest Song(string pick, string title) =>
        AsRequest(WorkerHarness.RequestFor(pick, "Radiohead", title, "request"), _clock.GetUtcNow());

    private static FetchRequest AsRequest(FetchRequest r, DateTimeOffset now) =>
        r with { RunId = "request", SessionId = "", Deadline = now + FetchLimits.RequestDeadline, Priority = FetchPriority.Request, Preference = FetchPreference.FlacFirst };

    private static readonly FetchReadiness Queued = new FetchReadiness.Pending(FetchStage.Queued);
    private static readonly FetchReadiness WaitingForCap = new FetchReadiness.Pending(FetchStage.Queued) { Waiting = FetchWaiting.HourlyCap };

    // ================================================================ coordinator (manual clock)

    [Fact]
    public void FP01_v02_callers_get_radio_priority_and_the_fastest_copy_by_default()
    {
        var r = new FetchRequest("p", "run", "s", Guid.Empty, WorkerHarness.Candidate("A", "B"), false, DateTimeOffset.UnixEpoch);
        Assert.Equal(FetchPriority.Radio, r.Priority);
        Assert.Equal(FetchPreference.Fastest, r.Preference);
        Assert.Equal(TimeSpan.FromMinutes(60), FetchLimits.RequestDeadline);
        Assert.Equal(1, FetchLimits.RequestSlots);
    }

    [Fact]
    public void FP02_at_the_hourly_cap_a_request_waits_queued_and_a_radio_request_is_refused()
    {
        var c = Create();
        c.SeedWindow(Enumerable.Range(0, FetchLimits.FetchesPerHour).Select(_ => _clock.GetUtcNow() - TimeSpan.FromMinutes(50)));
        Assert.False(c.Request(Radio("p1", "Song 1")));

        Assert.True(c.Request(Song("req:1", "Reckoner")));
        Assert.Equal(WaitingForCap, c.GetReadiness("req:1"));
        Assert.Equal(new FetchAvailability(false, "hourly_cap"), c.Availability);
        Assert.False(c.Request(Radio("p2", "Song 2")));

        _clock.Advance(TimeSpan.FromMinutes(11));   // the window frees: the request is no longer waiting for the cap
        c.Sweep();
        Assert.Equal(Queued, c.GetReadiness("req:1"));
        Assert.Equal(1, c.Status.Queued);
    }

    [Fact]
    public void FP03_a_queued_request_takes_the_last_place_so_a_radio_request_is_refused()
    {
        var c = Create();
        c.SeedWindow(Enumerable.Range(0, FetchLimits.FetchesPerHour - 1).Select(_ => _clock.GetUtcNow()));
        Assert.True(c.Request(Song("req:1", "Reckoner")));
        Assert.Equal(Queued, c.GetReadiness("req:1"));   // there is room: not waiting
        Assert.False(c.Request(Radio("p1", "Song 1")));
        Assert.Equal(new FetchAvailability(false, "hourly_cap"), c.Availability);
    }

    [Fact]
    public void FP04_a_request_is_still_refused_for_any_reason_other_than_the_hourly_cap()
    {
        var c = Create();
        c.SetSlskdState(false, false, "down");
        Assert.False(c.Request(Song("req:1", "Reckoner")));
        _owned.IsLoaded = false;
        c.SetSlskdState(true, true, null);
        Assert.False(c.Request(Song("req:1", "Reckoner")));
        _owned.IsLoaded = true;
        Assert.True(c.Request(Song("req:1", "Reckoner")));
    }

    [Fact]
    public void FP05_a_request_that_joins_a_radio_job_keeps_it_alive_when_the_run_ends_or_the_radio_pick_is_skipped()
    {
        var c = Create();
        Assert.True(c.Request(Radio("a1", "Reckoner", run: "runA")));
        Assert.True(c.Request(Radio("a2", "Nude", run: "runA")));
        Assert.True(c.Request(Song("req:1", "Reckoner")));

        c.EndRun("runA", "replaced");
        Assert.Equal(Queued, c.GetReadiness("req:1"));
        Assert.Equal(new FetchReadiness.Failed("cancelled"), c.GetReadiness("a2"));
        Assert.Equal(1, Assert.Single(_log.Named("run.ended")).Fields["CancelledJobs"]);

        Assert.True(c.Request(Radio("b1", "Reckoner", run: "runB")));
        c.Abandon("b1", "skipped");
        Assert.Equal(Queued, c.GetReadiness("req:1"));
    }

    [Fact]
    public void FP06_request_readiness_is_kept_past_an_hour_until_forgotten_and_radio_readiness_is_not()
    {
        var c = Create();
        Assert.True(c.Request(Radio("p1", "Song 1")));
        Assert.True(c.Request(Song("req:1", "Reckoner")));

        _clock.Advance(FetchLimits.RequestDeadline);
        c.Sweep();   // both fail with deadline
        Assert.Equal(new FetchReadiness.Failed("deadline"), c.GetReadiness("p1"));
        Assert.Equal(new FetchReadiness.Failed("deadline"), c.GetReadiness("req:1"));

        _clock.Advance(TimeSpan.FromHours(2));
        c.Sweep();
        Assert.Equal(new FetchReadiness.Failed("unknown"), c.GetReadiness("p1"));
        Assert.Equal(new FetchReadiness.Failed("deadline"), c.GetReadiness("req:1"));

        c.Forget("req:1");
        Assert.Equal(new FetchReadiness.Failed("unknown"), c.GetReadiness("req:1"));
    }

    [Fact]
    public void FP07_forgetting_a_pending_request_pick_abandons_it()
    {
        var c = Create();
        Assert.True(c.Request(Song("req:1", "Reckoner")));
        c.Forget("req:1");
        Assert.Equal(new FetchReadiness.Failed("unknown"), c.GetReadiness("req:1"));
        Assert.Equal(1, c.Status.Cancelled);
        Assert.Equal(0, c.Status.Queued);
        c.Forget("nope");   // unknown ids are ignored
    }

    [Fact]
    public void FP08_a_request_pick_is_forgotten_by_the_backstop_after_a_day()
    {
        var c = Create();
        Assert.True(c.Request(Song("req:1", "Reckoner")));
        _clock.Advance(FetchLimits.RequestDeadline);
        c.Sweep();
        _clock.Advance(FetchLimits.RequestPicksBackstop + TimeSpan.FromSeconds(1));
        c.Sweep();
        Assert.Equal(new FetchReadiness.Failed("unknown"), c.GetReadiness("req:1"));
    }

    [Fact]
    public void FP09_mode_off_forget_does_nothing()
    {
        NullFetchRequests.Instance.Forget("req:1");
        Assert.Equal(new FetchReadiness.Failed("off"), NullFetchRequests.Instance.GetReadiness("req:1"));
    }

    // ================================================================ worker (real clock)

    private static Task<SoulseekFetchResult> Held(FetchCall call, Task gate) =>
        FakeFetcher.DownloadAsync(call, downloading: gate);

    private static bool Request(WorkerHarness h, string pickId, string title) =>
        h.Coordinator.Request(AsRequest(WorkerHarness.RequestFor(pickId, "Artist", title), DateTimeOffset.UtcNow));

    [Fact]
    public async Task FP10_a_request_job_uses_the_request_slot_when_the_radio_slots_are_full()
    {
        await using var h = new WorkerHarness(concurrency: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fetcher.Behavior = c => Held(c, gate.Task);
        await h.StartAsync();

        Assert.True(h.Request("a", "Artist", "Radio A"));
        await Wait.Until(() => h.Fetcher.CallFor("Radio A") is not null, "radio A started");
        Assert.True(h.Request("b", "Artist", "Radio B"));
        Assert.True(Request(h, "req:1", "Request 1"));

        await Wait.Until(() => h.Fetcher.CallFor("Request 1") is not null, "the request started beside radio A");
        await Task.Delay(200);
        Assert.Null(h.Fetcher.CallFor("Radio B"));   // radio still has one slot only
        Assert.Equal(new FetchReadiness.Pending(FetchStage.Queued), h.Readiness("b"));

        gate.SetResult();
        foreach (var p in new[] { "a", "b", "req:1" })
        {
            await h.Until(p, IsReady, $"{p} ready");
        }
    }

    [Fact]
    public async Task FP11_a_queued_request_job_is_dequeued_before_an_older_radio_job()
    {
        await using var h = new WorkerHarness(concurrency: 1);
        var gates = new Dictionary<string, TaskCompletionSource>();
        foreach (var t in new[] { "Radio A", "Request 1", "Radio B", "Request 2" })
        {
            gates[t] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        h.Fetcher.Behavior = c => Held(c, gates[c.Job.Title].Task);
        await h.StartAsync();

        Assert.True(h.Request("a", "Artist", "Radio A"));
        await Wait.Until(() => h.Fetcher.CallFor("Radio A") is not null, "radio A started");
        Assert.True(Request(h, "req:1", "Request 1"));   // takes the request slot
        await Wait.Until(() => h.Fetcher.CallFor("Request 1") is not null, "request 1 started");
        Assert.True(h.Request("b", "Artist", "Radio B"));
        Assert.True(Request(h, "req:2", "Request 2"));
        await Task.Delay(100);
        Assert.Equal(2, h.Fetcher.Calls.Count);

        gates["Radio A"].SetResult();   // frees the radio slot: the newer request goes first
        await Wait.Until(() => h.Fetcher.Calls.Count == 3, "a third job started");
        Assert.Equal("Request 2", h.Fetcher.Calls.ElementAt(2).Job.Title);

        gates["Request 2"].SetResult();
        await Wait.Until(() => h.Fetcher.Calls.Count == 4, "radio B started");
        Assert.Equal("Radio B", h.Fetcher.Calls.ElementAt(3).Job.Title);
        gates["Request 1"].SetResult();
        gates["Radio B"].SetResult();
        foreach (var p in new[] { "a", "b", "req:1", "req:2" })
        {
            await h.Until(p, IsReady, $"{p} ready");
        }
    }

    [Fact]
    public async Task FP12_a_request_joining_a_queued_radio_job_moves_it_ahead_and_sets_flac_first()
    {
        await using var h = new WorkerHarness(concurrency: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fetcher.Behavior = c => Held(c, c.Job.Title == "Radio A" ? gate.Task : block.Task);
        await h.StartAsync();

        Assert.True(h.Request("a", "Artist", "Radio A"));
        await Wait.Until(() => h.Fetcher.CallFor("Radio A") is not null, "radio A started");
        Assert.True(Request(h, "req:1", "Request 1"));   // keeps the request slot busy
        await Wait.Until(() => h.Fetcher.CallFor("Request 1") is not null, "request 1 started");
        Assert.True(h.Request("b", "Artist", "Radio B"));
        Assert.True(h.Request("c", "Artist", "Radio C"));
        Assert.True(Request(h, "req:c", "Radio C"));      // joins c's queued job
        Assert.Contains(h.CoordinatorLog.Named("fetch.requested"), e => Equals(e.Fields["PickId"], "req:c") && Equals(e.Fields["Joined"], true));

        gate.SetResult();   // frees the radio slot only (request 1 holds the request slot)
        await Wait.Until(() => h.Fetcher.Calls.Count >= 3, "a third job started");
        var third = h.Fetcher.Calls.ElementAt(2).Job;
        Assert.Equal("Radio C", third.Title);
        await Task.Delay(100);
        Assert.Equal(3, h.Fetcher.Calls.Count);
        Assert.Equal(new FetchReadiness.Pending(FetchStage.Queued), h.Readiness("b"));
        Assert.Equal(FetchPreference.FlacFirst, third.Preference);
        Assert.Equal(FetchPreference.FlacFirst, h.Fetcher.CallFor("Request 1")!.Job.Preference);
        Assert.Equal(FetchPreference.Fastest, h.Fetcher.CallFor("Radio A")!.Job.Preference);
        block.SetResult();
        await h.Until("req:c", IsReady, "req:c ready");
        await h.Until("b", IsReady, "b ready");
    }

    [Fact]
    public async Task FP13_a_request_joining_a_searching_radio_job_does_not_change_its_preference()
    {
        await using var h = new WorkerHarness(concurrency: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fetcher.Behavior = async c => await FakeFetcher.SearchAsync(c, gate.Task) ?? await FakeFetcher.DownloadAsync(c);
        await h.StartAsync();

        Assert.True(h.Request("a", "Artist", "Radio A", runId: "runA"));
        await h.Until("a", r => r == new FetchReadiness.Pending(FetchStage.Searching), "a searching");
        Assert.True(Request(h, "req:a", "Radio A"));
        h.Coordinator.EndRun("runA", "replaced");   // the request still holds the job: not cancelled
        gate.SetResult();

        await h.Until("req:a", IsReady, "req:a ready");
        var call = Assert.Single(h.Fetcher.Calls);
        Assert.Equal(FetchPreference.Fastest, call.Job.Preference);
        Assert.Equal(0, h.Coordinator.Status.Cancelled);
    }

    [Fact]
    public async Task FP14_at_the_hourly_cap_a_request_waits_and_starts_when_the_window_frees()
    {
        await using var h = new WorkerHarness();
        await h.StartAsync();
        var leaves = DateTimeOffset.UtcNow - TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1.5);
        h.Coordinator.SeedWindow(Enumerable.Range(0, FetchLimits.FetchesPerHour).Select(_ => leaves));

        Assert.False(h.Request("p1", "Artist", "Radio 1"));
        Assert.True(Request(h, "req:1", "Request 1"));
        Assert.Equal(WaitingForCap, h.Readiness("req:1"));
        await Task.Delay(300);
        Assert.Empty(h.Fetcher.Calls);

        await h.Until("req:1", IsReady, "req:1 ready once the window freed");
        Assert.Equal("Request 1", Assert.Single(h.Fetcher.Calls).Job.Title);
    }

    [Fact]
    public async Task FP15_a_request_job_ignores_the_negative_cache()
    {
        await using var h = new WorkerHarness();
        h.Fetcher.Behavior = _ => Task.FromResult<SoulseekFetchResult>(new SoulseekFetchResult.Failed(FetchFailure.NoResults));
        await h.StartAsync();
        var key = WorkerHarness.Candidate("Artist", "Missing").Key.Value;
        h.Store.UpsertNegative(key, DateTimeOffset.UtcNow + FetchLimits.NegativeCache, "no_results");
        Assert.True(h.Store.IsNegative(key, DateTimeOffset.UtcNow));

        Assert.True(Request(h, "req:1", "Missing"));
        await h.Until("req:1", r => r is FetchReadiness.Failed, "req:1 searched and failed");
        Assert.Equal(new FetchReadiness.Failed("no_results"), h.Readiness("req:1"));
        Assert.Single(h.Fetcher.Calls);
    }

    // ================================================================ scorer FlacFirst

    private const string Dir = @"@@a\Music\Radiohead\In Rainbows\";
    private static readonly Scorer Subject = new();

    /// <summary>
    /// A peer spec "name:format:queue": queue -1 is a free slot (ETA = transfer only), else no free slot with that queue
    /// (ETA = 60 + 30 x queue + transfer). At 1 MB/s a FLAC transfers in 32 s and an MP3 in 11.6 s.
    /// </summary>
    private static SlskdSearchResponse PeerOf(string spec)
    {
        var parts = spec.Split(':');
        var queue = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        var file = parts[1] == "flac"
            ? new SlskdFile(Dir + "07 Reckoner.flac", 32_000_000, null, null, 290, null, null, "flac")
            : new SlskdFile(Dir + "07 Reckoner.mp3", 11_600_000, 320, false, 291, null, null, "mp3");
        return new SlskdSearchResponse(parts[0], queue < 0, 1_000_000, Math.Max(queue, 0), [file]);
    }

    public static IEnumerable<object?[]> FlacFirstCases()
    {
        // preference, seconds left, peers, expected order
        yield return [FetchPreference.FlacFirst, null, "flac:flac:-1,mp3:mp3:-1", "flac,mp3"];                 // fast FLAC (32 s) beats a faster MP3 (11.6 s)
        yield return [FetchPreference.Fastest, null, "flac:flac:-1,mp3:mp3:-1", "mp3,flac"];                   // v0.2: by ETA
        yield return [FetchPreference.FlacFirst, null, "flac:flac:40,mp3:mp3:20", "mp3,flac"];                // FLAC 1292 s > 20 min: after the MP3 (672 s)
        yield return [FetchPreference.FlacFirst, null, "flac:flac:36,mp3:mp3:-1", "flac,mp3"];                // FLAC 1172 s is within 20 min
        yield return [FetchPreference.FlacFirst, 300.0, "flac:flac:10,mp3:mp3:-1", "mp3,flac"];               // FLAC 392 s past a 300 s deadline
        yield return [FetchPreference.FlacFirst, 900.0, "flac:flac:10,mp3:mp3:-1", "flac,mp3"];               // FLAC 392 s within a 900 s deadline
        yield return [FetchPreference.FlacFirst, null, "slow:flac:10,fast:flac:-1,mp3:mp3:-1", "fast,slow,mp3"]; // FLACs by ETA, then MP3
        yield return [FetchPreference.FlacFirst, null, "late:mp3:20,soon:mp3:-1", "soon,late"];               // MP3s only: by ETA
        yield return [FetchPreference.FlacFirst, null, "slow:flac:40,fast:flac:-1,mp3:mp3:-1", "fast,mp3,slow"]; // a too-slow FLAC goes last
        yield return [FetchPreference.Fastest, 10.0, "flac:flac:40,mp3:mp3:20", "mp3,flac"];                  // Fastest ignores the deadline rule
        yield return [FetchPreference.FlacFirst, -5.0, "flac:flac:-1,mp3:mp3:-1", "mp3,flac"];                // already past the deadline
        yield return [FetchPreference.FlacFirst, null, "a:flac:40,b:flac:42,c:mp3:30", "c,a,b"];              // two slow FLACs after the MP3, by ETA
    }

    [Theory]
    [MemberData(nameof(FlacFirstCases))]
    public void FP16_flac_first_ranking_table(FetchPreference preference, double? secondsLeft, string peers, string expected)
    {
        var reference = new ScoreReference("Radiohead", "Reckoner", "In Rainbows", 290, preference, secondsLeft);
        var result = Subject.Score(reference, peers.Split(',').Select(PeerOf).ToList());
        Assert.Equal(0, result.RejectedTotal);
        Assert.Equal(expected, string.Join(",", result.Ranked.Select(f => f.Username)));
    }
}
