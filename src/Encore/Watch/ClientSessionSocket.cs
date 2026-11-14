using System.Net.WebSockets;
using System.Text;
using Encore.Jellyfin;

namespace Encore.Watch;

/// <summary><see cref="ISessionSocket"/> over <see cref="ClientWebSocket"/>.</summary>
public sealed class ClientSessionSocket : ISessionSocket
{
    /// <summary>Protocol ping interval. With <see cref="KeepAliveTimeout"/> it detects a dead peer even when Jellyfin is idle.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Largest message accepted. A Sessions push for a busy server is well under this.</summary>
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    private const int MaxDescriptionLength = 128;
    private const string Operation = "socket";

    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _buffer = new byte[16 * 1024];

    public string? CloseStatus => _ws.CloseStatus?.ToString();

    public string? CloseDescription
    {
        get
        {
            var text = _ws.CloseStatusDescription;
            return text is { Length: > MaxDescriptionLength } ? text[..MaxDescriptionLength] : text;
        }
    }

    public async Task ConnectAsync(Uri uri, string authorization, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", authorization);
        _ws.Options.KeepAliveInterval = KeepAliveInterval;
        _ws.Options.KeepAliveTimeout = KeepAliveTimeout;
        _ws.Options.CollectHttpResponseDetails = true;

        try
        {
            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            var status = (int)_ws.HttpStatusCode;
            if (status is 401 or 403)
            {
                throw new JellyfinAuthException(Operation, status);
            }

            // Only the status or the error code: exception messages can carry the URL, and nothing here may carry headers.
            throw status > 0
                ? new JellyfinRequestException(Operation, status)
                : new JellyfinRequestException(Operation, $"connect failed ({ex.WebSocketErrorCode})");
        }
        catch (HttpRequestException ex)
        {
            throw new JellyfinRequestException(Operation, $"connect failed ({ex.HttpRequestError})");
        }
    }

    public async Task SendTextAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(_buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await CloseQuietlyAsync(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                throw new JellyfinRequestException(Operation, "message too large");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseQuietlyAsync(WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
        _ws.Dispose();
        _sendLock.Dispose();
    }

    private async Task CloseQuietlyAsync(WebSocketCloseStatus status)
    {
        if (_ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        // A close is a send: ClientWebSocket allows only one send at a time, and the KeepAlive loop may be sending.
        var locked = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            locked = await _sendLock.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (locked)
            {
                await _ws.CloseOutputAsync(status, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Closing is best effort; Dispose aborts the connection anyway.
        }
        finally
        {
            if (locked)
            {
                _sendLock.Release();
            }
        }
    }
}

public sealed class ClientSessionSocketFactory : ISessionSocketFactory
{
    public ISessionSocket Create() => new ClientSessionSocket();
}
