namespace Encore.Picks;

/// <summary>Picker log events (v0.1 design, section 8: 500-502, numbered x10 like <c>Encore.Logging.Log</c>).</summary>
internal static partial class PickLog
{
    [LoggerMessage(EventId = 5000, EventName = "picks.source", Level = LogLevel.Debug,
        Message = "Pick source {Source} for seed {Seed}: returned {Returned}, kept {Kept}")]
    public static partial void PicksSource(this ILogger logger, string source, Guid seed, int returned, int kept);

    [LoggerMessage(EventId = 5001, EventName = "picks.planned", Level = LogLevel.Information,
        Message = "Planned {Count} songs: [{Items}], shortfall {Shortfall}, calls {Calls}")]
    public static partial void PicksPlanned(this ILogger logger, int count, string items, int shortfall, int calls);

    [LoggerMessage(EventId = 5002, EventName = "picks.none", Level = LogLevel.Warning,
        Message = "No songs found for seed {Seed}; sources tried [{SourcesTried}]")]
    public static partial void PicksNone(this ILogger logger, Guid seed, string sourcesTried);

    [LoggerMessage(EventId = 5003, EventName = "picks.source_failed", Level = LogLevel.Warning,
        Message = "Pick source {Source} failed for seed {Seed}: {Error}")]
    public static partial void PicksSourceFailed(this ILogger logger, string source, Guid seed, string error);
}
