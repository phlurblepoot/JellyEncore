using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encore.Lidarr;

/// <summary>What /health shows for Lidarr (v03 design section 12). Never holds the key.</summary>
public sealed record LidarrState(
    bool Configured,
    bool? Reachable,
    string? Version,
    DateTimeOffset? LastOkUtc,
    string? LastError,
    bool KeyRejected);

/// <summary>
/// Asks Lidarr's system/status every <see cref="LidarrClientOptions.StatusInterval"/> (60 s) and keeps the result as
/// <see cref="State"/> for /health and for the request code (<c>lidarr_unreachable</c>, <c>lidarr_key_rejected</c>).
/// Logs 9300 when reachable, version or key state change, 9320 when the key is rejected, 9310 for other failures.
/// </summary>
public sealed class LidarrStatusMonitor : BackgroundService
{
    private readonly ILidarrClient _client;
    private readonly LidarrClientOptions _options;
    private readonly ILogger<LidarrStatusMonitor> _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private LidarrState _state = new(true, null, null, null, null, false);

    public LidarrStatusMonitor(ILidarrClient client, LidarrClientOptions options, ILogger<LidarrStatusMonitor> log, TimeProvider? time = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The last check. Before the first check Reachable is null.</summary>
    public LidarrState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>One check; never throws except for cancellation.</summary>
    public async Task CheckOnceAsync(CancellationToken ct)
    {
        LidarrState next;
        var previous = State;
        try
        {
            var status = await _client.GetStatusAsync(ct).ConfigureAwait(false);
            next = new LidarrState(true, true, status.Version, _time.GetUtcNow(), null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LidarrAuthException e)
        {
            next = previous with { Reachable = true, LastError = $"key rejected (HTTP {e.StatusCode})", KeyRejected = true };
            _log.LidarrAuthFailed(e.Operation, e.StatusCode ?? 0);
        }
        catch (LidarrException e)
        {
            next = previous with { Reachable = false, LastError = e.Message, KeyRejected = false };
            _log.LidarrRequestFailed(e.Operation, e.StatusCode, e.Message);
        }

        lock (_gate)
        {
            _state = next;
        }

        if (previous.Reachable != next.Reachable || previous.Version != next.Version || previous.KeyRejected != next.KeyRejected)
        {
            _log.LidarrStateChanged(next.Reachable == true, next.Version, next.KeyRejected);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.StatusInterval, _time);
        try
        {
            do
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
