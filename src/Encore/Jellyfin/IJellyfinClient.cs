namespace Encore.Jellyfin;

/// <summary>The Jellyfin REST calls Encore makes. Every call throws <see cref="JellyfinException"/> (or cancels) on failure.</summary>
public interface IJellyfinClient
{
    /// <summary>GET /Sessions.</summary>
    Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default);

    /// <summary>GET /Users.</summary>
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default);

    /// <summary>GET /Artists/{artistId}/Similar. Pick source 1 (artist-based).</summary>
    Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default);

    /// <summary>GET /Items with artistIds, includeItemTypes=Audio, recursive, sortBy=Random. Songs by the given artists.</summary>
    Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default);

    /// <summary>GET /Items/{songId}/Similar. Fallback 1.</summary>
    Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default);

    /// <summary>GET /Items/{songId}/InstantMix. Fallback 2. Genre-based.</summary>
    Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// GET /Items?recursive=true&amp;includeItemTypes=Audio&amp;fields=Path,DateCreated&amp;startIndex=&amp;limit=&amp;enableTotalRecordCount=true.
    /// One page of every song on the server (the owned index). No userId: the API key sees every library.
    /// </summary>
    Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default);

    /// <summary>GET /Items?ids=a,b&amp;fields=. Used for an artist's MusicBrainz id (fields=ProviderIds). No userId.</summary>
    Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default);

    /// <summary>
    /// POST /Library/Media/Updated with {"Updates":[{"Path":"...","UpdateType":"Created"}]}, expects 204. Paths are as Jellyfin sees them.
    /// Never retried (idempotent: the next ingest repeats it).
    /// </summary>
    Task ReportMediaUpdatedAsync(IReadOnlyCollection<string> jellyfinPaths, CancellationToken ct = default);

    /// <summary>GET /System/Info/Public (anonymous): the server id, name and version. The id feeds the "Open in Jellyfin" link.</summary>
    Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default);

    /// <summary>GET /Library/VirtualFolders. Needs an admin token (the API key is one).</summary>
    Task<IReadOnlyList<VirtualFolderInfo>> GetVirtualFoldersAsync(CancellationToken ct = default);

    /// <summary>GET /Library/PhysicalPaths: the folders Jellyfin has in its library tree (empty until a library has been scanned). Needs an admin token.</summary>
    Task<IReadOnlyList<string>> GetPhysicalPathsAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /Items?parentId=&amp;recursive=true&amp;includeItemTypes=Audio&amp;fields=Path,DateCreated&amp;sortBy=DateCreated&amp;sortOrder=Descending&amp;limit=&amp;enableTotalRecordCount=false.
    /// The newest songs in one library, with their paths.
    /// </summary>
    Task<IReadOnlyList<BaseItemDto>> GetRecentAudioAsync(Guid parentId, int limit, CancellationToken ct = default);

    /// <summary>
    /// GET /Items?searchTerm=&amp;includeItemTypes=Audio,MusicAlbum&amp;recursive=true&amp;limit=&amp;userId=. The library as one user sees it (the userId keeps
    /// that user's access rules); used by request search. Songs and albums come with <c>AlbumArtist</c> and <c>ProductionYear</c> when Jellyfin has them.
    /// </summary>
    Task<QueryResult<BaseItemDto>> SearchItemsAsync(string searchTerm, Guid userId, int limit, CancellationToken ct = default);

    /// <summary>POST /Sessions/{sessionId}/Playing with playCommand=PlayLast and itemIds. Expects 204.</summary>
    Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>
    /// POST /Sessions/{sessionId}/Message with {"Header","Text","TimeoutMs"}, expects 204. Shows a message on that session's client.
    /// Never retried here: the caller decides (the request notifier retries once on a 5xx).
    /// </summary>
    Task SendMessageAsync(string sessionId, string header, string text, int timeoutMs, CancellationToken ct = default);
}
