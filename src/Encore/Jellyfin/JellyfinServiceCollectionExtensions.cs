using Encore.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Jellyfin;

public static class JellyfinServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IJellyfinClient"/> as a typed HttpClient with the base URL, the per-attempt timeout and the auth handler.
    /// Not called from Program yet: the orchestrator task wires it.
    /// </summary>
    public static IHttpClientBuilder AddJellyfinClient(this IServiceCollection services, EncoreOptions options, JellyfinClientOptions? clientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var client = clientOptions ?? new JellyfinClientOptions();
        var parameter = JellyfinAuth.BuildParameter(options.JellyfinApiKey, options.DeviceId, JellyfinAuth.VersionFromAssembly());
        var baseAddress = new Uri(options.JellyfinUrl.AbsoluteUri.TrimEnd('/') + "/");

        services.AddSingleton(client);
        // The retention calls are the same typed client (same HttpClient, auth handler and timeouts).
        services.AddTransient<IRetentionJellyfin>(sp => (IRetentionJellyfin)sp.GetRequiredService<IJellyfinClient>());
        return services.AddHttpClient<IJellyfinClient, JellyfinClient>(http =>
            {
                http.BaseAddress = baseAddress;
                http.Timeout = client.Timeout;
            })
            .AddHttpMessageHandler(() => new JellyfinAuthHandler(parameter));
    }
}
