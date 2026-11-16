using System.Globalization;
using Encore.Logging;
using Encore.Slskd;
using Encore.Store;
using Microsoft.Extensions.Hosting;

namespace Encore.Retention;

/// <summary>
/// Counts completed uploads of radio files from slskd's events API (v0.4 design section 5.2). Every 15 minutes it pages
/// back to its watermark minus 1 h (at most 50 pages), keeps the radio paths and records one row per transfer id.
/// </summary>
public sealed class SlskdEvents : BackgroundService
{
    public const int PageSize = 100;

    private static readonly TimeSpan Overlap = TimeSpan.FromHours(1);

    private readonly IRetentionSlskd _slskd;
    private readonly FetchStore _fetch;
    private readonly RetentionStore _store;
    private readonly string _radioDir;
    private readonly string _slskdRadioDir;
    private readonly ILogger<SlskdEvents> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SlskdEvents(
        IRetentionSlskd slskd, FetchStore fetch, RetentionStore store, string radioDir, string slskdRadioDir,
        ILogger<SlskdEvents> log, TimeProvider? time = null)
    {
        _slskd = slskd ?? throw new ArgumentNullException(nameof(slskd));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _radioDir = Normalize(radioDir);
        _slskdRadioDir = Normalize(slskdRadioDir);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>When the last read finished, or null (for /health).</summary>
    public DateTimeOffset? LastReadUtc { get; private set; }

    /// <summary>True when the last read failed (for /health: "slskd events unreadable").</summary>
    public bool Unreadable { get; private set; }

    /// <summary>Upload events under the radio folder that match no tracked file, since start (for /health).</summary>
    public int UnknownPaths { get; private set; }

    /// <summary>Radio uploads recorded since start.</summary>
    public int UploadsCounted { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(RetentionLimits.EventsReadEvery, _time);
            do
            {
                await ReadOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.WorkerCrashed("retention", $"slskd events: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>One read. Returns the number of new upload rows, or -1 when slskd could not be read.</summary>
    public async Task<int> ReadOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ReadCoreAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? watermark = null;
        if (_store.GetEventsWatermark() is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            watermark = parsed;
        }

        var stopBefore = watermark - Overlap;
        var paths = _fetch.TrackedForRetention()
            .Where(t => t.Path is not null)
            .GroupBy(t => Normalize(t.Path!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        var pages = 0;
        var events = 0;
        var added = 0;
        DateTimeOffset? newest = null;
        try
        {
            var offset = 0;
            while (pages < RetentionLimits.MaxEventPages)
            {
                var page = await _slskd.GetEventsAsync(offset, PageSize, ct).ConfigureAwait(false);
                pages++;
                events += page.RecordCount;
                foreach (var up in page.Uploads)
                {
                    if (newest is null || up.CompletedUtc > newest)
                    {
                        newest = up.CompletedUtc;
                    }

                    var local = Normalize(up.LocalFilename);
                    if (!local.StartsWith(_slskdRadioDir + "/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var radioPath = _radioDir + local[_slskdRadioDir.Length..];
                    if (!paths.TryGetValue(radioPath, out var rowId))
                    {
                        UnknownPaths++;
                        continue;
                    }

                    if (_store.AddUpload(up.TransferId.ToString("D"), rowId, up.CompletedUtc, now))
                    {
                        added++;
                    }
                }

                offset += page.RecordCount;
                if (page.RecordCount == 0 || page.RecordCount < PageSize
                    || (page.Total is { } total && offset >= total)
                    || (stopBefore is { } stop && page.OldestTimestamp is { } oldest && oldest < stop))
                {
                    break;
                }
            }
        }
        catch (SlskdException e)
        {
            Unreadable = true;
            _log.UploadsFailed("events", e.StatusCode, e.Message);
            return -1;
        }

        var next = newest is { } n && (watermark is null || n > watermark) ? n : watermark ?? now;
        var nextText = EncoreStore.FormatUtc(next);
        _store.SetEventsWatermark(nextText);
        UploadsCounted += added;
        LastReadUtc = now;
        Unreadable = false;
        _log.UploadsRead(pages, events, added, nextText);
        return added;
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }
}
