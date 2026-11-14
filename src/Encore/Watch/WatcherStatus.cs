namespace Encore.Watch;

public enum SocketState
{
    Connecting,
    Subscribed,
    Down,
}

/// <summary>Immutable watcher status for /health (design section 1.3 and 7). Swapped whole on every change.</summary>
public sealed record WatcherStatus
{
    public SocketState State { get; init; } = SocketState.Connecting;

    public DateTimeOffset Since { get; init; }

    /// <summary>Connection attempts after the first one.</summary>
    public int Reconnects { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public DateTimeOffset? LastSessionsAt { get; init; }

    /// <summary>True while the socket is not delivering Sessions: from start or a drop until the first socket Sessions message.</summary>
    public bool PollActive { get; init; } = true;

    public DateTimeOffset? LastPollAt { get; init; }

    /// <summary>"unauthorized" after a 401/403, otherwise the last failure reason (never a header or body); null once healthy.</summary>
    public string? LastError { get; init; }
}
