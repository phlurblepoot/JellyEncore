using Encore.Config;
using Encore.Logging;
using Encore.Store;

namespace Encore.Retention;

/// <summary>The three things one retention pass does, as delegates so the worker can be tested without the whole file machinery.</summary>
public sealed record RetentionSteps(
    Func<CancellationToken, Task<PassResult>> Pass,
    Func<PassResult, CancellationToken, Task<DeleteResult>> Delete,
    Func<IReadOnlyList<long>, SignalsSnapshot, int, CancellationToken, Task<IReadOnlyList<PromotionResult>>> Promote)
{
    public static RetentionSteps From(RetentionPass pass, Deleter deleter, Promoter promoter) => new(
        pass.RunAsync,
        deleter.RunAsync,
        promoter.RunPassAsync);
}

/// <summary>Timing of the worker; the defaults are the design values (v04 section 8).</summary>
public sealed record RetentionTiming
{
    /// <summary>The first pass runs this long after the start.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>What one run of the worker did, for tests and logs.</summary>
public sealed record RetentionRunResult(
    PassResult? Pass, DeleteResult? Deleted, IReadOnlyList<PromotionResult> Promotions, bool DeletesSkipped, bool Crashed);

/// <summary>
/// The retention schedule (v04 section 8): one pass 10 minutes after the start, then one every day at ENCORE_RETENTION_HOUR (local
/// time). Each pass decides (<see cref="RetentionPass"/>); in live mode it then promotes up to ENCORE_RETENTION_MAX_PROMOTIONS songs
/// one at a time and deletes up to ENCORE_RETENTION_MAX_DELETES. A failed signal read skips the deletes of that pass. A crash in a
/// pass is logged as 8010 (component retention) and the next day's pass still runs. Only registered when the mode is not off.
/// </summary>
public sealed class RetentionWorker : BackgroundService
{
    private readonly RetentionSteps _steps;
    private readonly EncoreOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionWorker> _log;
    private readonly RetentionTiming _timing;
    private readonly SemaphoreSlim _one = new(1, 1);

    public RetentionWorker(RetentionSteps steps, EncoreOptions options, TimeProvider time, ILogger<RetentionWorker> log, RetentionTiming? timing = null)
    {
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timing = timing ?? new RetentionTiming();
    }

    /// <summary>The time from <paramref name="now"/> to the next occurrence of <paramref name="hour"/>:00 in <paramref name="zone"/> (never zero).</summary>
    public static TimeSpan DelayUntilNext(DateTimeOffset now, int hour, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = new DateTime(local.Year, local.Month, local.Day, hour, 0, 0, DateTimeKind.Unspecified);
        for (var i = 0; i < 3; i++)
        {
            var next = day.AddDays(i);
            if (zone.IsInvalidTime(next))
            {
                next = next.AddHours(1);
            }

            var at = new DateTimeOffset(next, zone.GetUtcOffset(next));
            if (at > now)
            {
                return at - now;
            }
        }

        return TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await Task.Delay(_timing.StartupDelay, _time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                var wait = DelayUntilNext(_time.GetUtcNow(), _options.RetentionHour, _time.LocalTimeZone);
                await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass and its actions. Never throws (except for cancellation): a crash is logged as 8010 and ends this run only.</summary>
    public async Task<RetentionRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (_options.RetentionMode == RetentionMode.Off)
        {
            return new RetentionRunResult(null, null, [], false, false);
        }

        await _one.WaitAsync(ct).ConfigureAwait(false);
        PassResult? pass = null;
        DeleteResult? deleted = null;
        IReadOnlyList<PromotionResult> promotions = [];
        var deletesSkipped = false;
        try
        {
            pass = await _steps.Pass(ct).ConfigureAwait(false);
            if (pass.Mode == RetentionMode.Live)
            {
                // Promotions first: one runs at a time inside the promoter, and a crash in them must not stop the deletes' safety checks.
                var ids = pass.Rows.Where(r => r.Decision == RetentionDecision.Promote).Select(r => r.Row.Id).ToList();
                if (ids.Count > 0)
                {
                    promotions = await _steps.Promote(ids, pass.Signals, _options.RetentionMaxPromotions, ct).ConfigureAwait(false);
                }

                if (!pass.Signals.DeletesAllowed)
                {
                    // A failed signal read means a favorite or playlist may be hidden: no delete this pass (already logged as 8210).
                    deletesSkipped = true;
                }
                else
                {
                    deleted = await _steps.Delete(pass, ct).ConfigureAwait(false);
                }
            }

            return new RetentionRunResult(pass, deleted, promotions, deletesSkipped, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.WorkerCrashed("retention", $"{e.GetType().Name}: {e.Message}");
            return new RetentionRunResult(pass, deleted, promotions, deletesSkipped, true);
        }
        finally
        {
            _one.Release();
        }
    }

    public override void Dispose()
    {
        _one.Dispose();
        base.Dispose();
    }
}
