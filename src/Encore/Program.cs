using Encore;

// Container HEALTHCHECK entry point: the aspnet image has no curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<EncoreWorker>();

var app = builder.Build();
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
