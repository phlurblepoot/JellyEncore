using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Orchestration;
using Encore.Slskd;
using Encore.Store;
using Encore.Watch;

namespace Encore.Health;

public static class HealthEndpoint
{
    /// <summary>Maps <c>GET /health</c>. Reads only immutable snapshots, so it never touches the store connection.</summary>
    public static void Map(WebApplication app, string version)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/health", (
            EncoreOptions options,
            SessionWatcher watcher,
            Orchestrator orchestrator,
            EncoreStore store,
            TimeProvider time,
            IServiceProvider services) =>
        {
            var (code, report) = HealthEvaluator.Evaluate(new HealthInput(
                options,
                version,
                time.GetUtcNow(),
                watcher.Status,
                orchestrator.Status,
                store.Path,
                store.SchemaVersionOnDisk,
                FetchInput(services)));
            return Results.Json(report, statusCode: code);
        });
    }

    /// <summary>Null in mode off, where none of the fetch services exist.</summary>
    private static FetchHealthInput? FetchInput(IServiceProvider services)
    {
        var requests = services.GetService<IFetchRequests>();
        if (requests is null || requests is NullFetchRequests)
        {
            return null;
        }

        var owned = services.GetService<IOwnedIndex>();
        return new FetchHealthInput(
            requests.Status,
            services.GetService<SearchBudget>()?.SearchesInWindow ?? 0,
            owned?.IsLoaded ?? false,
            owned?.Count ?? 0,
            owned?.RefreshedUtc,
            services.GetService<DiscoveryRuntime>()?.DeezerLastFailureUtc);
    }
}
