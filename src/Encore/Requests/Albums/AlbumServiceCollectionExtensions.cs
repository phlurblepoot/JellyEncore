using Encore.Config;
using Encore.Jellyfin;
using Encore.Lidarr;
using Encore.Requests.Songs;
using Encore.Store;

namespace Encore.Requests.Albums;

public static class AlbumServiceCollectionExtensions
{
    /// <summary>
    /// Album requests (v0.3 design 7 and 14): the Lidarr client and its status monitor, the album search, the requester and
    /// its worker. Only with ENCORE_LIDARR_URL (and its key); without them nothing is registered and album requests answer
    /// 503 <c>albums_unavailable</c>. Needs the request store, the Jellyfin client and an <see cref="IRequestNotifier"/>.
    /// </summary>
    public static IServiceCollection AddAlbumRequests(this IServiceCollection services, EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.LidarrUrl is null || options.LidarrApiKey is null)
        {
            return services;
        }

        services.AddLidarrClient(options);
        services.AddSingleton<IAlbumLibrary>(sp => new JellyfinAlbumLibrary(sp.GetRequiredService<IJellyfinClient>()));
        services.AddSingleton(sp => new AlbumSearch(
            sp.GetRequiredService<ILidarrClient>(), sp.GetRequiredService<ILogger<AlbumSearch>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AlbumRequester(
            sp.GetRequiredService<RequestStore>(), sp.GetRequiredService<ILidarrClient>(), sp.GetRequiredService<IAlbumLibrary>(),
            AlbumRequestSettings.From(options), sp.GetRequiredService<ILogger<AlbumRequester>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AlbumWorker(
            sp.GetRequiredService<AlbumRequester>(), sp.GetRequiredService<IRequestNotifier>(), sp.GetRequiredService<ILogger<AlbumWorker>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<AlbumWorker>());
        return services;
    }
}
