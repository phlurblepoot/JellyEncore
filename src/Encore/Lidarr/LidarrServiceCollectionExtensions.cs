using Encore.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Lidarr;

public static class LidarrServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILidarrClient"/> as a typed HttpClient with the base URL, the timeout and the X-Api-Key handler,
    /// plus the <see cref="LidarrStatusMonitor"/> (also a hosted service). HttpClient logging never shows the X-Api-Key value.
    /// Only call it when ENCORE_LIDARR_URL and the key are set. Not called from Program yet.
    /// </summary>
    public static IHttpClientBuilder AddLidarrClient(this IServiceCollection services, EncoreOptions options, LidarrClientOptions? clientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.LidarrUrl is null || options.LidarrApiKey is null)
        {
            throw new InvalidOperationException("Lidarr URL and API key are not configured (album requests are off)");
        }

        var client = clientOptions ?? new LidarrClientOptions();
        var apiKey = options.LidarrApiKey;
        var baseAddress = new Uri(options.LidarrUrl.AbsoluteUri.TrimEnd('/') + "/");

        services.AddSingleton(client);
        services.AddSingleton<LidarrStatusMonitor>(sp => new LidarrStatusMonitor(
            sp.GetRequiredService<ILidarrClient>(), client,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LidarrStatusMonitor>>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<LidarrStatusMonitor>());
        return services.AddHttpClient<ILidarrClient, LidarrClient>(http =>
            {
                http.BaseAddress = baseAddress;
                http.Timeout = client.Timeout;
            })
            .RedactLoggedHeaders([LidarrAuthHandler.HeaderName, "Authorization"])
            .AddHttpMessageHandler(() => new LidarrAuthHandler(apiKey));
    }
}
