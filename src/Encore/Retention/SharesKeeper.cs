using Encore.Logging;
using Encore.Slskd;
using Encore.Store;
using Microsoft.Extensions.Hosting;

namespace Encore.Retention;

/// <summary>Marks the radio share as changed so the keeper asks slskd for a rescan (v0.4 design section 5.3).</summary>
public interface ISharesDirty
{
    /// <summary>Reason: import, delete or promote.</summary>
    void MarkDirty(string reason);
}

/// <summary>What <see cref="SharesKeeper.RescanIfDueAsync"/> did.</summary>
public enum RescanResult
{
    NotDirty,
    NotShared,
    TooSoon,
    Scanning,
    Failed,
    Started,
}

/// <summary>The answer of <see cref="SharesKeeper.CheckActiveAsync"/>. <see cref="Known"/> is false when it could not be decided (not shared, or slskd unreadable): callers defer.</summary>
public sealed record ActiveUploadCheck(bool Known, int PeerCount)
{
    public bool Active => !Known || PeerCount > 0;
}

/// <summary>
/// Knows whether music-radio is shared on Soulseek (checked every 30 min), asks slskd to rescan when files changed
/// (at most every 6 h, only when shared and idle) and tells whether a radio file is being uploaded right now
/// (v0.4 design sections 5.3 to 5.5).
/// </summary>
public sealed class SharesKeeper : BackgroundService, ISharesDirty
{
    private readonly IRetentionSlskd _slskd;
    private readonly RetentionStore _store;
    private readonly string _radioDir;
    private readonly string _slskdRadioDir;
    private readonly ILogger<SharesKeeper> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private bool _dirty;
    private string _dirtyReason = "import";
    private volatile bool _checked;
    private bool? _lastLogged;
    private SlskdShare? _share;

    public SharesKeeper(
        IRetentionSlskd slskd, RetentionStore store, string radioDir, string slskdRadioDir, ILogger<SharesKeeper> log, TimeProvider? time = null)
    {
        _slskd = slskd ?? throw new ArgumentNullException(nameof(slskd));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _radioDir = Normalize(radioDir);
        _slskdRadioDir = Normalize(slskdRadioDir);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
    }

    public bool Dirty
    {
        get
        {
            lock (_lock)
            {
                return _dirty;
            }
        }
    }

    /// <summary>True when a non-excluded share covers the radio folder (false until the first check).</summary>
    public bool RadioShared => Volatile.Read(ref _share) is not null;

    /// <summary>The radio share found by the last check, or null.</summary>
    public SlskdShare? RadioShare => Volatile.Read(ref _share);

    public DateTimeOffset? LastRescanUtc => _store.GetLastRescan();

    public void MarkDirty(string reason)
    {
        lock (_lock)
        {
            _dirty = true;
            _dirtyReason = reason;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await CheckSharesAsync(stoppingToken).ConfigureAwait(false);
            var lastCheck = _time.GetUtcNow();
            using var timer = new PeriodicTimer(RetentionLimits.RescanCheckEvery, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (_time.GetUtcNow() - lastCheck >= RetentionLimits.SharesCheckEvery)
                {
                    await CheckSharesAsync(stoppingToken).ConfigureAwait(false);
                    lastCheck = _time.GetUtcNow();
                }

                await RescanIfDueAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.WorkerCrashed("retention", $"shares keeper: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Reads the shares and finds the radio share (the soulseek folder, the radio folder or a parent of it, not excluded).</summary>
    public async Task<bool> CheckSharesAsync(CancellationToken ct)
    {
        IReadOnlyList<SlskdShare> shares;
        try
        {
            shares = await _slskd.GetSharesAsync(ct).ConfigureAwait(false);
        }
        catch (SlskdException e)
        {
            _log.UploadsFailed("shares", e.StatusCode, e.Message);
            return RadioShared;
        }

        var soulseek = _slskdRadioDir + "/soulseek";
        var found = shares
            .Where(s => !s.IsExcluded)
            .Select(s => (Share: s, Local: Normalize(s.LocalPath)))
            .Where(x => x.Local == soulseek || x.Local == _slskdRadioDir
                || soulseek.StartsWith(x.Local == "/" ? "/" : x.Local + "/", StringComparison.Ordinal))
            .OrderByDescending(x => x.Local.Length)
            .Select(x => x.Share)
            .FirstOrDefault();

        Volatile.Write(ref _share, found);
        _checked = true;
        var shared = found is not null;
        if (_lastLogged != shared || !shared)
        {
            _log.SharesState(shared ? LogLevel.Information : LogLevel.Warning, shared, shares.Count);
        }

        _lastLogged = shared;
        return shared;
    }

    /// <summary>Asks slskd for a rescan when files changed, music-radio is shared, no scan runs and the last one is 6 h old.</summary>
    public async Task<RescanResult> RescanIfDueAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string reason;
            lock (_lock)
            {
                if (!_dirty)
                {
                    return RescanResult.NotDirty;
                }

                reason = _dirtyReason;
            }

            if (!_checked)
            {
                await CheckSharesAsync(ct).ConfigureAwait(false);
            }

            if (!RadioShared)
            {
                _log.SharesRescanDeferred("not_shared");
                return RescanResult.NotShared;
            }

            var now = _time.GetUtcNow();
            if (_store.GetLastRescan() is { } last && now - last < RetentionLimits.MinRescanGap)
            {
                _log.SharesRescanDeferred("too_soon");
                return RescanResult.TooSoon;
            }

            try
            {
                var state = await _slskd.GetShareStateAsync(ct).ConfigureAwait(false);
                if (state.Scanning)
                {
                    _log.SharesRescanDeferred("scanning");
                    return RescanResult.Scanning;
                }

                await _slskd.RescanSharesAsync(ct).ConfigureAwait(false);
            }
            catch (SlskdException e)
            {
                _log.UploadsFailed("rescan", e.StatusCode, e.Message);
                return RescanResult.Failed;
            }

            _store.SetLastRescan(now);
            lock (_lock)
            {
                _dirty = false;
            }

            _log.SharesRescan(reason);
            return RescanResult.Started;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Is the radio file (an Encore path under the radio folder) being uploaded to a peer now? An active transfer has a state
    /// without "Completed" and a remote filename that equals, ignoring case, the share's remote path plus the file's path
    /// relative to the share (an exact match, not a suffix). Logs 8350 when peers are found.
    /// </summary>
    public async Task<ActiveUploadCheck> CheckActiveAsync(long rowId, string radioPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioPath);
        if (!_checked)
        {
            await CheckSharesAsync(ct).ConfigureAwait(false);
        }

        var share = RadioShare;
        var radio = Normalize(radioPath);
        if (share is null || !radio.StartsWith(_radioDir + "/", StringComparison.Ordinal))
        {
            return new ActiveUploadCheck(false, 0);
        }

        var slskdPath = _slskdRadioDir + radio[_radioDir.Length..];
        var shareLocal = Normalize(share.LocalPath);
        string relative;
        if (shareLocal == "/")
        {
            relative = slskdPath.TrimStart('/');
        }
        else if (slskdPath.StartsWith(shareLocal + "/", StringComparison.Ordinal))
        {
            relative = slskdPath[(shareLocal.Length + 1)..];
        }
        else
        {
            return new ActiveUploadCheck(false, 0);
        }

        var remote = share.RemotePath.Trim('\\', '/').Replace('/', '\\') + "\\" + relative.Replace('/', '\\');
        IReadOnlyList<SlskdUpload> uploads;
        try
        {
            uploads = await _slskd.GetUploadsAsync(ct).ConfigureAwait(false);
        }
        catch (SlskdException e)
        {
            _log.UploadsFailed("uploads", e.StatusCode, e.Message);
            return new ActiveUploadCheck(false, 0);
        }

        var peers = uploads
            .Where(u => u.IsActive && string.Equals(u.Filename.Replace('/', '\\').TrimStart('\\'), remote, StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Username)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (peers > 0)
        {
            _log.UploadsActiveSkip(rowId, peers);
        }

        return new ActiveUploadCheck(true, peers);
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }
}
