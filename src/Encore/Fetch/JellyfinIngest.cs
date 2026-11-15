using Encore.Jellyfin;
using Microsoft.Extensions.Logging;

namespace Encore.Fetch;

public enum IngestOutcome
{
    /// <summary>The song is in Jellyfin; <see cref="IngestResult.ItemId"/> is set.</summary>
    Ready,

    /// <summary>Not seen within <see cref="FetchLimits.IngestWait"/> (fail reason ingest_timeout).</summary>
    Timeout,

    /// <summary>The radio dir is in no Jellyfin music library (availability radio_dir_not_in_library). Nothing was sent or polled.</summary>
    NotInLibrary,

    /// <summary>The radio dir is in a music library, but Jellyfin has not scanned it yet (a Scan All Libraries is needed). Nothing was sent or polled.</summary>
    NotScanned,
}

public sealed record IngestResult(IngestOutcome Outcome, Guid? ItemId, TimeSpan Waited)
{
    public bool IsReady => Outcome == IngestOutcome.Ready;
}

/// <summary>
/// Where the radio dir sits in Jellyfin. <see cref="LibraryItemId"/> is the library to poll, null when there is none.
/// <see cref="Scanned"/> is false when Jellyfin lists neither the radio dir nor a parent of it among its physical paths
/// (a library added after the folder was empty is not walked until Scan All Libraries runs); true when unknown.
/// </summary>
public sealed record LibraryCheck(bool InLibrary, Guid? LibraryItemId, string? LibraryName, IReadOnlyList<string> Locations, bool Scanned = true);

/// <summary>Reports an imported file to Jellyfin and waits until Jellyfin lists it (v0.2 design section 8.2).</summary>
public interface IJellyfinIngest
{
    /// <summary>Is the radio Jellyfin dir inside a library of type music? Logs <c>ingest.library_check</c>.</summary>
    Task<LibraryCheck> CheckLibraryAsync(CancellationToken ct = default);

    /// <summary>
    /// Tells Jellyfin about <paramref name="jellyfinPath"/> (the path as Jellyfin sees it) and waits up to 3 minutes,
    /// polling every 5 s, for an Audio item with exactly that path. Jellyfin errors while reporting or checking the
    /// library are thrown; errors while polling are logged and polling goes on.
    /// </summary>
    Task<IngestResult> IngestAsync(string jobId, string jellyfinPath, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IJellyfinIngest"/> over <see cref="IJellyfinClient"/>. One shared poll loop runs while any path is awaited:
/// each tick makes one GET per library however many paths wait, and matches rows by exact, case-sensitive path.
/// The first poll runs at once, then every 5 s.
/// </summary>
public sealed class JellyfinIngest : IJellyfinIngest, IDisposable
{
    /// <summary>How many newest songs one poll asks for.</summary>
    internal const int PollLimit = 50;

    private sealed class Waiter(string jobId, string path, Guid library, DateTimeOffset started)
    {
        public string JobId { get; } = jobId;

        public string Path { get; } = path;

        public Guid Library { get; } = library;

        public DateTimeOffset Started { get; } = started;

        public TaskCompletionSource<IngestResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IJellyfinClient _jellyfin;
    private readonly string _radioDir;
    private readonly ILogger<JellyfinIngest> _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private bool _looping;

    /// <param name="jellyfin">The Jellyfin client.</param>
    /// <param name="radioJellyfinDir">ENCORE_RADIO_JELLYFIN_DIR: the radio dir as Jellyfin sees it.</param>
    /// <param name="log">Logger.</param>
    /// <param name="time">Clock for the poll interval and the 3 minute wait (tests pass a manual one).</param>
    public JellyfinIngest(IJellyfinClient jellyfin, string radioJellyfinDir, ILogger<JellyfinIngest> log, TimeProvider? time = null)
    {
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        ArgumentException.ThrowIfNullOrWhiteSpace(radioJellyfinDir);
        _radioDir = radioJellyfinDir;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
    }

    public async Task<LibraryCheck> CheckLibraryAsync(CancellationToken ct = default)
    {
        var folders = await _jellyfin.GetVirtualFoldersAsync(ct).ConfigureAwait(false);
        var check = Evaluate(folders, _radioDir);
        if (check.InLibrary)
        {
            check = check with { Scanned = await IsScannedAsync(ct).ConfigureAwait(false) };
        }

        var locations = string.Join(", ", check.Locations);
        _log.IngestLibraryCheck(check.InLibrary ? LogLevel.Information : LogLevel.Warning, check.InLibrary, check.LibraryName, locations);
        if (!check.Scanned)
        {
            _log.IngestNotScanned(_radioDir);
        }

        return check;
    }

    /// <summary>
    /// Does Jellyfin list the radio dir, or a parent of it, among its physical paths? A failed call counts as scanned:
    /// without an answer the ingest is tried as before, and its own timeout still ends the job.
    /// </summary>
    private async Task<bool> IsScannedAsync(CancellationToken ct)
    {
        try
        {
            var paths = await _jellyfin.GetPhysicalPathsAsync(ct).ConfigureAwait(false);
            return paths.Any(p => IsSameOrParent(p, _radioDir));
        }
        catch (JellyfinException e)
        {
            _log.IngestPhysicalPathsFailed(e.Message);
            return true;
        }
    }

    /// <summary>
    /// The first library of collection type music with a location equal to the radio dir or a parent of it,
    /// compared by whole path segments (so /music-radio2 does not hold /music-radio).
    /// </summary>
    internal static LibraryCheck Evaluate(IReadOnlyList<VirtualFolderInfo> folders, string radioDir)
    {
        var music = folders.Where(f => string.Equals(f.CollectionType, "music", StringComparison.OrdinalIgnoreCase)).ToList();
        var allLocations = music.SelectMany(f => f.Locations ?? []).ToList();
        foreach (var folder in music)
        {
            if ((folder.Locations ?? []).Any(l => IsSameOrParent(l, radioDir)))
            {
                return new LibraryCheck(true, folder.ItemId, folder.Name, allLocations);
            }
        }

        return new LibraryCheck(false, null, null, allLocations);
    }

    private static bool IsSameOrParent(string location, string dir)
    {
        var parent = Trim(location);
        var child = Trim(dir);
        if (parent.Length == 0)
        {
            return true; // "/" holds everything
        }

        return child.Equals(parent, StringComparison.Ordinal)
            || child.StartsWith(parent + "/", StringComparison.Ordinal);
    }

    private static string Trim(string path) => path.Replace('\\', '/').TrimEnd('/');

    public async Task<IngestResult> IngestAsync(string jobId, string jellyfinPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jellyfinPath);

        var check = await CheckLibraryAsync(ct).ConfigureAwait(false);
        if (check.LibraryItemId is not { } library)
        {
            return new IngestResult(IngestOutcome.NotInLibrary, null, TimeSpan.Zero);
        }

        if (!check.Scanned)
        {
            return new IngestResult(IngestOutcome.NotScanned, null, TimeSpan.Zero);
        }

        await _jellyfin.ReportMediaUpdatedAsync([jellyfinPath], ct).ConfigureAwait(false);
        _log.IngestReported(jobId, jellyfinPath);

        var waiter = new Waiter(jobId, jellyfinPath, library, _time.GetUtcNow());
        lock (_gate)
        {
            _waiters.Add(waiter);
            if (!_looping)
            {
                _looping = true;
                _ = Task.Run(LoopAsync);
            }
        }

        await using var registration = ct.Register(() => waiter.Done.TrySetCanceled(ct)).ConfigureAwait(false);
        try
        {
            return await waiter.Done.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (true)
            {
                List<Waiter> open;
                lock (_gate)
                {
                    open = _waiters.Where(w => !w.Done.Task.IsCompleted).ToList();
                    if (open.Count == 0)
                    {
                        _looping = false;
                        return;
                    }
                }

                foreach (var group in open.GroupBy(w => w.Library))
                {
                    await PollAsync(group.Key, group.ToList()).ConfigureAwait(false);
                }

                await Task.Delay(FetchLimits.IngestPoll, _time, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed: nobody should keep waiting.
            lock (_gate)
            {
                _looping = false;
                foreach (var w in _waiters)
                {
                    w.Done.TrySetCanceled();
                }
            }
        }
    }

    private async Task PollAsync(Guid library, List<Waiter> group)
    {
        IReadOnlyList<BaseItemDto>? items = null;
        try
        {
            items = await _jellyfin.GetRecentAudioAsync(library, PollLimit, _stop.Token).ConfigureAwait(false);
        }
        catch (JellyfinException e)
        {
            _log.IngestPollFailed(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            // Any other failure must not end the shared loop: it is also what times waiters out, so a dead loop would
            // leave every later ingest waiting until shutdown. Kind only; the message could hold a URL.
            _log.IngestPollFailed(e.GetType().Name);
        }

        var now = _time.GetUtcNow();
        foreach (var waiter in group)
        {
            var waited = now - waiter.Started;
            var hit = items?.FirstOrDefault(i => string.Equals(i.Path, waiter.Path, StringComparison.Ordinal));
            if (hit is not null)
            {
                if (waiter.Done.TrySetResult(new IngestResult(IngestOutcome.Ready, hit.Id, waited)))
                {
                    _log.IngestReady(waiter.JobId, hit.Id, (long)waited.TotalMilliseconds);
                }
            }
            else if (waited >= FetchLimits.IngestWait)
            {
                if (waiter.Done.TrySetResult(new IngestResult(IngestOutcome.Timeout, null, waited)))
                {
                    _log.IngestTimeout(waiter.JobId, waiter.Path, (long)waited.TotalMilliseconds);
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
