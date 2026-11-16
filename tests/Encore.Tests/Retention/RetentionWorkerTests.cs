using Encore.Config;
using Encore.Retention;
using Encore.Store;
using Encore.Tests.Watch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Encore.Tests.Retention;

public sealed class RetentionWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);

    private static EncoreOptions Options(RetentionMode mode, int maxPromotions = 7) => new()
    {
        JellyfinUrl = new Uri("http://jellyfin.test:8096"),
        JellyfinApiKey = "test-key-not-real",
        AllowedUsers = ["ann"],
        DataDir = Path.GetTempPath(),
        RetentionMode = mode,
        RetentionMaxPromotions = maxPromotions,
        RetentionHour = 4,
    };

    private static TrackedFile Row(long id) => new(
        id, "k" + id, "A", "T" + id, "ready", "/radio/" + id, null, Guid.NewGuid().ToString("N"), false, null, null, null, null, null, 0, 0, Now, "u", null, null);

    private static PlannedRow Planned(long id, string decision) => new(Row(id), decision, null, null, 1, 0, 0, null, null, null, null);

    private static PassResult Pass(RetentionMode mode, SignalsSnapshot? signals = null, params (long Id, string Decision)[] rows) => new(
        "p1", Now, mode, new PlannerSettings(30, 3, 3), rows.Select(r => Planned(r.Id, r.Decision)).ToList(), [],
        signals ?? new SignalsSnapshot(Now, 1, new HashSet<Guid>(), [], false, true, []), 1);

    private sealed class Steps
    {
        public int Passes;
        public int Deletes;
        public List<(IReadOnlyList<long> Ids, int Max)> PromoteCalls { get; } = [];
        public PassResult Result { get; set; } = Pass(RetentionMode.Live);
        public Exception? PassThrows { get; set; }
        public Exception? PromoteThrows { get; set; }

        public RetentionSteps Build() => new(
            ct =>
            {
                Interlocked.Increment(ref Passes);
                return PassThrows is null ? Task.FromResult(Result) : Task.FromException<PassResult>(PassThrows);
            },
            (p, ct) =>
            {
                Interlocked.Increment(ref Deletes);
                return Task.FromResult(DeleteResult.None);
            },
            (ids, signals, max, ct) =>
            {
                PromoteCalls.Add((ids, max));
                return PromoteThrows is null
                    ? Task.FromResult<IReadOnlyList<PromotionResult>>(ids.Select(i => new PromotionResult(i, PromotionOutcome.Done)).ToList())
                    : Task.FromException<IReadOnlyList<PromotionResult>>(PromoteThrows);
            });
    }

    private static (RetentionWorker Worker, ListLogger<RetentionWorker> Log) Make(Steps steps, RetentionMode mode, RetentionTiming? timing = null, int maxPromotions = 7)
    {
        var log = new ListLogger<RetentionWorker>();
        return (new RetentionWorker(steps.Build(), Options(mode, maxPromotions), TimeProvider.System, log, timing), log);
    }

    [Fact]
    public async Task RW01_Mode_off_does_nothing()
    {
        var steps = new Steps();
        var (worker, _) = Make(steps, RetentionMode.Off);
        var run = await worker.RunOnceAsync();
        Assert.Null(run.Pass);
        Assert.Equal(0, steps.Passes);
    }

    [Fact]
    public async Task RW02_Dry_run_decides_but_never_promotes_or_deletes()
    {
        var steps = new Steps { Result = Pass(RetentionMode.DryRun, null, (1, RetentionDecision.Promote), (2, RetentionDecision.Delete)) };
        var (worker, _) = Make(steps, RetentionMode.DryRun);
        var run = await worker.RunOnceAsync();
        Assert.Equal(1, steps.Passes);
        Assert.Empty(steps.PromoteCalls);
        Assert.Equal(0, steps.Deletes);
        Assert.NotNull(run.Pass);
    }

    [Fact]
    public async Task RW03_Live_promotes_only_promote_rows_with_the_cap_then_deletes()
    {
        var steps = new Steps { Result = Pass(RetentionMode.Live, null, (1, RetentionDecision.Promote), (2, RetentionDecision.Keep), (3, RetentionDecision.Promote), (4, RetentionDecision.Delete)) };
        var (worker, _) = Make(steps, RetentionMode.Live, maxPromotions: 5);
        var run = await worker.RunOnceAsync();
        var call = Assert.Single(steps.PromoteCalls);
        Assert.Equal(new long[] { 1, 3 }, call.Ids);
        Assert.Equal(5, call.Max);
        Assert.Equal(1, steps.Deletes);
        Assert.False(run.DeletesSkipped);
        Assert.Equal(2, run.Promotions.Count);
    }

    [Fact]
    public async Task RW04_Live_with_nothing_to_promote_skips_the_promoter()
    {
        var steps = new Steps { Result = Pass(RetentionMode.Live, null, (1, RetentionDecision.Keep)) };
        var (worker, _) = Make(steps, RetentionMode.Live);
        await worker.RunOnceAsync();
        Assert.Empty(steps.PromoteCalls);
        Assert.Equal(1, steps.Deletes);
    }

    [Fact]
    public async Task RW05_A_signal_read_failure_skips_the_deletes_of_the_pass()
    {
        var failed = SignalsSnapshot.Failed(Now, new SignalFailure("favorites", 500, "boom"));
        var steps = new Steps { Result = Pass(RetentionMode.Live, failed, (1, RetentionDecision.Delete)) };
        var (worker, _) = Make(steps, RetentionMode.Live);
        var run = await worker.RunOnceAsync();
        Assert.True(run.DeletesSkipped);
        Assert.Equal(0, steps.Deletes);
        Assert.False(run.Crashed);
    }

    [Fact]
    public async Task RW06_A_crash_is_logged_as_8010_with_component_retention_and_does_not_escape()
    {
        var steps = new Steps { PassThrows = new InvalidOperationException("kaput") };
        var (worker, log) = Make(steps, RetentionMode.Live);
        var run = await worker.RunOnceAsync();
        Assert.True(run.Crashed);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(8010, entry.EventId.Id);
        Assert.Contains("retention", entry.Message);
        Assert.Contains("kaput", entry.Message);
    }

    [Fact]
    public async Task RW07_A_crash_in_the_promoter_is_logged_and_skips_the_deletes_of_that_run()
    {
        var steps = new Steps { Result = Pass(RetentionMode.Live, null, (1, RetentionDecision.Promote)), PromoteThrows = new IOException("disk") };
        var (worker, log) = Make(steps, RetentionMode.Live);
        var run = await worker.RunOnceAsync();
        Assert.True(run.Crashed);
        Assert.Equal(0, steps.Deletes);
        Assert.Equal(8010, Assert.Single(log.Entries).EventId.Id);
    }

    [Fact]
    public async Task RW08_Runs_never_overlap()
    {
        var gate = new TaskCompletionSource();
        var running = 0;
        var peak = 0;
        var steps = new RetentionSteps(
            async ct =>
            {
                var now = Interlocked.Increment(ref running);
                peak = Math.Max(peak, now);
                await gate.Task;
                Interlocked.Decrement(ref running);
                return Pass(RetentionMode.DryRun);
            },
            (p, ct) => Task.FromResult(DeleteResult.None),
            (ids, s, m, ct) => Task.FromResult<IReadOnlyList<PromotionResult>>([]));
        var worker = new RetentionWorker(steps, Options(RetentionMode.DryRun), TimeProvider.System, new ListLogger<RetentionWorker>());
        var a = worker.RunOnceAsync();
        var b = worker.RunOnceAsync();
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task RW09_The_hosted_loop_runs_a_pass_after_the_startup_delay()
    {
        var steps = new Steps { Result = Pass(RetentionMode.DryRun) };
        var (worker, _) = Make(steps, RetentionMode.DryRun, new RetentionTiming { StartupDelay = TimeSpan.FromMilliseconds(30) });
        await worker.StartAsync(CancellationToken.None);
        await Wait.Until(() => steps.Passes >= 1, "first pass");
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, steps.Passes);
    }

    [Fact]
    public async Task RW10_No_pass_runs_before_the_startup_delay()
    {
        var steps = new Steps { Result = Pass(RetentionMode.DryRun) };
        var (worker, _) = Make(steps, RetentionMode.DryRun);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, steps.Passes);
    }

    [Fact]
    public void RW11_Default_startup_delay_is_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), new RetentionTiming().StartupDelay);
    }

    [Theory]
    [InlineData("2026-11-20T03:00:00+00:00", 4, 1)]
    [InlineData("2026-11-20T04:00:00+00:00", 4, 24)]
    [InlineData("2026-11-20T05:30:00+00:00", 4, 22.5)]
    [InlineData("2026-11-20T23:30:00+00:00", 0, 0.5)]
    public void RW12_Next_run_is_the_next_occurrence_of_the_hour(string now, int hour, double expectedHours)
    {
        var delay = RetentionWorker.DelayUntilNext(DateTimeOffset.Parse(now), hour, TimeZoneInfo.Utc);
        Assert.Equal(expectedHours, delay.TotalHours, 4);
    }

    [Fact]
    public void RW13_Next_run_uses_the_local_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(-5), "t", "t");
        // 04:00 local is 09:00 UTC.
        var delay = RetentionWorker.DelayUntilNext(DateTimeOffset.Parse("2026-11-20T08:00:00+00:00"), 4, zone);
        Assert.Equal(1, delay.TotalHours, 4);
    }

    [Fact]
    public void RW14_Mode_off_registers_nothing()
    {
        var services = new ServiceCollection();
        var before = services.Count;
        services.AddRetention(Options(RetentionMode.Off), null!);
        Assert.Equal(before, services.Count);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void RW15_Dry_run_registers_the_hosted_workers_and_one_play_tally()
    {
        var dir = Path.Combine(Path.GetTempPath(), "encore-rw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var main = EncoreStore.Open(Path.Combine(dir, "encore.db"), TimeProvider.System);
            using var retention = RetentionStore.Open(main.Path);
            var services = new ServiceCollection();
            services.AddRetention(Options(RetentionMode.DryRun), retention);
            Assert.Equal(4, services.Count(d => d.ServiceType == typeof(IHostedService)));
            Assert.Single(services, d => d.ServiceType == typeof(PlayTally));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
