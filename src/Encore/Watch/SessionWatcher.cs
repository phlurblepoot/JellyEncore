using System.Net.WebSockets;
using System.Threading.Channels;
using Encore.Jellyfin;
using Encore.Logging;
using Microsoft.Extensions.Hosting;

namespace Encore.Watch;

/// <summary>
/// Watches Jellyfin sessions (design section 1.3). Holds one <c>/socket</c> connection at a time, sends SessionsStart,
/// keeps it alive, and writes every Sessions push to the snapshot channel. Reconnects with backoff. While the socket
/// is not delivering, one poll loop does GET /Sessions every poll interval instead.
/// </summary>
public sealed class SessionWatcher : BackgroundService
{
    private readonly ISessionSocketFactory _sockets;
    private readonly IJellyfinClient _jellyfin;
    private readonly ChannelWriter<SessionsSnapshot> _output;
    private readonly WatcherSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionWatcher> _log;
    private readonly ReconnectBackoff _backoff;
    private readonly string _logUrl;
    private readonly Lock _gate = new();
    private WatcherStatus _status;

    public SessionWatcher(
        ISessionSocketFactory sockets,
        IJellyfinClient jellyfin,
        ChannelWriter<SessionsSnapshot> output,
        WatcherSettings settings,
        TimeProvider time,
        ILogger<SessionWatcher> log,
        Func<double>? random = null)
    {
        _sockets = sockets ?? throw new ArgumentNullException(nameof(sockets));
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _backoff = new ReconnectBackoff(settings.BackoffSteps, settings.BackoffResetAfter, settings.BackoffJitter, random);
        _logUrl = settings.SocketUrl.GetLeftPart(UriPartial.Path);
        _status = new WatcherStatus { Since = time.GetUtcNow() };
    }

    /// <summary>The current status, for /health.</summary>
    public WatcherStatus Status => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.PollStarted();
        var poll = PollLoopAsync(stoppingToken);
        try
        {
            await SocketLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task SocketLoopAsync(CancellationToken ct)
    {
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            var (authFailed, uptime) = await RunAttemptAsync(attempt, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            Update(s => s with { State = SocketState.Down, Since = _time.GetUtcNow() });
            SetPollActive(true);

            var delay = _backoff.Next(authFailed, uptime);
            _log.SocketReconnectScheduled(Math.Round(delay.TotalSeconds, 3), attempt + 1);
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            Update(s => s with { Reconnects = s.Reconnects + 1 });
        }
    }

    /// <summary>One connection attempt, start to finish. Everything it creates is disposed before it returns.</summary>
    private async Task<(bool AuthFailed, TimeSpan Uptime)> RunAttemptAsync(int attempt, CancellationToken ct)
    {
        Update(s => s with { State = SocketState.Connecting, Since = _time.GetUtcNow() });
        _log.SocketConnecting(_logUrl, attempt);

        DateTimeOffset? openedAt = null;
        var authFailed = false;
        string closeStatus;
        string? reason;

        var socket = _sockets.Create();
        await using (socket.ConfigureAwait(false))
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                // The timeout covers only the connect and the subscribe; it is disposed before the receive loop starts.
                using (var connectTimeout = new CancellationTokenSource(_settings.ConnectTimeout, _time))
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token, connectTimeout.Token))
                {
                    await socket.ConnectAsync(_settings.SocketUrl, _settings.Authorization, connectCts.Token).ConfigureAwait(false);
                    openedAt = _time.GetUtcNow();
                    await socket.SendTextAsync(SessionMessage.SessionsStart, connectCts.Token).ConfigureAwait(false);
                }

                Update(s => s with { State = SocketState.Subscribed, Since = openedAt.Value, LastError = null });
                _log.SocketSubscribed();

                var receive = ReceiveLoopAsync(socket, attemptCts.Token);
                var keepAlive = KeepAliveLoopAsync(socket, attemptCts.Token);
                var first = await Task.WhenAny(receive, keepAlive).ConfigureAwait(false);
                await attemptCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(receive, keepAlive).ConfigureAwait(false);
                }
                catch
                {
                    // The reason comes from whichever loop ended first; the other one was cancelled.
                }

                ct.ThrowIfCancellationRequested();
                if (first.IsFaulted)
                {
                    (closeStatus, reason) = ("error", Describe(first.Exception!.GetBaseException()));
                }
                else
                {
                    (closeStatus, reason) = (socket.CloseStatus ?? "closed", socket.CloseDescription);
                }
            }
            catch (JellyfinAuthException ex)
            {
                authFailed = true;
                (closeStatus, reason) = ("unauthorized", null);
                _log.JellyfinAuthFailed("/socket", ex.StatusCode);
                Update(s => s with { LastError = "unauthorized" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                (closeStatus, reason) = (openedAt is null ? "connect_failed" : "error", Describe(ex));
                Update(s => s with { LastError = reason });
            }
        }

        var uptime = openedAt is null ? TimeSpan.Zero : _time.GetUtcNow() - openedAt.Value;
        _log.SocketClosed(closeStatus, reason, Math.Round(uptime.TotalSeconds, 1));
        return (authFailed, uptime);
    }

    private async Task ReceiveLoopAsync(ISessionSocket socket, CancellationToken ct)
    {
        while (true)
        {
            var text = await socket.ReceiveTextAsync(ct).ConfigureAwait(false);
            if (text is null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            Update(s => s with { LastMessageAt = now });
            var message = SessionMessage.Parse(text);
            switch (message.Kind)
            {
                case SessionMessageKind.Sessions:
                    Update(s => s with { LastSessionsAt = now });
                    SetPollActive(false);
                    _output.TryWrite(new SessionsSnapshot(now, SnapshotSource.Socket, message.Sessions!));
                    break;
                case SessionMessageKind.ForceKeepAlive:
                    await socket.SendTextAsync(SessionMessage.KeepAlive, ct).ConfigureAwait(false);
                    break;
                case SessionMessageKind.KeepAlive:
                    break;
                default:
                    _log.SocketMessageSkipped(message.MessageType);
                    break;
            }
        }
    }

    private async Task KeepAliveLoopAsync(ISessionSocket socket, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_settings.KeepAliveInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await socket.SendTextAsync(SessionMessage.KeepAlive, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The single poll task. It polls while <see cref="WatcherStatus.PollActive"/>, and reconciles once per quiet period while subscribed.</summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        DateTimeOffset? lastReconcile = null;
        using var timer = new PeriodicTimer(_settings.PollInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var status = Status;
            if (status.PollActive)
            {
                await PollOnceAsync(requireActive: true, ct).ConfigureAwait(false);
                continue;
            }

            if (status.State == SocketState.Subscribed)
            {
                var now = _time.GetUtcNow();
                var quietSince = Latest(status.LastSessionsAt, status.Since, lastReconcile);
                if (now - quietSince >= _settings.ReconcileAfter)
                {
                    lastReconcile = now;
                    await PollOnceAsync(requireActive: false, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task PollOnceAsync(bool requireActive, CancellationToken ct)
    {
        IReadOnlyList<SessionInfoDto> sessions;
        try
        {
            sessions = await _jellyfin.GetSessionsAsync(ct).ConfigureAwait(false);
        }
        catch (JellyfinAuthException ex)
        {
            _log.JellyfinAuthFailed("/Sessions", ex.StatusCode);
            Update(s => s with { LastError = "unauthorized" });
            return;
        }
        catch (JellyfinException ex)
        {
            _log.PollFailed(ex.Message);
            return;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.PollFailed("timeout");
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One bad poll must not end the poll loop for the rest of the run.
            _log.PollFailed(ex.GetType().Name);
            return;
        }

        var now = _time.GetUtcNow();
        Update(s => s with { LastPollAt = now });

        // A socket Sessions message may have arrived while this request was in flight; its data is newer.
        if (requireActive && !Status.PollActive)
        {
            return;
        }

        _output.TryWrite(new SessionsSnapshot(now, SnapshotSource.Poll, sessions));
    }

    private void SetPollActive(bool active)
    {
        var changed = false;
        Update(s =>
        {
            changed = s.PollActive != active;
            return s with { PollActive = active };
        });

        if (!changed)
        {
            return;
        }

        if (active)
        {
            _log.PollStarted();
        }
        else
        {
            _log.PollStopped();
        }
    }

    private void Update(Func<WatcherStatus, WatcherStatus> change)
    {
        lock (_gate)
        {
            Volatile.Write(ref _status, change(_status));
        }
    }

    private static DateTimeOffset Latest(params DateTimeOffset?[] times) =>
        times.Max(t => t ?? DateTimeOffset.MinValue);

    /// <summary>A loggable reason: Jellyfin exception messages are built to be safe; anything else gives its type only.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        JellyfinException j => j.Message,
        WebSocketException w => $"{nameof(WebSocketException)} ({w.WebSocketErrorCode})",
        // Only the connect timeout reaches here: shutdown cancellation is filtered out before Describe is called.
        OperationCanceledException => "timed out",
        _ => ex.GetType().Name,
    };
}
