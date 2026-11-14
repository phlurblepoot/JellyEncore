using System.Collections.Concurrent;
using Encore.Jellyfin;
using Encore.Tests.Picks;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// A fake Jellyfin for the orchestrator: users, PlayLast (recorded, or failing on demand) and the pick sources,
/// which are delegated to the picker's fake so the picks follow the same rules as the picker tests.
/// </summary>
internal sealed class FakeJellyfin : IJellyfinClient
{
    public List<UserDto> Users { get; set; } = [];

    /// <summary>Set to make the next PlayLast throw this instead of sending.</summary>
    public Exception? PlayLastFailure { get; set; }

    public ConcurrentQueue<(string SessionId, Guid[] ItemIds)> PlayLasts { get; } = new();

    public FakePickClient Picks { get; } = new();

    public Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserDto>>(Users.ToList());

    public Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (PlayLastFailure is { } failure)
        {
            throw failure;
        }

        PlayLasts.Enqueue((sessionId, itemIds.ToArray()));
        return Task.CompletedTask;
    }

    public Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default) =>
        Picks.GetSimilarArtistsAsync(artistId, userId, limit, ct);

    public Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default) =>
        Picks.GetSongsByArtistsAsync(artistIds, userId, limit, ct);

    public Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default) =>
        Picks.GetSimilarSongsAsync(songId, userId, limit, excludeArtistIds, ct);

    public Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default) =>
        Picks.GetInstantMixAsync(songId, userId, limit, ct);
}

/// <summary>Ids and songs used by the end-to-end tests. The seed's artist is similar to three artists, each with four songs.</summary>
internal static class Catalog
{
    public static Guid G(long n) => new($"00000000-0000-0000-0000-{n:D12}");

    public static readonly Guid User = G(1);
    public static readonly Guid OtherUser = G(2);
    public static readonly Guid SeedSong = G(100);
    public static readonly Guid SeedArtist = G(200);

    public static readonly Guid[] SimilarArtists = [G(201), G(202), G(203)];

    public static FakeJellyfin Build()
    {
        var fake = new FakeJellyfin
        {
            Users = [new UserDto { Id = User, Name = "nathan" }, new UserDto { Id = OtherUser, Name = "guest" }],
        };
        fake.Picks.SimilarArtists[SeedArtist] = SimilarArtists
            .Select(id => new BaseItemDto { Id = id, Name = $"Artist {id:N}", Type = "MusicArtist" })
            .ToList();
        for (var a = 0; a < SimilarArtists.Length; a++)
        {
            var artist = SimilarArtists[a];
            fake.Picks.SongsByArtist[artist] = Enumerable.Range(0, 4)
                .Select(i => Song(G(1000 + (a * 100) + i), artist, $"Song {a}-{i}"))
                .ToList();
        }

        // The seed artist's own songs are the last resort, after the similar artists.
        fake.Picks.SongsByArtist[SeedArtist] = Enumerable.Range(0, 3).Select(i => Song(G(300 + i), SeedArtist, $"Seed artist {i}")).ToList();
        return fake;
    }

    public static BaseItemDto Song(Guid id, Guid artist, string name) => new()
    {
        Id = id,
        Name = name,
        Type = "Audio",
        MediaType = "Audio",
        RunTimeTicks = TimeSpan.FromMinutes(3).Ticks,
        ArtistItems = [new NameGuidPair { Id = artist, Name = $"Artist {artist:N}" }],
        AlbumArtists = [new NameGuidPair { Id = artist, Name = $"Artist {artist:N}" }],
    };
}
