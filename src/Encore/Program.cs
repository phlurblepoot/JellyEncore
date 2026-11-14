using System.Reflection;
using Encore;
using Encore.Config;
using Encore.Logging;

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

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(Encore.Logging.LogSetup.ConfigureJson);
Encore.Logging.LogSetup.ApplyFilters(builder.Logging, options.LogLevel);
// Second layer of defence: HttpClient logging must never show the Authorization header.
builder.Services.ConfigureHttpClientDefaults(b => b.RedactLoggedHeaders(name => string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)));
builder.Services.AddSingleton(options);
builder.Services.AddHostedService<EncoreWorker>();

var app = builder.Build();
var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Encore.Startup").StartupConfig(options, version);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
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
