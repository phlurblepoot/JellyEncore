using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Lidarr;
using Encore.Orchestration;
using Encore.Requests.Albums;
using Encore.Requests.Auth;
using Encore.Requests.Notify;
using Encore.Requests.Songs;
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
                FetchInput(services),
                RequestsInput(options, services, time.GetUtcNow())));
            return Results.Json(report, statusCode: code);
        });
    }

    /// <summary>Null with ENCORE_REQUESTS=off, where none of the request services exist.</summary>
    private static RequestsHealthInput? RequestsInput(EncoreOptions options, IServiceProvider services, DateTimeOffset now)
    {
        if (!options.RequestsEnabled)
        {
            return null;
        }

        var store = services.GetService<RequestStore>();
        var guard = services.GetService<LoginGuard>();
        if (store is null || guard is null)
        {
            return null;
        }

        var notify = (services.GetService<IRequestNotifier>() as RequestNotifier)?.Counts ?? (0, 0);
        return new RequestsHealthInput(
            store.CountActiveByState(),
            guard.Snapshot(),
            store.CountsSince(now - TimeSpan.FromHours(24)),
            options.LidarrUrl is null ? null : services.GetService<LidarrStatusMonitor>()?.State ?? new LidarrState(true, null, null, null, null, false),
            notify.Item1,
            notify.Item2,
            services.GetService<AlbumRequester>()?.WaitingForJellyfin ?? 0);
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
