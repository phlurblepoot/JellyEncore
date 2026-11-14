using Encore.Config;
using Encore.Slskd;
using Encore.Tests.Trigger;

namespace Encore.Tests.Slskd;

public class SearchBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_35th_search_in_220_seconds_is_blocked()
    {
        var time = new ManualTimeProvider(Start);
        var budget = new SearchBudget(new SearchLimit(34, 220), time);

        // 34 searches, 5 s apart, fill the budget: the last one is at 165 s.
        for (var i = 0; i < 34; i++)
        {
            Assert.True(await budget.TryAcquireAsync(time.GetUtcNow(), default), $"search {i + 1}");
            time.Advance(TimeSpan.FromSeconds(5));
        }

        // Now at 170 s. The 35th would have to wait until the first one leaves the window (220 s).
        Assert.False(await budget.TryAcquireAsync(Start.AddSeconds(219), default));
        Assert.Equal(34, budget.SearchesInWindow);

        // It was refused without taking a slot: at 220 s it fits.
        time.Advance(TimeSpan.FromSeconds(50));
        Assert.True(await budget.TryAcquireAsync(time.GetUtcNow(), default));
    }

    [Fact]
    public async Task Searches_are_at_least_5_seconds_apart()
    {
        var time = new ManualTimeProvider(Start);
        var budget = new SearchBudget(new SearchLimit(34, 220), time);

        Assert.True(await budget.TryAcquireAsync(Start, default));
        Assert.False(await budget.TryAcquireAsync(Start.AddSeconds(4), default));   // next slot is +5 s
        time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(await budget.TryAcquireAsync(time.GetUtcNow(), default));
        time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(await budget.TryAcquireAsync(time.GetUtcNow(), default));
    }

    [Fact]
    public async Task Callers_are_served_in_order_and_each_waits_for_its_slot()
    {
        var budget = new SearchBudget(new SearchLimit(10, 60), TimeProvider.System, TimeSpan.FromMilliseconds(80));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var done = new List<(int Id, long Ms)>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        async Task Run(int id)
        {
            Assert.True(await budget.TryAcquireAsync(deadline, default));
            lock (done)
            {
                done.Add((id, watch.ElapsedMilliseconds));
            }
        }

        var tasks = new List<Task>();
        for (var i = 0; i < 4; i++)
        {
            tasks.Add(Run(i));
            await Task.Delay(5);   // start in a known order
        }

        await Task.WhenAll(tasks);
        Assert.Equal([0, 1, 2, 3], done.OrderBy(d => d.Ms).Select(d => d.Id).ToArray());
        Assert.True(done.Max(d => d.Ms) >= 200, "the fourth search should wait about 3 gaps (240 ms)");
    }

    [Fact]
    public async Task A_slot_after_the_deadline_returns_false_without_waiting_or_taking_a_slot()
    {
        var budget = new SearchBudget(new SearchLimit(2, 3600), TimeProvider.System, TimeSpan.Zero);
        Assert.True(await budget.TryAcquireAsync(DateTimeOffset.UtcNow.AddSeconds(1), default));
        Assert.True(await budget.TryAcquireAsync(DateTimeOffset.UtcNow.AddSeconds(1), default));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await budget.TryAcquireAsync(DateTimeOffset.UtcNow.AddSeconds(60), default));
        Assert.True(watch.ElapsedMilliseconds < 500);
        Assert.Equal(2, budget.SearchesInWindow);
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting()
    {
        var budget = new SearchBudget(new SearchLimit(5, 60), TimeProvider.System, TimeSpan.FromSeconds(30));
        Assert.True(await budget.TryAcquireAsync(DateTimeOffset.UtcNow.AddSeconds(1), default));
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.TryAcquireAsync(DateTimeOffset.UtcNow.AddMinutes(5), cts.Token));
    }

    [Fact]
    public void Rejects_a_limit_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(new SearchLimit(0, 220)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(new SearchLimit(34, 0)));
    }
}
