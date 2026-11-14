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

    /// <summary>POST /Sessions/{sessionId}/Playing with playCommand=PlayLast and itemIds. Expects 204.</summary>
    Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}
