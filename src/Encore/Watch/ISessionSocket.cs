namespace Encore.Watch;

/// <summary>
/// One Jellyfin <c>/socket</c> connection attempt. A new instance is made for every attempt and disposed after it,
/// so nothing carries over between reconnects. Tests use a fake.
/// </summary>
public interface ISessionSocket : IAsyncDisposable
{
    /// <summary>Opens the socket with <c>Authorization: {authorization}</c> on the upgrade request.</summary>
    /// <exception cref="Encore.Jellyfin.JellyfinAuthException">The upgrade was refused with 401 or 403.</exception>
    /// <exception cref="Encore.Jellyfin.JellyfinRequestException">The upgrade failed for any other reason.</exception>
    Task ConnectAsync(Uri uri, string authorization, CancellationToken ct);

    /// <summary>Sends one text message. Safe to call from two loops at once.</summary>
    Task SendTextAsync(string text, CancellationToken ct);

    /// <summary>Receives one whole text message, or null once the server closed the socket.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);

    /// <summary>The close status the server sent, if any (for the <c>socket.closed</c> log).</summary>
    string? CloseStatus { get; }

    /// <summary>The close description the server sent, trimmed, if any.</summary>
    string? CloseDescription { get; }
}

public interface ISessionSocketFactory
{
    ISessionSocket Create();
}
