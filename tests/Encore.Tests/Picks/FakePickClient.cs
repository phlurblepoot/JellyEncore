using Encore.Jellyfin;

namespace Encore.Tests.Picks;

/// <summary>In-memory <see cref="IJellyfinClient"/> for the picker: scripted answers per source, every call recorded.</summary>
internal sealed class FakePickClient : IJellyfinClient
{
    public Dictionary<Guid, List<BaseItemDto>> SimilarArtists { get; } = [];

    /// <summary>Songs per artist; GetSongsByArtists returns them round-robin across the requested artists.</summary>
    public Dictionary<Guid, List<BaseItemDto>> SongsByArtist { get; } = [];

    public Dictionary<Guid, List<BaseItemDto>> SimilarSongs { get; } = [];

    public Dictionary<Guid, List<BaseItemDto>> InstantMix { get; } = [];

    /// <summary>Call names that throw a 503 <see cref="JellyfinRequestException"/>.</summary>
    public HashSet<string> Failing { get; } = [];

    public List<string> Calls { get; } = [];

    public List<(IReadOnlyCollection<Guid> Artists, int Limit)> SongsRequests { get; } = [];

    public List<IReadOnlyCollection<Guid>?> SimilarSongExcludes { get; } = [];

    public Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default) =>
        Answer("similar_artists", SimilarArtists.GetValueOrDefault(artistId), limit);

    public Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default)
    {
        SongsRequests.Add((artistIds, limit));
        // Round-robin across the artists, a stand-in for the server's sortBy=Random.
        var lists = artistIds.Select(a => SongsByArtist.GetValueOrDefault(a) ?? []).ToList();
        var items = Enumerable.Range(0, lists.Select(l => l.Count).DefaultIfEmpty(0).Max())
            .SelectMany(i => lists.Where(l => i < l.Count).Select(l => l[i]))
            .ToList();
        return Answer("songs_by_artists", items, limit);
    }

    public Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default)
    {
        SimilarSongExcludes.Add(excludeArtistIds);
        var items = (SimilarSongs.GetValueOrDefault(songId) ?? [])
            .Where(i => excludeArtistIds is null || i.ArtistItems is null || !i.ArtistItems.Any(a => excludeArtistIds.Contains(a.Id)))
            .ToList();
        return Answer("similar_songs", items, limit);
    }

    public Task ReportMediaUpdatedAsync(IReadOnlyCollection<string> jellyfinPaths, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<VirtualFolderInfo>> GetVirtualFoldersAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> GetPhysicalPathsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<BaseItemDto>> GetRecentAudioAsync(Guid parentId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default) =>
        Answer("instant_mix", InstantMix.GetValueOrDefault(songId), limit);

    private Task<QueryResult<BaseItemDto>> Answer(string name, List<BaseItemDto>? items, int limit)
    {
        Calls.Add(name);
        if (Failing.Contains(name))
        {
            throw new JellyfinRequestException(name, 503);
        }

        var list = (items ?? []).Take(limit).ToList();
        return Task.FromResult(new QueryResult<BaseItemDto> { Items = list, TotalRecordCount = list.Count });
    }
}

/// <summary>Builders for test songs and artists. Ids are deterministic per name.</summary>
internal static class Lib
{
    public static Guid IdOf(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return new Guid(bytes);
    }

    public static BaseItemDto Artist(string name) => new() { Id = IdOf("artist:" + name), Name = name, Type = "MusicArtist" };

    public static BaseItemDto Song(string name, string? artist, int seconds = 200, string type = "Audio") => new()
    {
        Id = IdOf("song:" + name),
        Name = name,
        Type = type,
        RunTimeTicks = seconds < 0 ? null : TimeSpan.FromSeconds(seconds).Ticks,
        ArtistItems = artist is null ? null : [new NameGuidPair { Id = IdOf("artist:" + artist), Name = artist }],
    };

    public static List<BaseItemDto> Songs(string artist, int count, string prefix = "") =>
        [.. Enumerable.Range(1, count).Select(i => Song($"{prefix}{artist} {i}", artist))];
}
