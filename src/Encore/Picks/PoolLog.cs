namespace Encore.Picks;

/// <summary>Pool and plan log events (v0.2 design section 13, ids 7600-7630).</summary>
internal static partial class PoolLog
{
    [LoggerMessage(EventId = 7600, EventName = "pool.planned", Level = LogLevel.Information,
        Message = "Pool planned ({Mode}) for run {RunId} session {SessionId}: [{Picks}]; external {External}, owned chain {OwnedChain}, unowned dropped {UnownedDropped} ({DropReason})")]
    public static partial void PoolPlanned(this ILogger logger, string mode, string runId, string sessionId, string picks,
        int external, int ownedChain, int unownedDropped, string dropReason);

    [LoggerMessage(EventId = 7610, EventName = "pool.handout", Level = LogLevel.Information,
        Message = "Pool hand-out for run {RunId} session {SessionId}: sent [{Sent}], filler {Filler}, passed over [{PassedOver}], skipped [{Skipped}]")]
    public static partial void PoolHandout(this ILogger logger, string runId, string sessionId, string sent, int filler,
        string passedOver, string skipped);

    [LoggerMessage(EventId = 7620, EventName = "pool.external_timeout", Level = LogLevel.Warning,
        Message = "Outside suggestions for seed {SeedItemId} took longer than {ElapsedMs} ms; planning from the owned chain")]
    public static partial void PoolExternalTimeout(this ILogger logger, Guid seedItemId, long elapsedMs);

    [LoggerMessage(EventId = 7625, EventName = "pool.external_failed", Level = LogLevel.Warning,
        Message = "Outside suggestions for seed {SeedItemId} failed: {Error}; planning from the owned chain")]
    public static partial void PoolExternalFailed(this ILogger logger, Guid seedItemId, string error);

    [LoggerMessage(EventId = 7630, EventName = "pool.index_not_ready", Level = LogLevel.Information,
        Message = "The owned index is not loaded yet; planning from the owned chain only")]
    public static partial void PoolIndexNotReady(this ILogger logger);
}
