using Encore.Jellyfin;
using Encore.Requests.Auth;
using Microsoft.Extensions.Hosting;

namespace Encore.Requests.Api;

/// <summary>
/// The Jellyfin server id for "Open in Jellyfin" links (design 4, /config). Read once from GET /System/Info/Public at
/// start; null until it has been read. A failed read is retried every <see cref="RequestLimits.ServerInfoRetry"/>.
/// </summary>
public sealed class JellyfinServerInfo : BackgroundService
{
    private readonly IJellyfinClient _jellyfin;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private volatile string? _serverId;

    public JellyfinServerInfo(IJellyfinClient jellyfin, ILogger<JellyfinServerInfo> logger, TimeProvider? time = null)
    {
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The server id, or null until one read has worked.</summary>
    public string? ServerId => _serverId;

    /// <summary>One read. Returns true when the id is known (now or before).</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (_serverId is not null)
        {
            return true;
        }

        try
        {
            var info = await _jellyfin.GetPublicSystemInfoAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(info.Id))
            {
                _logger.JellyfinFailed("GetPublicSystemInfo", 200, "no server id in the answer");
                return false;
            }

            _serverId = info.Id;
            return true;
        }
        catch (JellyfinException ex)
        {
            _logger.JellyfinFailed("GetPublicSystemInfo", ex.StatusCode ?? 0, ex.Message);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!await RefreshAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(RequestLimits.ServerInfoRetry, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
