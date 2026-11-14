using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Discovery;

public static class DiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the keyless Deezer and ListenBrainz Labs clients as typed HttpClients, with one shared
    /// <see cref="DiscoveryRuntime"/> (limiters, cache, clock). Called only when the fetch mode is not off.
    /// </summary>
    public static IServiceCollection AddDiscoveryClients(this IServiceCollection services, DiscoveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var settings = options ?? new DiscoveryOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DiscoveryRuntime(settings, sp.GetRequiredService<TimeProvider>()));

        services.AddHttpClient<IDeezerClient, DeezerClient>(http => Configure(http, "https://api.deezer.com/"));
        services.AddHttpClient<IListenBrainzClient, ListenBrainzClient>(http => Configure(http, "https://labs.api.listenbrainz.org/"));
        return services;
    }

    private static void Configure(HttpClient http, string baseAddress)
    {
        http.BaseAddress = new Uri(baseAddress);

        // The clients enforce their own timeout on the injected clock.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.MaxResponseContentBufferSize = 4 * 1024 * 1024;
    }
}
