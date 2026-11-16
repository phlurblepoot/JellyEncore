namespace Encore.Jellyfin;

/// <summary>Outcome of a playlist write. A refusal or a missing playlist is a result, not an exception (v04 §7.4).</summary>
public enum PlaylistWriteResult
{
    Ok,

    /// <summary>HTTP 403: the user named in the call may not edit this playlist. The caller tries the next candidate.</summary>
    Forbidden,

    /// <summary>HTTP 404: the playlist (or item) is gone.</summary>
    NotFound,
}

/// <summary>A playlist as a user sees it in a listing.</summary>
public sealed record PlaylistRef(Guid Id, string? Name);

/// <summary>One user's data on one item (<c>UserData</c> of <c>GET /Items?enableUserData=true</c>).</summary>
public sealed record UserItemData(bool IsFavorite, int PlayCount);

/// <summary>What the SmartLists plugin says. <see cref="PlaylistIds"/> is empty unless <see cref="Installed"/> and <see cref="ReadOk"/>.</summary>
public sealed record SmartListsInfo(bool Installed, bool ReadOk, IReadOnlySet<Guid> PlaylistIds)
{
    public static SmartListsInfo NotInstalled { get; } = new(false, true, new HashSet<Guid>());
}

/// <summary>
/// The Jellyfin calls the v0.4 retention pass makes (v04 §13). Implemented by <see cref="JellyfinClient"/> on the same HttpClient.
/// Failures throw <see cref="JellyfinException"/> like the other calls, except where a result type says otherwise.
/// </summary>
public interface IRetentionJellyfin
{
    /// <summary>
    /// GET /Items?userId=&amp;isFavorite=true&amp;includeItemTypes=Audio&amp;recursive=true[&amp;parentId=]&amp;fields=Path&amp;enableUserData=true, paged 500.
    /// Always with userId: isFavorite without a user fails on the server.
    /// </summary>
    Task<IReadOnlyList<BaseItemDto>> GetFavoriteAudioAsync(Guid userId, Guid? parentId = null, CancellationToken ct = default);

    /// <summary>GET /Items?userId=&amp;includeItemTypes=Playlist&amp;recursive=true: the playlists that user sees.</summary>
    Task<IReadOnlyList<PlaylistRef>> GetPlaylistsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>GET /Playlists/{id}/Items?userId=: the entries' item ids in playlist order (0-based index = list index).</summary>
    Task<IReadOnlyList<Guid>> GetPlaylistEntriesAsync(Guid playlistId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// POST /Playlists/{id}/Items?ids=&amp;userId=&amp;position= (0-based, the stored entries counted dead ones included). 204 = Ok, 403 = Forbidden, 404 = NotFound.
    /// Never retried.
    /// </summary>
    Task<PlaylistWriteResult> AddToPlaylistAsync(Guid playlistId, Guid itemId, Guid userId, int position, CancellationToken ct = default);

    /// <summary>DELETE /Playlists/{id}/Items?entryIds= (the API key; no userId). Entry ids are the item ids. 204 = Ok, 404 = NotFound.</summary>
    Task<PlaylistWriteResult> RemovePlaylistEntriesAsync(Guid playlistId, IReadOnlyCollection<Guid> entryIds, CancellationToken ct = default);

    /// <summary>POST /UserFavoriteItems/{itemId}?userId=.</summary>
    Task MarkFavoriteAsync(Guid itemId, Guid userId, CancellationToken ct = default);

    /// <summary>GET /Items?ids=&amp;userId=&amp;enableUserData=true. Null when the user's view has no such item.</summary>
    Task<UserItemData?> GetUserItemDataAsync(Guid itemId, Guid userId, CancellationToken ct = default);

    /// <summary>GET /Items?ids= with no userId (the key sees every library). Empty Items = false.</summary>
    Task<bool> ItemExistsAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// GET /Plugins (is SmartLists installed: name contains "SmartLists", ignoring case), then GET /Plugins/SmartLists?type=Playlist.
    /// The plugin route failing in any way (404, 5xx, a refusal, bad JSON, no connection) gives Installed true, ReadOk false: never a guess.
    /// A failure of GET /Plugins itself throws.
    /// </summary>
    Task<SmartListsInfo> GetSmartListsAsync(CancellationToken ct = default);
}
