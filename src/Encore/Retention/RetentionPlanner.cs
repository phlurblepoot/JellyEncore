using Encore.Store;

namespace Encore.Retention;

/// <summary>The retention thresholds the planner needs (ENCORE_RETENTION_DAYS, _UPLOADS, _PLAYS).</summary>
public sealed record PlannerSettings(int Days, int Uploads, int Plays);

/// <summary>Why a delete decision became a skip.</summary>
public static class RetentionSkip
{
    /// <summary>A signal read failed this pass, so nothing may be deleted (invariant 10).</summary>
    public const string SignalsFailed = "signals_failed";
}

/// <summary>
/// The planner's verdict on one row. <see cref="RecordKeepAs"/> is set when the row must be marked kept now (first keep signal, v04 §3);
/// <see cref="WouldDelete"/> is the delete reason (age or uploads) of a row that is due but skipped.
/// </summary>
public sealed record PlannedRow(
    TrackedFile Row,
    string Decision,
    string? Reason,
    string? RecordKeepAs,
    double AgeDays,
    int Uploads,
    int Plays,
    double? DaysLeft,
    int? UploadsLeft,
    int? PlaysLeft,
    string? TargetPath,
    string? WouldDelete = null);

/// <summary>
/// The v04 §3 table as a pure function: rows and signals in, one decision per row out. Nothing is read or written here.
/// Order: promotion in progress, requested, favorite, counted playlist, plays, already kept, age, uploads, wait. A row that would be
/// promoted while the main library already has the song (<c>libraryCopy</c> names it) is decided <c>dedupe</c> instead (T141), with the
/// same reason and the library copy's path as its target.
/// </summary>
public static class RetentionPlanner
{
    public const string ReasonRequested = "requested";
    public const string ReasonFavorite = "favorite";
    public const string ReasonPlaylist = "playlist";
    public const string ReasonPlays = "plays";
    public const string ReasonResume = "resume";
    public const string ReasonAge = "age";
    public const string ReasonUploads = "uploads";

    /// <summary>
    /// Plans every row in state <c>ready</c>; other states are not considered. <paramref name="promotionsInProgress"/> are the
    /// fetched_files ids that have an unfinished promotion. <paramref name="target"/> gives the library path a promotion would use (report only).
    /// <paramref name="libraryCopy"/> gives the Jellyfin path of the main library's copy of the song, or null when it has none.
    /// </summary>
    public static IReadOnlyList<PlannedRow> Plan(
        IEnumerable<TrackedFile> rows,
        IReadOnlySet<long> promotionsInProgress,
        SignalsSnapshot signals,
        DateTimeOffset now,
        PlannerSettings settings,
        Func<TrackedFile, string?>? target = null,
        Func<TrackedFile, string?>? libraryCopy = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(promotionsInProgress);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(settings);
        var plan = new List<PlannedRow>();
        foreach (var row in rows)
        {
            if (row.State != FetchState.Ready)
            {
                continue;
            }

            var planned = PlanRow(row, promotionsInProgress, signals, now, settings, target);
            if (planned.Decision == RetentionDecision.Promote && planned.Reason != ReasonResume && libraryCopy?.Invoke(row) is { } copy)
            {
                planned = planned with { Decision = RetentionDecision.Dedupe, TargetPath = copy };
            }

            plan.Add(planned);
        }

        return plan;
    }

    private static PlannedRow PlanRow(
        TrackedFile row, IReadOnlySet<long> inProgress, SignalsSnapshot signals, DateTimeOffset now, PlannerSettings settings, Func<TrackedFile, string?>? target)
    {
        var age = row.ReadyUtc is { } ready ? Math.Max(0, (now - ready).TotalDays) : 0;
        PlannedRow Make(string decision, string? reason, string? recordKeepAs = null, bool withLeft = false, string? wouldDelete = null) => new(
            row, decision, reason, recordKeepAs, age, row.Uploads, row.PlayThroughs,
            withLeft ? settings.Days - age : null,
            withLeft ? settings.Uploads - row.Uploads : null,
            withLeft ? settings.Plays - row.PlayThroughs : null,
            decision == RetentionDecision.Promote ? target?.Invoke(row) : null,
            wouldDelete);

        // The kept time is recorded once, with the first signal that matches.
        string? Record(string reason) => row.KeptUtc is null ? reason : null;

        if (inProgress.Contains(row.Id))
        {
            return Make(RetentionDecision.Promote, ReasonResume);
        }

        if (row.Requested)
        {
            return Make(RetentionDecision.Promote, ReasonRequested, Record(ReasonRequested));
        }

        if (Guid.TryParse(row.JellyfinItemId, out var item))
        {
            if (signals.IsFavorite(item))
            {
                return Make(RetentionDecision.Promote, ReasonFavorite, Record(ReasonFavorite));
            }

            if (signals.InCountedPlaylist(item))
            {
                return Make(RetentionDecision.Promote, ReasonPlaylist, Record(ReasonPlaylist));
            }
        }

        if (row.PlayThroughs >= settings.Plays)
        {
            return Make(RetentionDecision.Promote, ReasonPlays, Record(ReasonPlays));
        }

        if (row.KeptReason is not null)
        {
            return Make(RetentionDecision.Keep, row.KeptReason);
        }

        string? due = age >= settings.Days ? ReasonAge : row.Uploads >= settings.Uploads ? ReasonUploads : null;
        if (due is not null)
        {
            return signals.DeletesAllowed
                ? Make(RetentionDecision.Delete, due)
                : Make(RetentionDecision.Skip, RetentionSkip.SignalsFailed, wouldDelete: due);
        }

        return Make(RetentionDecision.Wait, null, withLeft: true);
    }
}

/// <summary>Finds audio files under the radio folder that no tracked row names (v04 §3 last bullet). Reads the disk only.</summary>
public static class UntrackedFiles
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".aac", ".wma", ".alac", ".ape",
    };

    /// <summary>
    /// Audio files under <c>{radioDir}/soulseek</c> whose path is not in <paramref name="knownPaths"/>. Hidden files and folders
    /// (names starting with a dot, such as the trash) are skipped. A missing folder or an unreadable one gives an empty list.
    /// </summary>
    public static IReadOnlyList<string> Find(string radioDir, IEnumerable<string> knownPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioDir);
        ArgumentNullException.ThrowIfNull(knownPaths);
        var root = Path.Combine(radioDir, "soulseek");
        var known = new HashSet<string>(knownPaths.Select(Normalize), StringComparer.Ordinal);
        var found = new List<string>();
        try
        {
            if (!Directory.Exists(root))
            {
                return found;
            }

            Walk(root, known, found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read lists nothing more: reporting is best effort.
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Walk(string dir, HashSet<string> known, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.') || !AudioExtensions.Contains(Path.GetExtension(name)))
            {
                continue;
            }

            if (!known.Contains(Normalize(file)))
            {
                found.Add(file.Replace('\\', '/'));
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!Path.GetFileName(sub).StartsWith('.'))
            {
                Walk(sub, known, found);
            }
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
