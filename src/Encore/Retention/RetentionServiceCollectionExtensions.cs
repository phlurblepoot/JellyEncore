using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Fetch.Import;
using Encore.Jellyfin;
using Encore.Lidarr;
using Encore.Slskd;
using Encore.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Retention;

public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers retention (v04 section 13). Mode off registers nothing (the hosted service list does not change). Dry run and live:
    /// the signals reader, the pass, the shares keeper, the slskd events reader, the play tally, the deleter, the promoter and the
    /// worker. Needs <c>AddFetch</c> with fetch mode live (the config check guarantees it), the Jellyfin client and a
    /// <see cref="TimeProvider"/>. The request store is used when it is registered.
    /// </summary>
    public static IServiceCollection AddRetention(this IServiceCollection services, EncoreOptions options, RetentionStore store)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RetentionMode == RetentionMode.Off)
        {
            return services;
        }

        ArgumentNullException.ThrowIfNull(store);
        services.AddSingleton(store);

        // IRetentionSlskd and IRetentionLidarr are the typed clients behind ISlskdClient and ILidarrClient.
        services.AddTransient<IRetentionSlskd>(sp => (IRetentionSlskd)sp.GetRequiredService<ISlskdClient>());
        if (options.LidarrUrl is not null && options.LidarrApiKey is not null)
        {
            if (!services.Any(d => d.ServiceType == typeof(ILidarrClient)))
            {
                services.AddLidarrClient(options);
            }

            services.AddTransient<IRetentionLidarr>(sp => (IRetentionLidarr)sp.GetRequiredService<ILidarrClient>());
        }

        services.AddSingleton(sp => new LidarrGuard(sp.GetService<ILidarrClient>(), sp.GetService<IRetentionLidarr>()));
        services.AddSingleton(sp => new RetentionSignals(
            sp.GetRequiredService<IJellyfinClient>(), sp.GetRequiredService<IRetentionJellyfin>(), options, Time(sp),
            sp.GetRequiredService<ILogger<RetentionSignals>>()));
        services.AddSingleton(sp => new RetentionPass(
            sp.GetRequiredService<FetchStore>(), store, sp.GetRequiredService<RetentionSignals>(), options, Time(sp),
            sp.GetRequiredService<ILogger<RetentionPass>>(), PromotionPaths.From(options).ReportTarget(sp.GetRequiredService<FetchStore>()),
            LibraryCopy.PathFinder(sp.GetRequiredService<IOwnedIndex>())));

        services.AddSingleton(sp => new SharesKeeper(
            sp.GetRequiredService<IRetentionSlskd>(), store, options.RadioDir, options.SlskdRadioDir,
            sp.GetRequiredService<ILogger<SharesKeeper>>(), Time(sp)));
        services.AddSingleton<ISharesDirty>(sp => sp.GetRequiredService<SharesKeeper>());
        services.AddSingleton<IUploadCheck>(sp => sp.GetRequiredService<SharesKeeper>());
        services.AddSingleton(sp => new SlskdEvents(
            sp.GetRequiredService<IRetentionSlskd>(), sp.GetRequiredService<FetchStore>(), store, options.RadioDir, options.SlskdRadioDir,
            sp.GetRequiredService<ILogger<SlskdEvents>>(), Time(sp)));
        services.AddSingleton(sp => new PlayTally(
            sp.GetRequiredService<FetchStore>(), store, options, Time(sp), sp.GetRequiredService<ILogger<PlayTally>>()));

        services.AddSingleton(sp => new Deleter(
            sp.GetRequiredService<FetchStore>(), store, sp.GetRequiredService<RetentionSignals>(), sp.GetRequiredService<IJellyfinClient>(),
            sp.GetRequiredService<IOwnedIndex>(), sp.GetRequiredService<IUploadCheck>(), sp.GetRequiredService<ISharesDirty>(), options, Time(sp),
            sp.GetRequiredService<ILogger<Deleter>>()));
        services.AddSingleton(sp => new Promoter(
            sp.GetRequiredService<FetchStore>(), store, sp.GetService<RequestStore>(), sp.GetRequiredService<IJellyfinClient>(),
            sp.GetRequiredService<IRetentionJellyfin>(), sp.GetRequiredService<IOwnedIndex>(), sp.GetRequiredService<LidarrGuard>(),
            sp.GetRequiredService<IFreeSpace>(), sp.GetRequiredService<ISharesDirty>(), sp.GetRequiredService<IUploadCheck>(), options, Time(sp),
            sp.GetRequiredService<ILogger<Promoter>>()));
        services.AddSingleton(sp => RetentionSteps.From(
            sp.GetRequiredService<RetentionPass>(), sp.GetRequiredService<Deleter>(), sp.GetRequiredService<Promoter>()));
        services.AddSingleton(sp => new RetentionWorker(
            sp.GetRequiredService<RetentionSteps>(), options, Time(sp), sp.GetRequiredService<ILogger<RetentionWorker>>()));

        // The Orchestrator takes the same PlayTally instance from the container (its optional constructor argument).
        services.AddHostedService(sp => sp.GetRequiredService<SharesKeeper>());
        services.AddHostedService(sp => sp.GetRequiredService<SlskdEvents>());
        services.AddHostedService(sp => sp.GetRequiredService<PlayTally>());
        services.AddHostedService(sp => sp.GetRequiredService<RetentionWorker>());
        return services;
    }

    private static TimeProvider Time(IServiceProvider sp) => sp.GetService<TimeProvider>() ?? TimeProvider.System;
}
