namespace Encore.Trigger;

/// <summary>
/// The parts of one Jellyfin session snapshot the trigger reads. The orchestrator maps the
/// Jellyfin client's session DTO onto this, so the trigger stays independent of the wire format.
/// </summary>
public sealed record TriggerSession
{
    /// <summary>Jellyfin session id (kept as a string, as the server sends it).</summary>
    public required string Id { get; init; }

    /// <summary>Owner of the session; null or <see cref="Guid.Empty"/> for sessions without a user (such as Encore's own).</summary>
    public Guid? UserId { get; init; }

    public string? UserName { get; init; }

    /// <summary>Client name, for example "Jellyfin Web" or "Finamp".</summary>
    public string? Client { get; init; }

    public string? DeviceId { get; init; }

    public bool? SupportsRemoteControl { get; init; }

    public NowPlaying? NowPlayingItem { get; init; }

    /// <summary>The server's stored queue. Jellyfin 12.4 only updates it from PlaybackStopped reports.</summary>
    public IReadOnlyList<QueueEntry> NowPlayingQueue { get; init; } = [];

    /// <summary>Session-level PlaylistItemId: the current entry for jellyfin-web, the album or playlist source id for Finamp.</summary>
    public string? PlaylistItemId { get; init; }

    /// <summary>PlayState.RepeatMode as the server sends it: RepeatNone, RepeatAll or RepeatOne. Null counts as RepeatNone.</summary>
    public string? RepeatMode { get; init; }

    public bool IsPaused { get; init; }

    /// <summary>PlayState.PlaybackOrder (Default or Shuffle). Informational: shuffle needs no special handling.</summary>
    public string? PlaybackOrder { get; init; }
}

/// <summary>The playing item: only its id and type matter to the trigger.</summary>
public sealed record NowPlaying(Guid Id, string? Type, string? Name = null);

/// <summary>One NowPlayingQueue entry.</summary>
public sealed record QueueEntry(Guid Id, string? PlaylistItemId);
