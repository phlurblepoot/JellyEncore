namespace Encore.Requests.Search;

/// <summary>Search log events 9100, 9110 and 9111 (design 13). Never pass a Deezer preview link or a token here.</summary>
public static partial class SearchLog
{
    [LoggerMessage(EventId = 9100, EventName = "request.search", Level = LogLevel.Debug,
        Message = "Search by {UserId} for {Query}: {Library} library, {Songs} songs, {Albums} albums in {ElapsedMs} ms")]
    public static partial void Searched(this ILogger logger, Guid userId, string query, int library, int songs, int albums, long elapsedMs);

    [LoggerMessage(EventId = 9111, EventName = "request.search_no_albums", Level = LogLevel.Information,
        Message = "Search by {UserId}: the album lookup returned no rows ({Library} library songs, {LibraryAlbums} library albums, {Songs} songs)")]
    public static partial void SearchedNoAlbums(this ILogger logger, Guid userId, int library, int libraryAlbums, int songs);

    [LoggerMessage(EventId = 9110, EventName = "request.search_source_failed", Level = LogLevel.Warning,
        Message = "Search source {Source} failed: {Error}")]
    public static partial void SourceFailed(this ILogger logger, string source, string error);
}
