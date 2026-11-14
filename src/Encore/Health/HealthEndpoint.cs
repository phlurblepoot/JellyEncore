using Encore.Config;
using Encore.Orchestration;
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
            TimeProvider time) =>
        {
            var (code, report) = HealthEvaluator.Evaluate(new HealthInput(
                options,
                version,
                time.GetUtcNow(),
                watcher.Status,
                orchestrator.Status,
                store.Path,
                store.SchemaVersionOnDisk));
            return Results.Json(report, statusCode: code);
        });
    }
}
