using System.Threading.Channels;
using Encore.Jellyfin;

namespace Encore.Watch;

/// <summary>Where a snapshot came from.</summary>
public enum SnapshotSource
{
    /// <summary>A <c>Sessions</c> message pushed over <c>/socket</c>.</summary>
    Socket,

    /// <summary>A <c>GET /Sessions</c> made while the socket was down (or the 10-minute reconcile).</summary>
    Poll,
}

/// <summary>Every Jellyfin session as seen at one moment. The orchestrator is the only reader.</summary>
public sealed record SessionsSnapshot(DateTimeOffset ReceivedAt, SnapshotSource Source, IReadOnlyList<SessionInfoDto> Sessions);

public static class SessionSnapshots
{
    /// <summary>Channel capacity. When it is full the oldest snapshot is dropped, so the socket reader never blocks.</summary>
    public const int Capacity = 16;

    /// <summary>Bounded 16, drop-oldest, one reader (the orchestrator), two writers (socket and poll loops).</summary>
    public static Channel<SessionsSnapshot> CreateChannel() =>
        Channel.CreateBounded<SessionsSnapshot>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
}
