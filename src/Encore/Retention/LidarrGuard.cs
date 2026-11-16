using Encore.Lidarr;

namespace Encore.Retention;

/// <summary>Why promotions are paused (log 8590 and the /health warning).</summary>
public static class PromotionPause
{
    /// <summary>A Lidarr root folder's default monitor option is not <c>none</c>: an auto-added artist would get albums monitored.</summary>
    public const string LidarrDefaults = "lidarr_defaults";

    /// <summary>Lidarr's Write Audio Tags is <c>allFiles</c> or <c>sync</c>: an in-place import would retag the promoted file.</summary>
    public const string LidarrTags = "lidarr_tags";

    /// <summary>Lidarr is configured but its settings could not be read, so the two guards above cannot be judged.</summary>
    public const string LidarrUnreadable = "lidarr_unreadable";

    /// <summary>The library has less than the free-space floor (20 GB) left after the copy.</summary>
    public const string DiskLow = "disk_low";

    /// <summary>The library dir or its staging folder cannot be written.</summary>
    public const string LibraryNotWritable = "library_not_writable";
}

/// <summary>What <see cref="LidarrGuard.CheckAsync"/> found. <see cref="Checked"/> is false when Lidarr is not configured (the guards are skipped).</summary>
public sealed record LidarrGuardResult(bool Checked, bool Ok, string? Reason, string? Detail)
{
    public static LidarrGuardResult NotConfigured { get; } = new(false, true, null, null);
}

/// <summary>
/// The Lidarr guards of a promotion (v04 §7.5) [R5]. Lidarr is not told about promoted songs; it finds them with its folder watcher and
/// imports them in place. Promotions pause while that could do harm: a root folder default monitor option other than <c>none</c>, or a
/// Write Audio Tags setting that retags existing files (<c>allFiles</c>, <c>sync</c>; <c>no</c> and <c>newFiles</c> pass). A failed read
/// pauses too, since neither guard can then be judged. <see cref="RunningImportsAsync"/> serves the courtesy wait before a file is placed.
/// </summary>
public sealed class LidarrGuard
{
    /// <summary>Commands whose <c>queued</c> or <c>started</c> state makes the <c>placed</c> step wait.</summary>
    public static readonly IReadOnlySet<string> ImportCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DownloadedAlbumsScan", "ManualImport", "RescanFolders", "RenameFiles", "RenameArtist", "RetagFiles", "RetagArtist",
    };

    private readonly ILidarrClient? _lidarr;
    private readonly IRetentionLidarr? _retention;
    private readonly object _gate = new();
    private LidarrGuardResult? _last;

    /// <summary>Both null (ENCORE_LIDARR_URL unset) skips every guard.</summary>
    public LidarrGuard(ILidarrClient? lidarr, IRetentionLidarr? retention)
    {
        _lidarr = lidarr;
        _retention = retention;
    }

    public bool Configured => _lidarr is not null && _retention is not null;

    /// <summary>The newest result, or null before the first check (for /health).</summary>
    public LidarrGuardResult? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public async Task<LidarrGuardResult> CheckAsync(CancellationToken ct)
    {
        var result = await EvaluateAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _last = result;
        }

        return result;
    }

    /// <summary>The running import or rescan commands (name list), empty when none or Lidarr is not configured; null when the read failed.</summary>
    public async Task<IReadOnlyList<string>?> RunningImportsAsync(CancellationToken ct)
    {
        if (_retention is null)
        {
            return [];
        }

        try
        {
            var commands = await _retention.GetCommandsAsync(ct).ConfigureAwait(false);
            return commands.Where(c => c.IsRunning && ImportCommands.Contains(c.Name)).Select(c => c.Name).ToList();
        }
        catch (Exception e) when (e is LidarrException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<LidarrGuardResult> EvaluateAsync(CancellationToken ct)
    {
        if (_lidarr is null || _retention is null)
        {
            return LidarrGuardResult.NotConfigured;
        }

        try
        {
            var roots = await _lidarr.GetRootFoldersAsync(ct).ConfigureAwait(false);
            foreach (var root in roots)
            {
                if (!IsNone(root.DefaultMonitorOption) || !IsNone(root.DefaultNewItemMonitorOption))
                {
                    return new LidarrGuardResult(true, false, PromotionPause.LidarrDefaults,
                        $"root folder {root.Path}: monitor {root.DefaultMonitorOption ?? "unknown"}, new items {root.DefaultNewItemMonitorOption ?? "unknown"}");
                }
            }

            var tags = await _retention.GetWriteAudioTagsAsync(ct).ConfigureAwait(false);
            if (LidarrWriteAudioTags.RetagsExistingFiles(tags))
            {
                return new LidarrGuardResult(true, false, PromotionPause.LidarrTags, $"writeAudioTags {tags}");
            }

            return new LidarrGuardResult(true, true, null, null);
        }
        catch (Exception e) when (e is LidarrException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new LidarrGuardResult(true, false, PromotionPause.LidarrUnreadable, e.GetType().Name);
        }
    }

    // A missing option counts as not none: the guard cannot see it, so it does not pass.
    private static bool IsNone(string? option) => string.Equals(option, "none", StringComparison.OrdinalIgnoreCase);
}
