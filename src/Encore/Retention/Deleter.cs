using System.Globalization;
using Encore.Config;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Store;
using Microsoft.Data.Sqlite;

namespace Encore.Retention;

/// <summary>What one <see cref="Deleter.RunAsync"/> did.</summary>
public sealed record DeleteResult(int Deleted, int Skipped, int Kept, int Failed, int TrashFoldersPurged, int TrashFilesPurged)
{
    public static readonly DeleteResult None = new(0, 0, 0, 0, 0, 0);
}

/// <summary>Why a delete was put off to the next pass (the <c>why</c> of log 8420).</summary>
public static class DeleteSkip
{
    public const string NowPlaying = "now_playing";
    public const string Queued = "queued";
    public const string RecentAdd = "recent_add";
    public const string Uploading = "uploading";
    public const string UploadsUnknown = "uploads_unknown";
    public const string Signal = "signal";
    public const string SignalsFailed = "signals_failed";
    public const string SessionsFailed = "sessions_failed";
    public const string Changed = "changed";
    public const string StoreFailed = "store_failed";
}

/// <summary>
/// The delete path (v04 section 6), live mode only. For the rows a pass decided to delete, at most <c>RetentionMaxDeletes</c> per pass:
/// re-check the signals and the row, skip what is playing, queued, recently added by Encore or being uploaded, move the file to
/// <c>{radio}/.encore-trash/{yyyy-MM-dd}/{rowId}.{ext}</c>, remove empty folders, drop the song from the owned index, mark the row deleted,
/// report one Media/Updated per album folder, mark the shares dirty and purge trash older than 7 days. Only files under
/// <c>{radio}/soulseek/</c> are ever touched (invariant 4); a row that is requested or kept is never deleted (invariant 3).
/// </summary>
public sealed class Deleter
{
    public const string TrashFolder = ".encore-trash";

    private readonly FetchStore _fetch;
    private readonly RetentionStore _retention;
    private readonly RetentionSignals _signals;
    private readonly IJellyfinClient _jellyfin;
    private readonly IOwnedIndex _owned;
    private readonly IUploadCheck _uploads;
    private readonly ISharesDirty _shares;
    private readonly EncoreOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<Deleter> _log;

    public Deleter(
        FetchStore fetch, RetentionStore retention, RetentionSignals signals, IJellyfinClient jellyfin, IOwnedIndex owned,
        IUploadCheck uploads, ISharesDirty shares, EncoreOptions options, TimeProvider time, ILogger<Deleter> log)
    {
        _fetch = fetch;
        _retention = retention;
        _signals = signals;
        _jellyfin = jellyfin;
        _owned = owned;
        _uploads = uploads;
        _shares = shares;
        _options = options;
        _time = time;
        _log = log;
    }

    /// <summary>Acts on the delete decisions of a finished pass. Does nothing unless the mode is live.</summary>
    public async Task<DeleteResult> RunAsync(PassResult pass, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (_options.RetentionMode != RetentionMode.Live)
        {
            return DeleteResult.None;
        }

        var now = _time.GetUtcNow();
        var (purgedFolders, purgedFiles) = PurgeTrash(now);
        var due = pass.Rows.Where(r => r.Decision == RetentionDecision.Delete).ToList();
        if (due.Count == 0)
        {
            return new DeleteResult(0, 0, 0, 0, purgedFolders, purgedFiles);
        }

        int deleted = 0, skipped = 0, kept = 0, failed = 0;
        var folders = new List<string>();

        var itemIds = due.Select(r => Guid.TryParse(r.Row.JellyfinItemId, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        SignalsSnapshot fresh;
        try
        {
            fresh = await _signals.ReadAsync(itemIds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fresh = SignalsSnapshot.Failed(now, new SignalFailure("signals", null, ex.GetType().Name));
        }

        HashSet<Guid>? busy = null;
        HashSet<Guid>? nowPlaying = null;
        if (fresh.DeletesAllowed)
        {
            try
            {
                var sessions = await _jellyfin.GetSessionsAsync(ct).ConfigureAwait(false);
                nowPlaying = sessions.Where(s => s.NowPlayingItem is not null).Select(s => s.NowPlayingItem!.Id).ToHashSet();
                busy = sessions.SelectMany(s => s.NowPlayingQueue ?? []).Select(q => q.Id).ToHashSet();
            }
            catch (Exception ex) when (IsJellyfinFailure(ex, ct))
            {
                // Without the sessions nothing can be shown to be idle: put every delete off.
            }
        }

        var cap = Math.Max(0, _options.RetentionMaxDeletes);
        foreach (var planned in due)
        {
            if (deleted >= cap)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            var why = !fresh.DeletesAllowed ? DeleteSkip.SignalsFailed : nowPlaying is null || busy is null ? DeleteSkip.SessionsFailed : null;
            if (why is not null)
            {
                _log.DeleteSkipped(planned.Row.Id, why);
                skipped++;
                continue;
            }

            var outcome = await TryDeleteAsync(planned, fresh, nowPlaying!, busy!, now, folders, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case Outcome.Deleted:
                    deleted++;
                    break;
                case Outcome.Kept:
                    kept++;
                    break;
                case Outcome.Failed:
                    failed++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        if (deleted > 0)
        {
            await ReportAsync(folders, ct).ConfigureAwait(false);
            _shares.MarkDirty("delete");
        }

        return new DeleteResult(deleted, skipped, kept, failed, purgedFolders, purgedFiles);
    }

    private static bool IsJellyfinFailure(Exception ex, CancellationToken ct) =>
        ex is JellyfinException or HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private enum Outcome
    {
        Deleted,
        Skipped,
        Kept,
        Failed,
    }

    private async Task<Outcome> TryDeleteAsync(
        PlannedRow planned, SignalsSnapshot signals, HashSet<Guid> nowPlaying, HashSet<Guid> queued, DateTimeOffset now, List<string> folders, CancellationToken ct)
    {
        var id = planned.Row.Id;
        TrackedFile? row;
        try
        {
            row = _fetch.GetTracked(id);
        }
        catch (Exception ex) when (ex is SqliteException or StoreException)
        {
            _log.StoreFailed("delete_recheck", ex.GetType().Name);
            _log.DeleteSkipped(id, DeleteSkip.StoreFailed);
            return Outcome.Skipped;
        }

        if (row is null || row.State != FetchState.Ready || string.IsNullOrEmpty(row.Path))
        {
            _log.DeleteSkipped(id, DeleteSkip.Changed);
            return Outcome.Skipped;
        }

        _ = Guid.TryParse(row.JellyfinItemId, out var item);

        // 1. Re-check: any keep signal wins over the delete (invariant 3).
        var keep = row.KeptReason is not null ? row.KeptReason
            : row.Requested ? RetentionPlanner.ReasonRequested
            : item != Guid.Empty && signals.IsFavorite(item) ? RetentionPlanner.ReasonFavorite
            : item != Guid.Empty && signals.InCountedPlaylist(item) ? RetentionPlanner.ReasonPlaylist
            : row.PlayThroughs >= _options.RetentionPlays ? RetentionPlanner.ReasonPlays
            : null;
        if (keep is not null)
        {
            if (row.KeptReason is null)
            {
                try
                {
                    if (_fetch.MarkKept(id, keep, now))
                    {
                        _log.Kept(id, keep);
                    }
                }
                catch (Exception ex) when (ex is SqliteException or StoreException)
                {
                    _log.StoreFailed("delete_keep", ex.GetType().Name);
                }
            }

            _log.DeleteSkipped(id, DeleteSkip.Signal);
            return Outcome.Kept;
        }

        // 2. Skips.
        if (item != Guid.Empty && nowPlaying.Contains(item))
        {
            _log.DeleteSkipped(id, DeleteSkip.NowPlaying);
            return Outcome.Skipped;
        }

        if (item != Guid.Empty && queued.Contains(item))
        {
            _log.DeleteSkipped(id, DeleteSkip.Queued);
            return Outcome.Skipped;
        }

        try
        {
            if (item != Guid.Empty && _retention.WasSentToAnySession(item, now - RetentionLimits.DeleteSkipWindow))
            {
                _log.DeleteSkipped(id, DeleteSkip.RecentAdd);
                return Outcome.Skipped;
            }
        }
        catch (Exception ex) when (ex is SqliteException or StoreException)
        {
            _log.StoreFailed("delete_recent_add", ex.GetType().Name);
            _log.DeleteSkipped(id, DeleteSkip.StoreFailed);
            return Outcome.Skipped;
        }

        var soulseek = Path.GetFullPath(Path.Combine(_options.RadioDir, "soulseek"));
        var source = Path.GetFullPath(row.Path);
        if (!source.StartsWith(soulseek + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            _log.DeleteFailed(id, row.Path, "path is not under the radio soulseek folder");
            return Outcome.Failed;
        }

        var upload = await _uploads.CheckActiveAsync(id, row.Path, ct).ConfigureAwait(false);
        if (upload.Active)
        {
            _log.DeleteSkipped(id, upload.Known ? DeleteSkip.Uploading : DeleteSkip.UploadsUnknown);
            return Outcome.Skipped;
        }

        // 3. Move to the trash (same filesystem, a rename), then tidy empty folders.
        var reason = planned.Reason ?? RetentionPlanner.ReasonAge;
        var trashFile = "";
        try
        {
            if (File.Exists(source))
            {
                var day = Path.Combine(Path.GetFullPath(_options.RadioDir), TrashFolder, now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(day);
                trashFile = Path.Combine(day, id.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(source));
                File.Move(source, trashFile, overwrite: true);
            }

            RemoveEmptyFolders(Path.GetDirectoryName(source)!, soulseek);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.DeleteFailed(id, row.Path, ex.GetType().Name + ": " + ex.Message);
            return Outcome.Failed;
        }

        // 4. Owned index and the row.
        if (item != Guid.Empty)
        {
            _owned.Remove(item);
        }

        try
        {
            _fetch.MarkDeleted(id, reason, now);
        }
        catch (Exception ex) when (ex is SqliteException or StoreException)
        {
            _log.StoreFailed("mark_deleted", ex.GetType().Name);
        }

        var folder = AlbumFolder(row);
        if (folder is not null && !folders.Contains(folder, StringComparer.Ordinal))
        {
            folders.Add(folder);
        }

        _log.DeleteDone(id, row.Path, reason, trashFile);
        return Outcome.Deleted;
    }

    /// <summary>The album folder as Jellyfin sees it, from the row's Jellyfin path or the radio path translated.</summary>
    private string? AlbumFolder(TrackedFile row)
    {
        var jellyfinPath = row.JellyfinPath;
        if (string.IsNullOrEmpty(jellyfinPath) && row.Path is { Length: > 0 } local)
        {
            var radio = _options.RadioDir.Replace('\\', '/').TrimEnd('/');
            var normal = local.Replace('\\', '/');
            if (normal.StartsWith(radio + "/", StringComparison.Ordinal))
            {
                jellyfinPath = _options.RadioJellyfinDir.TrimEnd('/') + normal[radio.Length..];
            }
        }

        if (string.IsNullOrEmpty(jellyfinPath))
        {
            return null;
        }

        var normalized = jellyfinPath.Replace('\\', '/');
        var cut = normalized.LastIndexOf('/');
        return cut > 0 ? normalized[..cut] : null;
    }

    private async Task ReportAsync(List<string> folders, CancellationToken ct)
    {
        if (folders.Count == 0)
        {
            return;
        }

        try
        {
            await _jellyfin.ReportMediaUpdatedAsync(folders, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsJellyfinFailure(ex, ct))
        {
            // The next scan finds the change anyway; only the hurry is lost.
            _log.DeleteFailed(0, folders[0], "Media/Updated: " + ex.GetType().Name);
        }
    }

    /// <summary>Removes empty folders from <paramref name="dir"/> up to, never including, <paramref name="stop"/>.</summary>
    private static void RemoveEmptyFolders(string dir, string stop)
    {
        var current = Path.GetFullPath(dir);
        while (current.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(current))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            Directory.Delete(current);
            current = Path.GetDirectoryName(current) ?? stop;
        }
    }

    /// <summary>Deletes dated trash folders whose last possible file is older than 7 days. Folders with other names are left alone.</summary>
    private (int Folders, int Files) PurgeTrash(DateTimeOffset now)
    {
        var root = Path.Combine(Path.GetFullPath(_options.RadioDir), TrashFolder);
        int folders = 0, files = 0;
        try
        {
            if (!Directory.Exists(root))
            {
                return (0, 0);
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                var lastPossible = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc).AddDays(1));
                if (now - lastPossible < RetentionLimits.TrashKept)
                {
                    continue;
                }

                var count = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
                Directory.Delete(dir, recursive: true);
                folders++;
                files += count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.DeleteFailed(0, root, "trash purge: " + ex.GetType().Name);
        }

        if (folders > 0)
        {
            _log.TrashPurged(folders, files);
        }

        return (folders, files);
    }
}
