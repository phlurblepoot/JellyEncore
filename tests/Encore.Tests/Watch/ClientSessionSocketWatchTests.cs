using System.Net.WebSockets;
using System.Text;
using Encore.Jellyfin;
using Encore.Watch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Encore.Tests.Watch;

/// <summary>The real <see cref="ClientSessionSocket"/> against a tiny local websocket server on 127.0.0.1.</summary>
public sealed class ClientSessionSocketWatchTests : IAsyncLifetime
{
    private const string GoodAuth = "MediaBrowser Token=\"good\", Client=\"Encore\", Device=\"Encore\", DeviceId=\"encore-0123456789ab\", Version=\"0.1.0\"";

    private WebApplication? _app;
    private Uri _socketUrl = null!;
    private readonly TaskCompletionSource<string> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? SeenAuthorization { get; private set; }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/socket", async context =>
        {
            SeenAuthorization = context.Request.Headers.Authorization;
            if (SeenAuthorization != GoodAuth)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            var first = await ws.ReceiveAsync(buffer, CancellationToken.None);
            _received.TrySetResult(Encoding.UTF8.GetString(buffer, 0, first.Count));

            // One message split over two frames, larger than the client's 16 KB buffer.
            var big = """{"MessageType":"Other","Data":""" + "\"" + new string('a', 40_000) + "\"}";
            var bytes = Encoding.UTF8.GetBytes(big);
            await ws.SendAsync(bytes.AsMemory(0, 100), WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
            await ws.SendAsync(bytes.AsMemory(100), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        });
        await _app.StartAsync();
        var http = new Uri(_app.Urls.First());
        _socketUrl = new Uri($"ws://{http.Authority}/socket");
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connects_with_the_header_sends_text_and_reassembles_fragments_until_close()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var socket = new ClientSessionSocket();

        await socket.ConnectAsync(_socketUrl, GoodAuth, timeout.Token);
        await socket.SendTextAsync(SessionMessage.SessionsStart, timeout.Token);

        Assert.Equal(GoodAuth, SeenAuthorization);
        Assert.Equal(SessionMessage.SessionsStart, await _received.Task.WaitAsync(timeout.Token));
        var message = await socket.ReceiveTextAsync(timeout.Token);
        Assert.NotNull(message);
        Assert.Equal(40_000 + 33, message.Length);
        Assert.Equal(SessionMessageKind.Other, SessionMessage.Parse(message).Kind);
        Assert.Null(await socket.ReceiveTextAsync(timeout.Token));
        Assert.Equal("NormalClosure", socket.CloseStatus);
        Assert.Equal("bye", socket.CloseDescription);
    }

    [Fact]
    public async Task A_401_upgrade_throws_an_auth_exception_without_the_key()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var socket = new ClientSessionSocket();

        var ex = await Assert.ThrowsAsync<JellyfinAuthException>(
            () => socket.ConnectAsync(_socketUrl, "MediaBrowser Token=\"wrong-key\"", timeout.Token));

        Assert.Equal(401, ex.StatusCode);
        Assert.DoesNotContain("wrong-key", ex.ToString());
    }

    [Fact]
    public async Task A_refused_connection_is_a_request_exception()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var closedPort = _socketUrl.Port;
        await DisposeAsync();
        _app = null;
        await using var socket = new ClientSessionSocket();

        var ex = await Assert.ThrowsAsync<JellyfinRequestException>(
            () => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{closedPort}/socket"), GoodAuth, timeout.Token));

        Assert.Null(ex.StatusCode);
        Assert.StartsWith("socket failed: connect failed", ex.Message);
    }
}
