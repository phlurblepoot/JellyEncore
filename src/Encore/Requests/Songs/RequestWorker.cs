namespace Encore.Requests.Songs;

/// <summary>
/// Runs the song-request lifecycle (v0.3 design 6.3): <see cref="RequestService.Tick"/> every 2 s. The first pass after a
/// start submits every active request again (log 9290). Notifications are sent after the pass, outside the service's
/// lock. An unexpected error logs <c>request.worker_error</c> and the loop goes on.
/// </summary>
public sealed class RequestWorker : BackgroundService
{
    public static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(2);

    private readonly RequestService _service;
    private readonly IRequestNotifier _notifier;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _tick;

    public RequestWorker(RequestService service, IRequestNotifier notifier, ILogger<RequestWorker> logger, TimeProvider? time = null, TimeSpan? tick = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _tick = tick ?? DefaultTick;
    }

    /// <summary>The start pass: submits every active request again and logs how many. Public for tests.</summary>
    public async Task<int> ResubmitAsync(CancellationToken ct)
    {
        var result = RunPass();
        _log.Resubmitted(result.Submitted);
        await NotifyAsync(result.Notify, ct).ConfigureAwait(false);
        return result.Submitted;
    }

    /// <summary>One pass and its notifications. Public for tests.</summary>
    public async Task<RequestTickResult> TickAsync(CancellationToken ct)
    {
        var result = RunPass();
        await NotifyAsync(result.Notify, ct).ConfigureAwait(false);
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting (the fetch side's first checks) before the first pass.
        await Task.Yield();
        await ResubmitAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_tick, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private RequestTickResult RunPass()
    {
        try
        {
            return _service.Tick();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.WorkerError("tick", ex.GetType().Name);
            return new RequestTickResult(0, []);
        }
    }

    private async Task NotifyAsync(IReadOnlyList<Store.RequestRow> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            try
            {
                await _notifier.NotifyAsync(row, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.WorkerError("notify", ex.GetType().Name);
            }
        }
    }
}
