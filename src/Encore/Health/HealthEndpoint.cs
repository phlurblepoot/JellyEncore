using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Lidarr;
using Encore.Orchestration;
using Encore.Retention;
using Encore.Requests.Albums;
using Encore.Requests.Auth;
using Encore.Requests.Notify;
using Encore.Requests.Songs;
using Encore.Slskd;
using Encore.Store;
using Microsoft.Data.Sqlite;
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
                RequestsInput(options, services, time.GetUtcNow()),
                RetentionInput(options, services, time.GetUtcNow(), orchestrator)));
            return Results.Json(report, statusCode: code);
        });
    }

    /// <summary>Null with ENCORE_RETENTION_MODE=off, where none of the retention services exist.</summary>
    private static RetentionHealthInput? RetentionInput(EncoreOptions options, IServiceProvider services, DateTimeOffset now, Orchestrator orchestrator)
    {
        if (options.RetentionMode == RetentionMode.Off)
        {
            return null;
        }

        var fetch = services.GetService<FetchStore>();
        var store = services.GetService<RetentionStore>();
        var pass = services.GetService<RetentionPass>();
        var promoter = services.GetService<Promoter>();
        var keeper = services.GetService<SharesKeeper>();
        var events = services.GetService<SlskdEvents>();
        if (fetch is null || store is null || pass is null)
        {
            return null;
        }

        var latest = pass.Latest;
        var next = latest is null
            ? orchestrator.Status.StartedUtc + new RetentionTiming().StartupDelay
            : now + RetentionWorker.DelayUntilNext(now, options.RetentionHour, TimeZoneInfo.Local);
        var active = Safe(() => store.ActivePromotions(), []);
        return new RetentionHealthInput(
            options.RetentionMode,
            latest,
            next,
            Safe(() => fetch.CountState(FetchState.Promoted), 0),
            Safe(() => fetch.CountState(FetchState.Deleted), 0),
            active.Count(p => !p.Stalled),
            active.Count(p => p.Stalled),
            TrashCount(options.RadioDir),
            keeper?.RadioShared ?? false,
            events?.UploadsCounted ?? 0,
            events?.Unreadable ?? false,
            events?.LastReadUtc,
            keeper is null ? null : Safe(() => keeper.LastRescanUtc, null),
            keeper?.Dirty ?? false,
            promoter?.LastGuard);
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is SqliteException or StoreException)
        {
            return fallback;
        }
    }

    /// <summary>Files in the dated trash folders under the radio folder; 0 when there is none or it cannot be read.</summary>
    private static int TrashCount(string radioDir)
    {
        try
        {
            var root = Path.Combine(Path.GetFullPath(radioDir), Deleter.TrashFolder);
            return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
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
