using Encore.Config;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Store;
using Microsoft.Data.Sqlite;
using Encore.Fetch;
using Encore.Slskd;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Tests.Fetch;

/// <summary>FW17: mode off (the test environment's default) registers the null fetch side and no outside client.</summary>
public class FetchWiringTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void FW17_mode_off_registers_NullFetchRequests_and_no_Deezer_ListenBrainz_or_slskd_client()
    {
        var services = factory.Services;
        Assert.Same(NullFetchRequests.Instance, services.GetRequiredService<IFetchRequests>());
        Assert.Null(services.GetService<IDeezerClient>());
        Assert.Null(services.GetService<IListenBrainzClient>());
        Assert.Null(services.GetService<ISlskdClient>());
        Assert.Null(services.GetService<FetchCoordinator>());
        Assert.Null(services.GetService<FetchWorker>());
        Assert.Null(services.GetService<IOwnedIndex>());
    }

    [Fact]
    public void FW17_dry_run_and_live_register_the_whole_fetch_side()
    {
        var root = Path.Combine(Path.GetTempPath(), "encore-t49w-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "encore.db");
        EncoreStore.Open(db, TimeProvider.System).Dispose();
        try
        {
            foreach (var mode in new[] { FetchMode.DryRun, FetchMode.Live })
            {
                using var store = FetchStore.Open(db);
                var options = new EncoreOptions
                {
                    JellyfinUrl = new Uri("http://jellyfin:8096"), JellyfinApiKey = "0123456789abcdef0123456789abcdef",
                    AllowedUsers = ["nate"], DataDir = root, DeviceId = "encore-test", FetchMode = mode, DryRun = false,
                    SlskdUrl = new Uri("http://slskd:5030"), SlskdApiKey = "abcdefghijklmnop0123",
                };
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton(TimeProvider.System);
                services.AddJellyfinClient(options);
                services.AddFetch(options, store);
                using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

                var coordinator = Assert.IsType<FetchCoordinator>(provider.GetRequiredService<IFetchRequests>());
                Assert.Equal(mode, coordinator.Mode);
                Assert.IsType<OwnedIndex>(provider.GetRequiredService<IOwnedIndex>());
                Assert.IsType<CandidateSource>(provider.GetRequiredService<ICandidateSource>());
                Assert.NotNull(provider.GetRequiredService<FetchWorker>());
                Assert.Contains(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is FetchWorker);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
