using System.Reflection;
using Encore;
using Encore.Config;
using Encore.Fetch;
using Encore.Health;
using Encore.Jellyfin;
using Encore.Logging;
using Encore.Orchestration;
using Encore.Picks;
using Encore.Store;
using Encore.Watch;

// Container HEALTHCHECK entry point: the aspnet image has no curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var config = EncoreOptionsLoader.LoadFromEnvironment();
if (config.Options is not { } options)
{
    // Fail fast: one log line per bad variable, then exit 2. No value of the API key is ever included.
    using var bootstrap = LoggerFactory.Create(b => b.AddJsonConsole(Encore.Logging.LogSetup.ConfigureJson));
    var bootLogger = bootstrap.CreateLogger("Encore.Startup");
    foreach (var error in config.Errors)
    {
        bootLogger.StartupConfigInvalid(error.Variable, error.Message);
    }

    return EncoreOptionsLoader.ExitCode;
}

// The store opens before the host, so a database that is broken or newer than this build stops the process with exit code 3.
using var storeLoggerFactory = LoggerFactory.Create(b => b.AddJsonConsole(Encore.Logging.LogSetup.ConfigureJson));
var storeLogger = storeLoggerFactory.CreateLogger("Encore.Startup");
EncoreStore store;
try
{
    store = EncoreStore.Open(Path.Combine(options.DataDir, "encore.db"), TimeProvider.System);
    var pruned = store.Prune(TimeProvider.System.GetUtcNow());
    storeLogger.StoreReady(store.Path, store.SchemaVersionOnDisk, pruned.Plays + pruned.AddedItems + pruned.Triggers);
}
catch (StoreException ex)
{
    storeLogger.StoreError("open", ex.Message);
    return 3;
}

// The fetch store (dry run and live) is a second connection to the same file, opened after the main store migrated it.
FetchStore? fetchStore = null;
if (options.FetchMode != FetchMode.Off)
{
    try
    {
        fetchStore = FetchStore.Open(store.Path);
    }
    catch (StoreException ex)
    {
        storeLogger.StoreError("open_fetch", ex.Message);
        store.Dispose();
        return 3;
    }
}

// Live mode: the radio dir gets its soulseek folder now, so Jellyfin never scans an empty music-radio.
if (options.FetchMode == FetchMode.Live)
{
    try
    {
        if (Encore.Fetch.Import.RadioFolders.EnsureSoulseekFolder(options.RadioDir))
        {
            Encore.Fetch.Import.ImportLog.ImportFolderCreated(storeLogger, options.RadioDir);
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Encore.Fetch.Import.ImportLog.ImportFolderFailed(storeLogger, options.RadioDir, ex.GetType().Name);
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(Encore.Logging.LogSetup.ConfigureJson);
Encore.Logging.LogSetup.ApplyFilters(builder.Logging, options.LogLevel);
// Second layer of defence: HttpClient logging must never show the Authorization header.
builder.Services.ConfigureHttpClientDefaults(b => b.RedactLoggedHeaders(name => string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddJellyfinClient(options);
builder.Services.AddSessionWatcher(options);
builder.Services.AddFetch(options, fetchStore);
builder.Services.AddSingleton<IPicker, Picker>();
builder.Services.AddSingleton<Orchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Orchestrator>());

var app = builder.Build();
var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Encore.Startup");
startupLogger.StartupConfig(options, version);
foreach (var warning in config.Warnings)
{
    startupLogger.StartupConfigWarning(warning.Variable, warning.Message);
}

app.Lifetime.ApplicationStopped.Register(() =>
{
    fetchStore?.Dispose();
    store.Dispose();
});
HealthEndpoint.Map(app, version);
app.Run();
return 0;

namespace Encore
{
    public partial class Program;

    internal static class HealthCheck
    {
        public static async Task<int> RunAsync()
        {
            var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "8080";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                return response.IsSuccessStatusCode ? 0 : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }
    }
}
