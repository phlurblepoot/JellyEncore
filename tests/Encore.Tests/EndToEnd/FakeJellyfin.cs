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

    /// <summary>Every PlayLast call, including the ones that failed.</summary>
    public ConcurrentQueue<(string SessionId, Guid[] ItemIds)> PlayLastAttempts { get; } = new();

    public FakePickClient Picks { get; } = new();

    public Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserDto>>(Users.ToList());

    public Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        PlayLastAttempts.Enqueue((sessionId, itemIds.ToArray()));
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

    /// <summary>Every Audio item on the fake server, for the owned index.</summary>
    public List<BaseItemDto> Audio { get; set; } = [];

    /// <summary>Items <see cref="GetItemsByIdsAsync"/> can return (artists with ProviderIds, say), by id.</summary>
    public Dictionary<Guid, BaseItemDto> ItemsById { get; } = [];

    public Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default) =>
        Task.FromResult(new QueryResult<BaseItemDto> { Items = Audio.Skip(startIndex).Take(limit).ToList(), TotalRecordCount = Audio.Count });

    /// <summary>Items by id: <see cref="ItemsById"/>, then any reported file (as Jellyfin lists a song it ingested).</summary>
    public Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default)
    {
        var found = new List<BaseItemDto>();
        lock (_reported)
        {
            foreach (var id in ids)
            {
                if (ItemsById.TryGetValue(id, out var item) || (item = _reported.FirstOrDefault(r => r.Item.Id == id).Item) is not null)
                {
                    found.Add(item);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<BaseItemDto>>(found);
    }

    // ---- Jellyfin ingest (T48) -------------------------------------------------------------------

    /// <summary>The clock that decides when a reported file appears. Tests pass a manual clock.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>How long after Media/Updated a reported file shows up in the recent-audio list. Null = it never does.</summary>
    public TimeSpan? IngestDelay { get; set; } = TimeSpan.Zero;

    /// <summary>What GET /Library/VirtualFolders returns.</summary>
    public List<VirtualFolderInfo> VirtualFolders { get; set; } = [];

    /// <summary>What GET /Library/PhysicalPaths returns. Null = every location of every library (a scanned server).</summary>
    public List<string>? PhysicalPaths { get; set; }

    /// <summary>When set, GET /Library/PhysicalPaths throws it.</summary>
    public Exception? PhysicalPathsFailure { get; set; }

    public int PhysicalPathsCalls;

    /// <summary>Every path Encore reported through Media/Updated, in order.</summary>
    public ConcurrentQueue<string> MediaUpdatedPaths { get; } = new();

    /// <summary>Every GetRecentAudio call: the library and the limit.</summary>
    public ConcurrentQueue<(Guid ParentId, int Limit)> RecentCalls { get; } = new();

    /// <summary>The next recent-audio calls throw these, one each, before answering normally.</summary>
    public ConcurrentQueue<Exception> RecentFailures { get; } = new();

    /// <summary>Set to make Media/Updated throw.</summary>
    public Exception? MediaUpdatedFailure { get; set; }

    /// <summary>Items the recent-audio list always holds (older songs, or one with a similar path).</summary>
    public List<BaseItemDto> RecentExtra { get; } = [];

    private readonly List<(BaseItemDto Item, DateTimeOffset Due)> _reported = [];

    public Task ReportMediaUpdatedAsync(IReadOnlyCollection<string> jellyfinPaths, CancellationToken ct = default)
    {
        if (MediaUpdatedFailure is { } failure)
        {
            throw failure;
        }

        foreach (var path in jellyfinPaths)
        {
            MediaUpdatedPaths.Enqueue(path);
            if (IngestDelay is { } delay)
            {
                // Filed as {dir}/{source}/{artist}/{title}.ext (the importer's layout): the item carries that artist and title.
                var segments = path.Split('/');
                var artist = segments.Length >= 2 ? segments[^2] : null;
                var item = new BaseItemDto
                {
                    Id = Guid.NewGuid(),
                    Type = "Audio",
                    MediaType = "Audio",
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path,
                    RunTimeTicks = TimeSpan.FromSeconds(200).Ticks,
                    ArtistItems = artist is null ? null : [new NameGuidPair { Name = artist }],
                };
                lock (_reported)
                {
                    _reported.Add((item, Clock.GetUtcNow() + delay));
                }
            }
        }

        return Task.CompletedTask;
    }

    public PublicSystemInfo PublicInfo { get; set; } = new() { Id = "serverid1234567890", ServerName = "Fake", Version = "10.11.0" };

    public bool PublicInfoDown { get; set; }

    public int PublicInfoCalls { get; private set; }

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default)
    {
        PublicInfoCalls++;
        return PublicInfoDown ? throw new JellyfinRequestException("GetPublicSystemInfo", 503) : Task.FromResult(PublicInfo);
    }

    public Task<IReadOnlyList<VirtualFolderInfo>> GetVirtualFoldersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VirtualFolderInfo>>(VirtualFolders.ToList());

    public Task<IReadOnlyList<string>> GetPhysicalPathsAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref PhysicalPathsCalls);
        if (PhysicalPathsFailure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult<IReadOnlyList<string>>(PhysicalPaths?.ToList() ?? VirtualFolders.SelectMany(f => f.Locations ?? []).ToList());
    }

    public Task<IReadOnlyList<BaseItemDto>> GetRecentAudioAsync(Guid parentId, int limit, CancellationToken ct = default)
    {
        RecentCalls.Enqueue((parentId, limit));
        if (RecentFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        var now = Clock.GetUtcNow();
        List<BaseItemDto> visible;
        lock (_reported)
        {
            visible = _reported.Where(r => r.Due <= now).OrderByDescending(r => r.Due).Select(r => r.Item).Concat(RecentExtra).Take(limit).ToList();
        }

        return Task.FromResult<IReadOnlyList<BaseItemDto>>(visible);
    }

    /// <summary>The item id the fake gave to a reported path (null when the path was never reported).</summary>
    public Guid? ItemIdOf(string path)
    {
        lock (_reported)
        {
            return _reported.FirstOrDefault(r => r.Item.Path == path).Item?.Id;
        }
    }

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
