using Encore.Jellyfin;

namespace Encore.Tests.EndToEnd;

/// <summary>The retention half of the fake (v04 §13): per-user favorites and playlists, playlist writes, item data and SmartLists.</summary>
internal sealed partial class FakeJellyfin
{
    /// <summary>Each user's favorite songs.</summary>
    public Dictionary<Guid, List<BaseItemDto>> Favorites { get; } = [];

    /// <summary>Playlists by id; <see cref="PlaylistViewers"/> says who sees one, <see cref="PlaylistEditors"/> who may write.</summary>
    public Dictionary<Guid, PlaylistRef> Playlists { get; } = [];

    public Dictionary<Guid, List<Guid>> PlaylistEntries { get; } = [];

    public Dictionary<Guid, HashSet<Guid>> PlaylistViewers { get; } = [];

    public Dictionary<Guid, HashSet<Guid>> PlaylistEditors { get; } = [];

    /// <summary>Per user and item: the user data.</summary>
    public Dictionary<(Guid User, Guid Item), UserItemData> UserData { get; } = [];

    /// <summary>Item ids that exist on the server, for <see cref="ItemExistsAsync"/> (besides <see cref="Audio"/> and <see cref="ItemsById"/>).</summary>
    public HashSet<Guid> ExistingItems { get; } = [];

    public SmartListsInfo SmartLists { get; set; } = SmartListsInfo.NotInstalled;

    /// <summary>Every playlist write in order: kind (add, remove), playlist, item or items, user (null for a remove) and position.</summary>
    public List<(string Kind, Guid Playlist, Guid[] Items, Guid? User, int? Position)> PlaylistWrites { get; } = [];

    public List<(Guid Item, Guid User)> FavoriteMarks { get; } = [];

    public Task<IReadOnlyList<BaseItemDto>> GetFavoriteAudioAsync(Guid userId, Guid? parentId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BaseItemDto>>(Favorites.TryGetValue(userId, out var list) ? list.ToList() : []);

    public Task<IReadOnlyList<PlaylistRef>> GetPlaylistsAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlaylistRef>>(Playlists.Values.Where(p => PlaylistViewers.TryGetValue(p.Id, out var v) && v.Contains(userId)).ToList());

    public Task<IReadOnlyList<Guid>> GetPlaylistEntriesAsync(Guid playlistId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(PlaylistEntries.TryGetValue(playlistId, out var list) ? list.ToList() : []);

    public Task<PlaylistWriteResult> AddToPlaylistAsync(Guid playlistId, Guid itemId, Guid userId, int position, CancellationToken ct = default)
    {
        PlaylistWrites.Add(("add", playlistId, [itemId], userId, position));
        if (!Playlists.ContainsKey(playlistId))
        {
            return Task.FromResult(PlaylistWriteResult.NotFound);
        }

        if (!PlaylistEditors.TryGetValue(playlistId, out var editors) || !editors.Contains(userId))
        {
            return Task.FromResult(PlaylistWriteResult.Forbidden);
        }

        var entries = PlaylistEntries.TryGetValue(playlistId, out var existing) ? existing : PlaylistEntries[playlistId] = [];
        entries.Insert(Math.Min(position, entries.Count), itemId);
        return Task.FromResult(PlaylistWriteResult.Ok);
    }

    public Task<PlaylistWriteResult> RemovePlaylistEntriesAsync(Guid playlistId, IReadOnlyCollection<Guid> entryIds, CancellationToken ct = default)
    {
        PlaylistWrites.Add(("remove", playlistId, entryIds.ToArray(), null, null));
        if (!Playlists.ContainsKey(playlistId))
        {
            return Task.FromResult(PlaylistWriteResult.NotFound);
        }

        if (PlaylistEntries.TryGetValue(playlistId, out var entries))
        {
            entries.RemoveAll(entryIds.Contains);
        }

        return Task.FromResult(PlaylistWriteResult.Ok);
    }

    public Task MarkFavoriteAsync(Guid itemId, Guid userId, CancellationToken ct = default)
    {
        FavoriteMarks.Add((itemId, userId));
        UserData[(userId, itemId)] = (UserData.TryGetValue((userId, itemId), out var d) ? d : new UserItemData(false, 0)) with { IsFavorite = true };
        return Task.CompletedTask;
    }

    public Task<UserItemData?> GetUserItemDataAsync(Guid itemId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(UserData.TryGetValue((userId, itemId), out var d) ? d : (ItemExists(itemId) ? new UserItemData(false, 0) : null));

    public Task<bool> ItemExistsAsync(Guid itemId, CancellationToken ct = default) => Task.FromResult(ItemExists(itemId));

    public Task<SmartListsInfo> GetSmartListsAsync(CancellationToken ct = default) => Task.FromResult(SmartLists);

    private bool ItemExists(Guid id) => ExistingItems.Contains(id) || ItemsById.ContainsKey(id) || Audio.Any(a => a.Id == id);
}
