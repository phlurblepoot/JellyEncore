using Encore.Config;
using Encore.Fetch;
using Encore.Fetch.Import;
using Encore.Tests.Discovery;
using Encore.Tests.Watch;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Fetch;

/// <summary>FetchCoordinator on its own with a manual clock (v0.2 design section 15, T49: FW01, FW04, FW07, FW09, FW10).</summary>
public class FetchCoordinatorTests
{
    private readonly ManualClock _clock = new();
    private readonly FakeOwned _owned = new();
    private readonly ListLogger<FetchCoordinator> _log = new();

    private FetchCoordinator Create(FetchMode mode = FetchMode.Live)
    {
        var c = new FetchCoordinator(mode, _owned, _log, _clock);
        c.SetSlskdState(true, true, null);
        c.SetLibrary(true, "Music");
        c.SetFreeBytes(100_000_000_000);
        return c;
    }

    private FetchRequest Req(string pick, string title, string run = "run1", string artist = "Radiohead") =>
        WorkerHarness.RequestFor(pick, artist, title, run) with { Deadline = _clock.GetUtcNow() + FetchLimits.JobDeadline };

    [Fact]
    public void FW01_a_request_is_pending_queued_and_logged()
    {
        var c = Create();
        Assert.True(c.Request(Req("p1", "Reckoner")));

        Assert.Equal(new FetchReadiness.Pending(FetchStage.Queued), c.GetReadiness("p1"));
        var logged = Assert.Single(_log.Named("fetch.requested"));
        Assert.Equal("p1", logged.Fields["PickId"]);
        Assert.Equal("deezer_radio", logged.Fields["Origin"]);
        Assert.Equal(false, logged.Fields["Joined"]);
        Assert.Equal(1, c.Status.Queued);
    }

    [Fact]
    public void An_unknown_pick_reads_failed_unknown_and_a_repeated_pick_id_is_one_request()
    {
        var c = Create();
        Assert.Equal(new FetchReadiness.Failed("unknown"), c.GetReadiness("nope"));
        Assert.True(c.Request(Req("p1", "Reckoner")));
        Assert.True(c.Request(Req("p1", "Reckoner")));
        Assert.Single(_log.Named("fetch.requested"));
    }

    [Fact]
    public void Mode_off_null_object_refuses_everything()
    {
        var n = NullFetchRequests.Instance;
        Assert.Equal(new FetchAvailability(false, "off"), n.Availability);
        Assert.False(n.Request(Req("p1", "Reckoner")));
        Assert.Equal(new FetchReadiness.Failed("off"), n.GetReadiness("p1"));
        Assert.Equal(FetchMode.Off, n.Status.Mode);
    }

    [Fact]
    public void FW04_the_31st_job_in_an_hour_is_refused_and_accepted_again_an_hour_later()
    {
        var c = Create();
        c.SeedWindow(Enumerable.Range(0, 29).Select(i => _clock.GetUtcNow() - TimeSpan.FromMinutes(50)));
        Assert.True(c.Request(Req("p30", "Song 30")));   // 29 counted + 1 reserved = 30

        Assert.False(c.Request(Req("p31", "Song 31")));
        Assert.Equal(new FetchAvailability(false, "hourly_cap"), c.Availability);
        Assert.Equal(new FetchReadiness.Failed("unknown"), c.GetReadiness("p31"));

        // A join adds no download, so it still works at the cap.
        Assert.True(c.Request(Req("p30b", "Song 30", run: "run2")));

        _clock.Advance(TimeSpan.FromMinutes(11));   // the 29 seeded downloads leave the window
        Assert.True(c.Request(Req("p31", "Song 31")));
        Assert.Equal(2, c.Status.Queued);
    }

    [Fact]
    public void FW04_a_job_that_ends_before_downloading_gives_its_place_back()
    {
        var c = Create();
        c.SeedWindow(Enumerable.Range(0, 29).Select(_ => _clock.GetUtcNow()));
        Assert.True(c.Request(Req("p1", "Song 1")));
        Assert.False(c.Request(Req("p2", "Song 2")));

        c.Abandon("p1", "skipped");   // queued: cancelled at once
        Assert.True(c.Request(Req("p2", "Song 2")));
    }

    [Fact]
    public void FW07_abandon_of_a_queued_job_cancels_it_but_a_joined_job_stays_while_another_pick_holds_it()
    {
        var c = Create();
        Assert.True(c.Request(Req("a1", "Reckoner", run: "runA")));
        Assert.True(c.Request(Req("b1", "Reckoner", run: "runB")));
        Assert.Contains(_log.Named("fetch.requested"), e => Equals(e.Fields["Joined"], true));

        c.Abandon("a1", "skipped");
        Assert.Equal(new FetchReadiness.Pending(FetchStage.Queued), c.GetReadiness("b1"));

        c.Abandon("b1", "skipped");
        Assert.Equal(new FetchReadiness.Failed("cancelled"), c.GetReadiness("b1"));
        Assert.Equal(new FetchReadiness.Failed("cancelled"), c.GetReadiness("a1"));
        Assert.Equal("abandoned", Assert.Single(_log.Named("fetch.cancelled")).Fields["Reason"]);
        Assert.Equal(1, c.Status.Cancelled);
    }

    [Fact]
    public void FW06_end_run_cancels_queued_jobs_held_only_by_that_run()
    {
        var c = Create();
        Assert.True(c.Request(Req("a1", "Song 1", run: "runA")));
        Assert.True(c.Request(Req("a2", "Song 2", run: "runA")));
        Assert.True(c.Request(Req("b2", "Song 2", run: "runB")));   // joins a2's job

        c.EndRun("runA", "replaced");

        Assert.Equal(new FetchReadiness.Failed("cancelled"), c.GetReadiness("a1"));
        Assert.Equal(new FetchReadiness.Pending(FetchStage.Queued), c.GetReadiness("b2"));
        Assert.Equal("run_end", Assert.Single(_log.Named("fetch.cancelled")).Fields["Reason"]);
        var ended = Assert.Single(_log.Named("run.ended"));
        Assert.Equal(1, ended.Fields["CancelledJobs"]);
        Assert.Equal("replaced", ended.Fields["Reason"]);
    }

    [Fact]
    public void FW09_a_queued_job_past_15_minutes_fails_with_deadline()
    {
        var c = Create();
        Assert.True(c.Request(Req("p1", "Reckoner")));
        _clock.Advance(FetchLimits.JobDeadline - TimeSpan.FromSeconds(1));
        c.Sweep();
        Assert.Equal(new FetchReadiness.Pending(FetchStage.Queued), c.GetReadiness("p1"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        c.Sweep();
        Assert.Equal(new FetchReadiness.Failed("deadline"), c.GetReadiness("p1"));
        Assert.Equal("deadline", Assert.Single(_log.Named("fetch.cancelled")).Fields["Reason"]);
        Assert.Equal("deadline", Assert.Single(_log.Named("fetch.failed")).Fields["Reason"]);
        Assert.Equal(1, c.Status.Failed);
    }

    [Fact]
    public void FW10_availability_reasons_refuse_requests_and_log_once_per_change()
    {
        var c = Create();
        Assert.True(c.Availability.Available);
        _log.Entries.Clear();

        c.SetSlskdState(false, false, "timeout");
        Assert.False(c.Request(Req("p1", "Song 1")));
        Assert.Equal("slskd_unreachable", c.Availability.Reason);
        Assert.Equal("slskd_unreachable", c.Status.UnavailableReason);

        c.SetSlskdState(true, false, null);
        Assert.Equal("not_logged_in", c.Availability.Reason);
        c.SetSlskdState(true, false, null);   // no change: no new log line

        c.SetFreeBytes(FetchLimits.FreeSpaceFloorBytes - 1);
        Assert.Equal("not_logged_in", c.Availability.Reason);
        c.SetSlskdState(true, true, null);
        Assert.Equal("disk_low", c.Availability.Reason);
        Assert.False(c.Request(Req("p1", "Song 1")));

        c.SetLibrary(false, null);
        Assert.Equal("radio_dir_not_in_library", c.Availability.Reason);
        c.SetFreeBytes(FetchLimits.FreeSpaceFloorBytes);

        _owned.IsLoaded = false;
        c.SetLibrary(true, "Music");
        Assert.Equal("index_not_ready", c.Availability.Reason);
        _owned.IsLoaded = true;
        Assert.True(c.Availability.Available);
        Assert.True(c.Request(Req("p1", "Song 1")));

        var unavailable = _log.Named("fetch.unavailable").Select(e => (string)e.Fields["Reason"]!).ToList();
        Assert.Equal(["slskd_unreachable", "not_logged_in", "disk_low", "radio_dir_not_in_library", "index_not_ready"], unavailable);
        Assert.Single(_log.Named("fetch.available"));
        Assert.All(_log.Named("fetch.unavailable"), e => Assert.Equal(LogLevel.Warning, e.Level));
    }

    [Fact]
    public void FW10_dry_run_ignores_the_library_and_disk_checks()
    {
        var c = Create(FetchMode.DryRun);
        c.SetLibrary(false, null);
        c.SetFreeBytes(1);
        Assert.True(c.Availability.Available);
    }

    [Fact]
    public async Task FW13_readiness_reads_from_many_threads_never_throw_while_requests_and_cancels_run()
    {
        var c = Create();
        using var stop = new CancellationTokenSource();
        var errors = 0;
        var readers = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    for (var i = 0; i < 40; i++)
                    {
                        _ = c.GetReadiness($"p{i}");
                    }

                    _ = c.Status;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        })).ToArray();

        for (var i = 0; i < 40; i++)
        {
            c.Request(Req($"p{i}", $"Song {i % 10}", run: $"run{i % 3}"));
            if (i % 7 == 0)
            {
                c.Abandon($"p{i / 2}", "skipped");
            }
        }

        c.EndRun("run1", "replaced");
        stop.Cancel();
        await Task.WhenAll(readers);
        Assert.Equal(0, errors);
    }

    [Fact]
    public void There_is_one_free_space_floor_of_20_decimal_gigabytes()
    {
        Assert.Equal(20_000_000_000, FetchLimits.FreeSpaceFloorBytes);
        Assert.Equal(FetchLimits.FreeSpaceFloorBytes, ImporterSettings.DefaultFreeSpaceFloor);
    }
}
