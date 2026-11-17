using System.Diagnostics;
using Encore.Config;
using Encore.Store;
using Microsoft.Data.Sqlite;

namespace Encore.Retention;

/// <summary>One finished pass: what was decided and the signals it was decided on. The report is built from it.</summary>
public sealed record PassResult(
    string PassId,
    DateTimeOffset AtUtc,
    RetentionMode Mode,
    PlannerSettings Settings,
    IReadOnlyList<PlannedRow> Rows,
    IReadOnlyList<string> Untracked,
    SignalsSnapshot Signals,
    long DurationMs)
{
    public bool DryRun => Mode != RetentionMode.Live;

    public int Count(string decision) => decision == RetentionDecision.Untracked ? Untracked.Count : Rows.Count(r => r.Decision == decision);
}

/// <summary>
/// One decision pass (v04 §3, §8): read the signals, plan every ready row, record the first keep signal at once (dry run too: keeping takes
/// nothing away), write one <c>retention_decisions</c> line per row and log them. It never deletes, moves or writes to Jellyfin; the
/// worker (T130) acts on <see cref="PassResult"/> in live mode. The latest result is kept in memory for <c>/retention/report</c>.
/// </summary>
public sealed class RetentionPass
{
    private readonly FetchStore _fetch;
    private readonly RetentionStore _retention;
    private readonly RetentionSignals _signals;
    private readonly EncoreOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionPass> _log;
    private readonly Func<TrackedFile, string?>? _target;
    private readonly Func<TrackedFile, string?>? _libraryCopy;
    private readonly object _gate = new();
    private PassResult? _latest;

    /// <param name="promotionTarget">The library path a promotion would use, for the report; null shows none (the promoter, T129, supplies it).</param>
    /// <param name="libraryCopy">The Jellyfin path of the main library's copy of a song (T141, <see cref="LibraryCopy.PathFinder"/>); a row that
    /// would be promoted while one exists is decided dedupe. Null never decides dedupe.</param>
    public RetentionPass(
        FetchStore fetch, RetentionStore retention, RetentionSignals signals, EncoreOptions options, TimeProvider time,
        ILogger<RetentionPass> log, Func<TrackedFile, string?>? promotionTarget = null,
        Func<TrackedFile, string?>? libraryCopy = null)
    {
        _fetch = fetch;
        _retention = retention;
        _signals = signals;
        _options = options;
        _time = time;
        _log = log;
        _target = promotionTarget;
        _libraryCopy = libraryCopy;
    }

    public EncoreOptions Options => _options;

    /// <summary>The newest finished pass, or null before the first.</summary>
    public PassResult? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public async Task<PassResult> RunAsync(CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var now = _time.GetUtcNow();
        var passId = "p" + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var mode = _options.RetentionMode;
        var dryRun = mode != RetentionMode.Live;
        var modeText = mode == RetentionMode.Live ? "live" : "dry_run";
        var settings = new PlannerSettings(_options.RetentionDays, _options.RetentionUploads, _options.RetentionPlays);

        var rows = _fetch.ReadyForRetention();
        _log.PassStarted(passId, modeText, rows.Count);
        var inProgress = _retention.ActivePromotions().Select(p => p.FetchedFileId).ToHashSet();
        var itemIds = rows.Select(r => Guid.TryParse(r.JellyfinItemId, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var signals = await _signals.ReadAsync(itemIds, ct).ConfigureAwait(false);

        var plan = RetentionPlanner.Plan(rows, inProgress, signals, now, settings, _target, _libraryCopy);
        foreach (var p in plan)
        {
            Record(passId, now, dryRun, p);
        }

        var known = SafePaths();
        IReadOnlyList<string> untracked = known is null ? [] : UntrackedFiles.Find(_options.RadioDir, known);
        foreach (var path in untracked)
        {
            _log.Untracked(path);
            Store("decision", () => _retention.AddDecision(new RetentionDecisionRecord(passId, now, RetentionDecision.Untracked, dryRun, null, path)));
        }

        Store("prune", () => _retention.PruneDecisions(now));
        var result = new PassResult(passId, now, mode, settings, plan, untracked, signals, watch.ElapsedMilliseconds);
        _log.PassDone(passId, modeText, result.Count(RetentionDecision.Promote), result.Count(RetentionDecision.Dedupe), result.Count(RetentionDecision.Keep), result.Count(RetentionDecision.Delete),
            result.Count(RetentionDecision.Wait), result.Count(RetentionDecision.Skip), untracked.Count, result.DurationMs);
        lock (_gate)
        {
            _latest = result;
        }

        return result;
    }

    private void Record(string passId, DateTimeOffset now, bool dryRun, PlannedRow p)
    {
        if (p.RecordKeepAs is { } reason && Store("keep", () => _fetch.MarkKept(p.Row.Id, reason, now)))
        {
            _log.Kept(p.Row.Id, reason);
        }

        Store("decision", () => _retention.AddDecision(new RetentionDecisionRecord(
            passId, now, p.Decision, dryRun, p.Row.Id, p.Row.Path, p.Reason ?? p.WouldDelete, Math.Round(p.AgeDays, 2), p.Uploads, p.Plays, p.TargetPath)));
        if (p.Decision != RetentionDecision.Wait)
        {
            _log.Decision(passId, p.Row.Id, p.Row.Artist, p.Row.Title, p.Decision, p.Reason, Math.Round(p.AgeDays, 1), p.Uploads, p.Plays, p.TargetPath, dryRun);
        }
    }

    /// <summary>The known paths, or null when the store cannot say (then no file is called untracked: a wrong list would blame every file).</summary>
    private IReadOnlyList<string>? SafePaths()
    {
        try
        {
            return _fetch.NonFailedPaths();
        }
        catch (Exception ex) when (ex is SqliteException or StoreException)
        {
            _log.StoreFailed("known_paths", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Runs one store write; a store failure is logged (8900) and does not stop the pass.</summary>
    private T? Store<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is SqliteException or StoreException)
        {
            _log.StoreFailed(operation, ex.GetType().Name);
            return default;
        }
    }
}
