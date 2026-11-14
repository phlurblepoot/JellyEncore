using System.Threading.Channels;
using Encore.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Encore.Watch;

public static class WatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the snapshot channel (reader for the orchestrator), the socket factory and <see cref="SessionWatcher"/>
    /// as a hosted service. Needs <c>AddJellyfinClient</c>. Not called from Program yet: the orchestrator task wires it.
    /// </summary>
    public static IServiceCollection AddSessionWatcher(this IServiceCollection services, EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var channel = SessionSnapshots.CreateChannel();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(channel.Reader);
        services.AddSingleton<ChannelWriter<SessionsSnapshot>>(channel.Writer);
        services.AddSingleton(WatcherSettings.FromOptions(options));
        services.TryAddSingleton<ISessionSocketFactory, ClientSessionSocketFactory>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SessionWatcher>(sp));
        services.AddHostedService(sp => sp.GetRequiredService<SessionWatcher>());
        return services;
    }
}
