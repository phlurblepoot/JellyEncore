using Encore.Config;
using Encore.Jellyfin;

namespace Encore.Watch;

/// <summary>
/// Timings and the socket target for <see cref="SessionWatcher"/>. A class, not a record, so the auth header
/// (which holds the API key) is never printed by a generated ToString.
/// </summary>
public sealed class WatcherSettings
{
    public required Uri SocketUrl { get; init; }

    /// <summary>The full Authorization header value. Never log it.</summary>
    public required string Authorization { get; init; }

    /// <summary>GET /Sessions interval while the socket is not delivering (ENCORE_POLL_SECONDS).</summary>
    public required TimeSpan PollInterval { get; init; }

    /// <summary>App-level KeepAlive. Jellyfin drops a socket after 60 s without one.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>While subscribed, one GET /Sessions after this long without a Sessions message.</summary>
    public TimeSpan ReconcileAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Longest time for the connect and the SessionsStart send of one attempt. ClientWebSocket has no timeout of its own,
    /// so a server that accepts TCP but never answers the upgrade would hold the attempt (and block reconnects) forever.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public IReadOnlyList<TimeSpan> BackoffSteps { get; init; } = ReconnectBackoff.DefaultSteps;

    public TimeSpan BackoffResetAfter { get; init; } = ReconnectBackoff.DefaultResetAfter;

    public double BackoffJitter { get; init; } = ReconnectBackoff.DefaultJitter;

    public override string ToString() => $"WatcherSettings {{ SocketUrl = {SocketUrl}, Authorization = <redacted>, PollInterval = {PollInterval} }}";

    public static WatcherSettings FromOptions(EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var parameter = JellyfinAuth.BuildParameter(options.JellyfinApiKey, options.DeviceId, JellyfinAuth.VersionFromAssembly());
        return new WatcherSettings
        {
            SocketUrl = options.WebSocketUrl,
            Authorization = $"{JellyfinAuth.Scheme} {parameter}",
            PollInterval = TimeSpan.FromSeconds(options.PollSeconds),
        };
    }
}
