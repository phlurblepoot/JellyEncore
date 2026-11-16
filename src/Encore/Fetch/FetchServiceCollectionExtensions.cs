using Encore.Config;
using Encore.Discovery;
using Encore.Fetch.Import;
using Encore.Fetch.Scoring;
using Encore.Jellyfin;
using Encore.Slskd;
using Encore.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Fetch;

public static class FetchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the fetch side (v0.2 design section 14). Mode off: only <see cref="NullFetchRequests"/> as
    /// <see cref="IFetchRequests"/>, no Deezer, ListenBrainz or slskd client. Dry run or live: the discovery and slskd
    /// clients, <see cref="OwnedIndex"/>, <see cref="CandidateSource"/> with the store's negative cache, the scorer,
    /// <see cref="SoulseekFetcher"/>, <see cref="Importer"/>, <see cref="JellyfinIngest"/>, the given
    /// <see cref="FetchStore"/>, <see cref="FetchCoordinator"/> (as <see cref="IFetchRequests"/>) and the hosted
    /// <see cref="FetchWorker"/>. Needs <c>AddJellyfinClient</c> and a <see cref="TimeProvider"/>.
    /// </summary>
    /// <param name="store">The fetch store, opened after the main store; required unless mode is off.</param>
    public static IServiceCollection AddFetch(this IServiceCollection services, EncoreOptions options, FetchStore? store)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.FetchMode == FetchMode.Off)
        {
            services.AddSingleton<IFetchRequests>(NullFetchRequests.Instance);
            return services;
        }

        ArgumentNullException.ThrowIfNull(store);
        services.AddDiscoveryClients();
        services.AddSlskdClient(options);
        services.AddSingleton(store);

        services.AddSingleton(sp => new OwnedIndex(
            sp.GetRequiredService<IJellyfinClient>(), options.RadioJellyfinDir, sp.GetRequiredService<ILogger<OwnedIndex>>(), Time(sp)));
        services.AddSingleton<IOwnedIndex>(sp => sp.GetRequiredService<OwnedIndex>());
        services.AddSingleton<INegativeCache>(_ => new FetchStoreNegativeCache(store));
        services.AddSingleton<ICandidateSource, CandidateSource>();

        services.AddSingleton<IScorer, Scorer>();
        services.AddSingleton<ISoulseekFetcher>(sp => new SoulseekFetcher(
            sp.GetRequiredService<ISlskdClient>(), sp.GetRequiredService<SearchBudget>(), sp.GetRequiredService<IScorer>(), store,
            options.SlskdDownloadsDir, sp.GetRequiredService<ILogger<SoulseekFetcher>>(), Time(sp)));

        services.AddSingleton<IFreeSpace, DriveFreeSpace>();
        services.AddSingleton(ImporterSettings.From(options));
        services.AddSingleton<IImporter>(sp => new Importer(
            sp.GetRequiredService<ImporterSettings>(), sp.GetRequiredService<IFreeSpace>(), sp.GetRequiredService<ILogger<Importer>>(),
            sp.GetRequiredService<IDeezerClient>()));

        // One registration only: a forwarding registration would make the container dispose it twice.
        services.AddSingleton<IJellyfinIngest>(sp => new JellyfinIngest(
            sp.GetRequiredService<IJellyfinClient>(), options.RadioJellyfinDir, sp.GetRequiredService<ILogger<JellyfinIngest>>(), Time(sp)));

        services.AddSingleton(sp => new FetchCoordinator(
            options.FetchMode, sp.GetRequiredService<IOwnedIndex>(), sp.GetRequiredService<ILogger<FetchCoordinator>>(), Time(sp),
            configured: options.SlskdUrl is not null && options.SlskdApiKey is not null));
        services.AddSingleton<IFetchRequests>(sp => sp.GetRequiredService<FetchCoordinator>());

        services.AddSingleton(FetchWorkerSettings.From(options));
        services.AddSingleton(sp => new FetchWorker(
            sp.GetRequiredService<FetchCoordinator>(), sp.GetRequiredService<ISoulseekFetcher>(), sp.GetRequiredService<IImporter>(),
            sp.GetRequiredService<IJellyfinIngest>(), sp.GetRequiredService<IOwnedIndex>(), sp.GetRequiredService<IJellyfinClient>(),
            sp.GetRequiredService<ISlskdClient>(), store, sp.GetRequiredService<IFreeSpace>(), sp.GetRequiredService<FetchWorkerSettings>(),
            sp.GetRequiredService<ILogger<FetchWorker>>(), Time(sp), sp.GetService<Encore.Retention.ISharesDirty>()));
        services.AddHostedService(sp => sp.GetRequiredService<FetchWorker>());
        return services;
    }

    private static TimeProvider Time(IServiceProvider sp) => sp.GetService<TimeProvider>() ?? TimeProvider.System;
}
