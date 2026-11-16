namespace Encore.Retention;

/// <summary>Retention log events (v0.4 design section 12). This part holds the play tally, 8220-8230.</summary>
internal static partial class RetentionLog
{
    [LoggerMessage(EventId = 8220, EventName = "plays.counted", Level = LogLevel.Information,
        Message = "Row {RowId} (item {ItemId}) played through by user {UserId} in session {SessionId}, {Total} user-started plays")]
    public static partial void PlaysCounted(this ILogger logger, long rowId, string itemId, string userId, string sessionId, int total);

    [LoggerMessage(EventId = 8230, EventName = "plays.radio_skipped", Level = LogLevel.Debug,
        Message = "Row {RowId} (item {ItemId}) played through in session {SessionId}, not counted: {Why}")]
    public static partial void PlaysRadioSkipped(this ILogger logger, long rowId, string itemId, string sessionId, string why);
}
