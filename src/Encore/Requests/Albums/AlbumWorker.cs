using Encore.Requests.Songs;
using Encore.Store;

namespace Encore.Requests.Albums;

/// <summary>
/// Runs the album-request lifecycle (v0.3 design 7.3): <see cref="AlbumRequester.TickAsync"/> every 2 s (each album is
/// polled at most every 2 min). Notifications go out after the pass. An unexpected error logs <c>request.worker_error</c>
/// and the loop goes on.
/// </summary>
public sealed class AlbumWorker : BackgroundService
{
    private readonly AlbumRequester _albums;
    private readonly IRequestNotifier _notifier;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _tick;

    public AlbumWorker(AlbumRequester albums, IRequestNotifier notifier, ILogger<AlbumWorker> logger, TimeProvider? time = null, TimeSpan? tick = null)
    {
        _albums = albums ?? throw new ArgumentNullException(nameof(albums));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _tick = tick ?? RequestWorker.DefaultTick;
    }

    /// <summary>One pass and its notifications. Public for tests.</summary>
    public async Task<IReadOnlyList<RequestRow>> TickAsync(CancellationToken ct)
    {
        IReadOnlyList<RequestRow> notify;
        try
        {
            notify = await _albums.TickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.WorkerError("album_tick", ex.GetType().Name);
            return [];
        }

        foreach (var row in notify)
        {
            try
            {
                await _notifier.NotifyAsync(row, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.WorkerError("notify", ex.GetType().Name);
            }
        }

        return notify;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_tick, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
