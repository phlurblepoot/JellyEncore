namespace Encore.Requests.Search;

/// <summary>Search log events 9100 and 9110 (design 13). Never pass a Deezer preview link or a token here.</summary>
public static partial class SearchLog
{
    [LoggerMessage(EventId = 9100, EventName = "request.search", Level = LogLevel.Debug,
        Message = "Search by {UserId} for {Query}: {Library} library, {Songs} songs, {Albums} albums in {ElapsedMs} ms")]
    public static partial void Searched(this ILogger logger, Guid userId, string query, int library, int songs, int albums, long elapsedMs);

    [LoggerMessage(EventId = 9110, EventName = "request.search_source_failed", Level = LogLevel.Warning,
        Message = "Search source {Source} failed: {Error}")]
    public static partial void SourceFailed(this ILogger logger, string source, string error);
}
