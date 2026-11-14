using System.Threading.Channels;
using Encore.Jellyfin;
using Encore.Tests.Jellyfin;
using Encore.Watch;

namespace Encore.Tests.Watch;

/// <summary>
/// The watcher against a fake socket and a fake REST client, in real time with millisecond timings.
/// Waits use generous deadlines, so the tests stay stable on slow runners.
/// </summary>
public sealed class SessionWatcherTests : IAsyncLifetime
{
    private const string Authorization = "MediaBrowser Token=\"test-key\", Client=\"Encore\", Device=\"Encore\", DeviceId=\"encore-0123456789ab\", Version=\"0.1.0\"";
    private static readonly Uri SocketUrl = new("ws://jellyfin.test:8096/socket");
    private static readonly string SessionsMessage = $$"""{"MessageType":"Sessions","Data":{{JellyfinFixtures.Sessions}},"MessageId":"m1"}""";

    private readonly FakeSocketFactory _sockets = new();
    private readonly FakeJellyfinClient _client = new();
    private readonly ListLogger<SessionWatcher> _log = new();
    private readonly Channel<SessionsSnapshot> _channel = SessionSnapshots.CreateChannel();
    private SessionWatcher? _watcher;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_watcher is not null)
        {
            await _watcher.StopAsync(CancellationToken.None);
            _watcher.Dispose();
        }
    }

    private async Task<SessionWatcher> StartAsync(
        TimeSpan? poll = null,
        TimeSpan? keepAlive = null,
        IReadOnlyList<TimeSpan>? backoff = null,
        TimeSpan? resetAfter = null,
        TimeSpan? connectTimeout = null)
    {
        var settings = new WatcherSettings
        {
            SocketUrl = SocketUrl,
            Authorization = Authorization,
            PollInterval = poll ?? TimeSpan.FromMinutes(10),
            KeepAliveInterval = keepAlive ?? TimeSpan.FromMinutes(10),
            BackoffSteps = backoff ?? [TimeSpan.FromMilliseconds(20)],
            BackoffResetAfter = resetAfter ?? TimeSpan.FromMinutes(10),
            BackoffJitter = 0,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(15),
        };
        _watcher = new SessionWatcher(_sockets, _client, _channel.Writer, settings, TimeProvider.System, _log);
        await _watcher.StartAsync(CancellationToken.None);
        return _watcher;
    }

    private async Task<SessionsSnapshot> NextSnapshotAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _channel.Reader.ReadAsync(timeout.Token);
    }

    private List<SessionsSnapshot> Drain()
    {
        var list = new List<SessionsSnapshot>();
        while (_channel.Reader.TryRead(out var snapshot))
        {
            list.Add(snapshot);
        }

        return list;
    }

    private double[] ScheduledDelays() =>
        _log.Named("socket.reconnect_scheduled").Select(e => Convert.ToDouble(e.Fields["DelaySeconds"])).ToArray();

    [Fact]
    public async Task Connect_sends_the_auth_header_then_SessionsStart()
    {
        var socket = new FakeSocket();
        _sockets.Then(socket);
        var watcher = await StartAsync();

        await Wait.Until(() => !socket.Sent.IsEmpty, "SessionsStart sent");

        Assert.Equal(SocketUrl, socket.ConnectedUri);
        Assert.Equal(Authorization, socket.Authorization);
        Assert.Equal("""{"MessageType":"SessionsStart","Data":"0,1500"}""", socket.Sent.First());
        await Wait.Until(() => watcher.Status.State == SocketState.Subscribed, "state Subscribed");
        Assert.Single(_log.Named("socket.subscribed"));
        Assert.Equal(SocketUrl.ToString(), _log.Named("socket.connecting").Single().Fields["Url"]);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("test-key"));
    }

    [Fact]
    public async Task A_Sessions_message_becomes_a_socket_snapshot()
    {
        var socket = new FakeSocket();
        _sockets.Then(socket);
        var watcher = await StartAsync();

        socket.Push(SessionsMessage);
        var snapshot = await NextSnapshotAsync();

        Assert.Equal(SnapshotSource.Socket, snapshot.Source);
        Assert.Equal(2, snapshot.Sessions.Count);
        Assert.Equal("e3a7f0c2b1d94a5e8f6c7b2a1d0e9f8c", snapshot.Sessions[0].Id);
        Assert.Equal(2, snapshot.Sessions[0].NowPlayingQueue!.Count);
        Assert.Equal(snapshot.ReceivedAt, watcher.Status.LastSessionsAt);
        Assert.False(watcher.Status.PollActive);
    }

    [Fact]
    public async Task ForceKeepAlive_is_answered_with_KeepAlive()
    {
        var socket = new FakeSocket();
        _sockets.Then(socket);
        await StartAsync();

        socket.Push("""{"MessageType":"ForceKeepAlive","Data":60}""");

        await Wait.Until(() => socket.Sent.Count >= 2, "KeepAlive reply");
        Assert.Equal("""{"MessageType":"KeepAlive"}""", socket.Sent.ElementAt(1));
    }

    [Fact]
    public async Task KeepAlive_is_sent_on_the_interval()
    {
        var socket = new FakeSocket();
        _sockets.Then(socket);
        await StartAsync(keepAlive: TimeSpan.FromMilliseconds(40));

        await Wait.Until(() => socket.Sent.Count(m => m == SessionMessage.KeepAlive) >= 3, "three KeepAlives");
        Assert.Equal(SessionMessage.SessionsStart, socket.Sent.First());
    }

    [Fact]
    public async Task Server_KeepAlive_unknown_and_garbage_messages_are_skipped()
    {
        var socket = new FakeSocket();
        _sockets.Then(socket);
        await StartAsync();

        socket.Push("""{"MessageType":"KeepAlive"}""");
        socket.Push("this is not json");
        socket.Push("""{"MessageType":"UserDataChanged","Data":{"secret":"do-not-log"}}""");
        socket.Push("""{"MessageType":"Sessions","Data":"oops"}""");
        socket.Push(SessionsMessage);

        var snapshot = await NextSnapshotAsync();
        Assert.Equal(SnapshotSource.Socket, snapshot.Source);
        Assert.Empty(Drain());

        var skipped = _log.Named("socket.message_skipped").Select(e => (string)e.Fields["MessageType"]!).ToArray();
        Assert.Equal(["unparseable", "UserDataChanged", "Sessions"], skipped);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("do-not-log") || e.Message.Contains("not json"));
        Assert.Single(socket.Sent); // only SessionsStart: a server KeepAlive needs no reply
    }

    [Fact]
    public async Task A_closed_socket_is_disposed_and_a_fresh_one_connects_after_the_backoff()
    {
        var first = new FakeSocket();
        var second = new FakeSocket();
        _sockets.Then(first).Then(second);
        var watcher = await StartAsync(backoff: [TimeSpan.FromMilliseconds(30)]);
        await Wait.Until(() => watcher.Status.State == SocketState.Subscribed, "first subscribed");

        first.Close("EndpointUnavailable");

        await Wait.Until(() => second.Sent.Contains(SessionMessage.SessionsStart), "second socket subscribed");
        Assert.True(first.Disposed);
        var closed = _log.Named("socket.closed").Single();
        Assert.Equal("EndpointUnavailable", closed.Fields["CloseStatus"]);
        Assert.Equal([0.03], ScheduledDelays());
        await Wait.Until(() => watcher.Status is { State: SocketState.Subscribed, Reconnects: 1 }, "reconnect counted");

        second.Push(SessionsMessage);
        Assert.Equal(SnapshotSource.Socket, (await NextSnapshotAsync()).Source);
    }

    [Fact]
    public async Task Failed_connects_follow_the_backoff_sequence_and_each_socket_is_disposed()
    {
        var watcher = await StartAsync(backoff: [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)]);

        await Wait.Until(() => ScheduledDelays().Length >= 5, "five reconnects scheduled");

        Assert.Equal([0.01, 0.02, 0.04, 0.04, 0.04], ScheduledDelays().Take(5));
        Assert.NotEqual(SocketState.Subscribed, watcher.Status.State);
        Assert.All(_log.Named("socket.closed").Take(5), e => Assert.Equal("connect_failed", e.Fields["CloseStatus"]));
        await Wait.Until(() => _sockets.Created.Count >= 5, "five sockets created");
        Assert.All(_sockets.Created.Take(4), s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task A_401_upgrade_logs_auth_failed_and_waits_at_the_cap()
    {
        _sockets.Then(FakeSocketFactory.Unauthorized());
        var watcher = await StartAsync(backoff: [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(300)]);

        await Wait.Until(() => ScheduledDelays().Length >= 1, "reconnect scheduled");

        var authFailed = _log.Named("jellyfin.auth_failed").Single();
        Assert.Equal("/socket", authFailed.Fields["Endpoint"]);
        Assert.Equal(401, authFailed.Fields["Status"]);
        Assert.Equal(0.3, ScheduledDelays()[0]);
        Assert.Equal("unauthorized", watcher.Status.LastError);
    }

    [Fact]
    public async Task A_connection_that_stayed_up_resets_the_backoff()
    {
        var longLived = new FakeSocket();
        _sockets.Then(FakeSocketFactory.Refused()).Then(longLived);
        var watcher = await StartAsync(
            backoff: [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50)],
            resetAfter: TimeSpan.FromMilliseconds(100));

        await Wait.Until(() => watcher.Status.State == SocketState.Subscribed, "second socket subscribed");
        await Task.Delay(200);
        longLived.Close();

        await Wait.Until(() => ScheduledDelays().Length >= 2, "second reconnect scheduled");
        Assert.Equal([0.01, 0.01], ScheduledDelays().Take(2));
    }

    [Fact]
    public async Task Poll_runs_while_the_socket_is_down_and_stops_on_the_first_socket_Sessions()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new FakeSocket { OnConnect = ct => release.Task.WaitAsync(ct) };
        _sockets.Then(socket);
        _client.Sessions = _ => [new SessionInfoDto { Id = "polled" }];
        var watcher = await StartAsync(poll: TimeSpan.FromMilliseconds(30));

        var polled = await NextSnapshotAsync();
        Assert.Equal(SnapshotSource.Poll, polled.Source);
        Assert.Equal("polled", polled.Sessions.Single().Id);
        Assert.Equal(SocketState.Connecting, watcher.Status.State);
        Assert.True(watcher.Status.PollActive);
        Assert.NotNull(watcher.Status.LastPollAt);

        release.SetResult();
        await Wait.Until(() => watcher.Status.State == SocketState.Subscribed, "subscribed");
        Assert.True(watcher.Status.PollActive); // still polling: an idle server pushes nothing after SessionsStart

        socket.Push(SessionsMessage);
        await Wait.Until(() => !watcher.Status.PollActive, "poll stopped");
        await Task.Delay(100); // let any request that was already in flight finish
        Drain();
        var calls = _client.SessionCalls;

        await Task.Delay(250);
        Assert.Equal(calls, _client.SessionCalls);
        Assert.Empty(Drain());
        Assert.Single(_log.Named("poll.stopped"));
    }

    [Fact]
    public async Task Poll_restarts_when_the_socket_drops()
    {
        var socket = new FakeSocket();
        _sockets.Then(socket);
        var watcher = await StartAsync(poll: TimeSpan.FromMilliseconds(30), backoff: [TimeSpan.FromMinutes(5)]);
        socket.Push(SessionsMessage);
        await Wait.Until(() => !watcher.Status.PollActive, "poll stopped");
        Drain();
        var calls = _client.SessionCalls;

        socket.Close();

        await Wait.Until(() => watcher.Status.State == SocketState.Down, "down");
        await Wait.Until(() => _client.SessionCalls >= calls + 2, "polling again");
        Assert.True(watcher.Status.PollActive);
        Assert.Equal(2, _log.Named("poll.started").Count());
        Assert.Contains(Drain(), s => s.Source == SnapshotSource.Poll);
    }

    [Fact]
    public async Task A_failed_poll_is_logged_and_polling_continues()
    {
        _client.Sessions = call => call == 1
            ? throw new JellyfinRequestException("GetSessions", 500)
            : [new SessionInfoDto { Id = "after-failure" }];
        await StartAsync(poll: TimeSpan.FromMilliseconds(30), backoff: [TimeSpan.FromMinutes(5)]);

        var snapshot = await NextSnapshotAsync();

        Assert.Equal("after-failure", snapshot.Sessions.Single().Id);
        Assert.Equal("GetSessions failed with HTTP 500", _log.Named("poll.failed").First().Fields["Error"]);
    }

    [Fact]
    public async Task Stopping_disposes_the_open_socket()
    {
        var socket = new FakeSocket();
        _sockets.Then(socket);
        var watcher = await StartAsync(keepAlive: TimeSpan.FromMilliseconds(20), poll: TimeSpan.FromMilliseconds(20));
        await Wait.Until(() => watcher.Status.State == SocketState.Subscribed, "subscribed");

        await watcher.StopAsync(CancellationToken.None);

        Assert.True(socket.Disposed);
        Assert.True(watcher.ExecuteTask!.IsCompleted);
        Assert.Single(_sockets.Created);
    }

    [Fact]
    public async Task A_connect_that_never_answers_times_out_and_the_next_attempt_runs()
    {
        var hung = new FakeSocket { OnConnect = ct => Task.Delay(Timeout.Infinite, ct) };
        var good = new FakeSocket();
        _sockets.Then(hung).Then(good);
        var watcher = await StartAsync(connectTimeout: TimeSpan.FromMilliseconds(100));

        await Wait.Until(() => watcher.Status.State == SocketState.Subscribed, "second attempt subscribed");

        var closed = _log.Named("socket.closed").First();
        Assert.Equal("connect_failed", closed.Fields["CloseStatus"]);
        Assert.Equal("timed out", closed.Fields["Reason"]);
        Assert.True(hung.Disposed);
    }
}
