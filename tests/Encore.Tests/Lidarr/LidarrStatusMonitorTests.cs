using System.Net;
using Encore.Lidarr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.Lidarr;

public class LidarrStatusMonitorTests
{
    private const string Key = "LidarrKey0123456789abcdefAB";

    private static (LidarrStatusMonitor Monitor, FakeLidarr Fake, LidarrClientTests.CapturingLoggerProvider Log) Make(FakeLidarr fake)
    {
        var log = new LidarrClientTests.CapturingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        var options = new LidarrClientOptions { Timeout = TimeSpan.FromMilliseconds(300), GetRetryDelay = TimeSpan.FromMilliseconds(1) };
        var http = new HttpClient(new LidarrAuthHandler(Key) { InnerHandler = fake }) { BaseAddress = new Uri("http://lidarr:8686/"), Timeout = options.Timeout };
        var monitor = new LidarrStatusMonitor(new LidarrClient(http, options), options, factory.CreateLogger<LidarrStatusMonitor>(), new FakeTime());
        return (monitor, fake, log);
    }

    [Fact]
    public void The_default_interval_is_60_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new LidarrClientOptions().StatusInterval);
    }

    [Fact]
    public async Task Before_the_first_check_reachability_is_unknown()
    {
        var (monitor, _, _) = Make(new FakeLidarr());
        Assert.True(monitor.State.Configured);
        Assert.Null(monitor.State.Reachable);
    }

    [Fact]
    public async Task A_good_check_records_version_and_time()
    {
        var (monitor, fake, _) = Make(new FakeLidarr().Reply(HttpStatusCode.OK, LidarrJson.Status));
        await monitor.CheckOnceAsync(default);

        var s = monitor.State;
        Assert.True(s.Reachable);
        Assert.Equal("3.1.6.5079", s.Version);
        Assert.NotNull(s.LastOkUtc);
        Assert.Null(s.LastError);
        Assert.False(s.KeyRejected);
        Assert.Equal("/api/v1/system/status", Assert.Single(fake.Sent).PathAndQuery);
    }

    [Fact]
    public async Task A_rejected_key_is_reachable_but_flagged_and_the_key_never_reaches_state_or_logs()
    {
        var (monitor, _, log) = Make(new FakeLidarr().Reply(HttpStatusCode.OK, LidarrJson.Status).Reply(HttpStatusCode.Unauthorized, Key));
        await monitor.CheckOnceAsync(default);
        await monitor.CheckOnceAsync(default);

        var s = monitor.State;
        Assert.True(s.Reachable);
        Assert.True(s.KeyRejected);
        Assert.Equal("3.1.6.5079", s.Version);
        Assert.Contains("401", s.LastError);
        Assert.DoesNotContain(Key, s.ToString() + s.LastError);
        Assert.Contains(log.Lines, l => l.Contains("rejected the API key"));
        Assert.DoesNotContain(log.Lines, l => l.Contains(Key));
    }

    [Fact]
    public async Task Unreachable_then_recovered_and_the_state_is_logged_only_on_change()
    {
        var (monitor, _, log) = Make(new FakeLidarr()
            .Fail(new HttpRequestException($"down {Key}", null, null))
            .Fail(new HttpRequestException($"down {Key}", null, null))
            .Reply(HttpStatusCode.OK, LidarrJson.Status)
            .Reply(HttpStatusCode.OK, LidarrJson.Status));

        await monitor.CheckOnceAsync(default);
        Assert.False(monitor.State.Reachable);
        Assert.NotNull(monitor.State.LastError);
        Assert.DoesNotContain(Key, monitor.State.LastError);
        await monitor.CheckOnceAsync(default);
        await monitor.CheckOnceAsync(default);
        Assert.True(monitor.State.Reachable);
        Assert.False(monitor.State.KeyRejected);
        await monitor.CheckOnceAsync(default);

        var stateLines = log.Lines.Where(l => l.Contains("Lidarr reachable")).ToList();
        Assert.Equal(2, stateLines.Count);
        Assert.DoesNotContain(log.Lines, l => l.Contains(Key));
    }

    [Fact]
    public async Task Cancellation_is_not_recorded_as_a_failure()
    {
        var (monitor, _, _) = Make(new FakeLidarr().Hang());
        using var cts = new CancellationTokenSource();
        var check = monitor.CheckOnceAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Null(monitor.State.Reachable);
    }

    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    }
}
