using Encore.Config;
using Encore.Retention;
using Encore.Store;
using Encore.Tests.Fetch;
using Encore.Tests.Trigger;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.Retention;

/// <summary>
/// Shutdown (T133). AddRetention registers SharesKeeper, SlskdEvents, PlayTally and RetentionWorker once as singletons and forwards them
/// as hosted services, so the container disposes each of them twice (conventions.md, the register-once rule). Each must take that.
/// </summary>
public sealed class RetentionShutdownTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-shutdown-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero));
    private readonly EncoreStore _main;
    private readonly FetchStore _fetch;
    private readonly RetentionStore _store;

    public RetentionShutdownTests()
    {
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "encore.db");
        _main = EncoreStore.Open(db, _time);
        _fetch = FetchStore.Open(db);
        _store = RetentionStore.Open(db);
    }

    public void Dispose()
    {
        _store.Dispose();
        _fetch.Dispose();
        _main.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IEnumerable<(string Name, Func<BackgroundService> Make)> Services()
    {
        var options = new EncoreOptions
        {
            JellyfinUrl = new Uri("http://jellyfin.test:8096"),
            JellyfinApiKey = "test-key-not-real",
            AllowedUsers = ["ann"],
            DataDir = _dir,
            RadioDir = Path.Combine(_dir, "radio"),
            RetentionMode = RetentionMode.DryRun,
        };
        var slskd = new FakeSlskd(_time, _dir);
        yield return ("SharesKeeper", () => new SharesKeeper(slskd, _store, "/music-radio", "/music-radio", NullLogger<SharesKeeper>.Instance, _time));
        yield return ("SlskdEvents", () => new SlskdEvents(slskd, _fetch, _store, "/music-radio", "/music-radio", NullLogger<SlskdEvents>.Instance, _time));
        yield return ("PlayTally", () => new PlayTally(_fetch, _store, options, _time, NullLogger<PlayTally>.Instance));
        yield return ("RetentionWorker", () => new RetentionWorker(
            new RetentionSteps(_ => throw new InvalidOperationException("no pass in this test"), (_, _) => Task.FromResult(DeleteResult.None), (_, _, _, _) => Task.FromResult<IReadOnlyList<PromotionResult>>([])),
            options, _time, NullLogger<RetentionWorker>.Instance));
    }

    [Fact]
    public async Task SD01_each_retention_hosted_service_takes_a_second_dispose_after_a_start_and_stop()
    {
        foreach (var (name, make) in Services())
        {
            var service = make();
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            var first = Record.Exception(service.Dispose);
            var second = Record.Exception(service.Dispose);

            Assert.True(first is null, $"{name}: first Dispose threw {first}");
            Assert.True(second is null, $"{name}: second Dispose threw {second}");
        }
    }

    [Fact]
    public void SD02_each_retention_hosted_service_takes_a_second_dispose_without_a_start()
    {
        foreach (var (name, make) in Services())
        {
            var service = make();
            service.Dispose();
            var second = Record.Exception(service.Dispose);
            Assert.True(second is null, $"{name}: second Dispose threw {second}");
        }
    }
}
