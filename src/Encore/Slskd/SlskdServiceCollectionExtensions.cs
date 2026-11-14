using Encore.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Slskd;

public static class SlskdServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISlskdClient"/> as a typed HttpClient with the base URL, the timeout and the X-API-Key handler,
    /// plus the shared <see cref="SearchBudget"/>. HttpClient logging never shows the X-API-Key value.
    /// Only call it when fetch mode is not off (the URL and key are then set). Not called from Program yet.
    /// </summary>
    public static IHttpClientBuilder AddSlskdClient(this IServiceCollection services, EncoreOptions options, SlskdClientOptions? clientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SlskdUrl is null || options.SlskdApiKey is null)
        {
            throw new InvalidOperationException("slskd URL and API key are not configured (fetch mode is off)");
        }

        var client = clientOptions ?? new SlskdClientOptions();
        var apiKey = options.SlskdApiKey;
        var baseAddress = new Uri(options.SlskdUrl.AbsoluteUri.TrimEnd('/') + "/");

        services.AddSingleton(client);
        services.AddSingleton(sp => new SearchBudget(options.SearchLimit, sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services.AddHttpClient<ISlskdClient, SlskdClient>(http =>
            {
                http.BaseAddress = baseAddress;
                http.Timeout = client.Timeout;
            })
            .RedactLoggedHeaders([SlskdAuthHandler.HeaderName, "Authorization"])
            .AddHttpMessageHandler(() => new SlskdAuthHandler(apiKey));
    }
}
