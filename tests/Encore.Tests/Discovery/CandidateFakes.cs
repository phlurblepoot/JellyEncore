using System.Collections.Concurrent;
using Encore.Discovery;
using Encore.Jellyfin;

namespace Encore.Tests.Discovery;

/// <summary>In-memory Deezer: artists by folded name, radio, related and top lists by artist id; every call recorded.</summary>
internal sealed class FakeDeezer : IDeezerClient
{
    private int _inFlight;
    private int _maxInFlight;

    public Dictionary<string, DeezerArtist> Artists { get; } = new(StringComparer.Ordinal);

    public Dictionary<long, List<DeezerTrack>> Radio { get; } = [];

    public Dictionary<long, List<DeezerArtist>> Related { get; } = [];

    public Dictionary<long, List<DeezerTrack>> Top { get; } = [];

    /// <summary>When set, every call throws it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When true, every call waits until the caller cancels.</summary>
    public bool Hang { get; set; }

    public ConcurrentQueue<string> Calls { get; } = new();

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public DeezerArtist AddArtist(long id, string name)
    {
        var artist = new DeezerArtist(id, name, 1000, true);
        Artists[KeyText.ArtistKey(name)] = artist;
        return artist;
    }

    public Task<DeezerArtist?> FindArtistAsync(string name, CancellationToken ct) =>
        Run($"find:{name}", () => Artists.TryGetValue(KeyText.ArtistKey(name), out var a) ? a : null, ct);

    public Task<IReadOnlyList<DeezerTrack>> GetArtistRadioAsync(long artistId, int limit, CancellationToken ct) =>
        Run<IReadOnlyList<DeezerTrack>>($"radio:{artistId}:{limit}", () => Radio.GetValueOrDefault(artistId, []).Take(limit).ToList(), ct);

    public Task<IReadOnlyList<DeezerArtist>> GetRelatedArtistsAsync(long artistId, int limit, CancellationToken ct) =>
        Run<IReadOnlyList<DeezerArtist>>($"related:{artistId}:{limit}", () => Related.GetValueOrDefault(artistId, []).Take(limit).ToList(), ct);

    public Task<IReadOnlyList<DeezerTrack>> GetArtistTopAsync(long artistId, int limit, CancellationToken ct) =>
        Run<IReadOnlyList<DeezerTrack>>($"top:{artistId}:{limit}", () => Top.GetValueOrDefault(artistId, []).Take(limit).ToList(), ct);

    public Task<DeezerTrackDetail?> GetTrackAsync(long trackId, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>What <see cref="SearchTracksAsync"/> returns (the whole list, whatever the query).</summary>
    public List<DeezerSearchTrack> SearchRows { get; } = [];

    public Task<IReadOnlyList<DeezerSearchTrack>> SearchTracksAsync(string query, int limit, CancellationToken ct) =>
        Run<IReadOnlyList<DeezerSearchTrack>>($"search:{query}:{limit}", () => SearchRows.Take(limit).ToList(), ct);

    private async Task<T> Run<T>(string call, Func<T> answer, CancellationToken ct)
    {
        Calls.Enqueue(call);
        var now = Interlocked.Increment(ref _inFlight);
        try
        {
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            await Task.Delay(5, ct);
            if (Failure is { } failure)
            {
                throw failure;
            }

            return answer();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

internal sealed class FakeListenBrainz : IListenBrainzClient
{
    public Dictionary<Guid, List<LbSimilarArtist>> Similar { get; } = [];

    public ConcurrentQueue<(Guid Mbid, int Limit)> Calls { get; } = new();

    public Task<IReadOnlyList<LbSimilarArtist>> GetSimilarArtistsAsync(Guid artistMbid, int limit, CancellationToken ct)
    {
        Calls.Enqueue((artistMbid, limit));
        return Task.FromResult<IReadOnlyList<LbSimilarArtist>>(Similar.GetValueOrDefault(artistMbid, []).Take(limit).ToList());
    }
}

/// <summary>A Jellyfin with an Audio list (paged) and artist items with ProviderIds; can fail on demand.</summary>
internal sealed class FakeLibrary : IJellyfinClient
{
    public List<BaseItemDto> Audio { get; set; } = [];

    public Dictionary<Guid, BaseItemDto> Items { get; } = [];

    public Exception? Failure { get; set; }

    public ConcurrentQueue<(int Start, int Limit)> PageCalls { get; } = new();

    public ConcurrentQueue<(Guid[] Ids, string Fields)> ByIdCalls { get; } = new();

    public Task ReportMediaUpdatedAsync(IReadOnlyCollection<string> jellyfinPaths, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<VirtualFolderInfo>> GetVirtualFoldersAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> GetPhysicalPathsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> SearchItemsAsync(string searchTerm, Guid userId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<BaseItemDto>> GetRecentAudioAsync(Guid parentId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default)
    {
        PageCalls.Enqueue((startIndex, limit));
        if (Failure is { } failure)
        {
            throw failure;
        }

        var all = Audio.ToList();
        return Task.FromResult(new QueryResult<BaseItemDto> { Items = all.Skip(startIndex).Take(limit).ToList(), TotalRecordCount = all.Count });
    }

    public Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default)
    {
        ByIdCalls.Enqueue((ids.ToArray(), fields));
        if (Failure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult<IReadOnlyList<BaseItemDto>>(ids.Where(Items.ContainsKey).Select(i => Items[i]).ToList());
    }

    public Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

    public Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeNegative : INegativeCache
{
    public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

    IReadOnlySet<string> INegativeCache.Keys(DateTimeOffset now) => Keys;
}

/// <summary>Builders for songs and tracks.</summary>
internal static class Songs
{
    public const string RadioDir = "/music-radio";

    public static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    public static BaseItemDto Audio(int id, string artist, string title, int? seconds = 200, string? path = null,
        DateTimeOffset? created = null, int artistId = 0) => new()
    {
        Id = Id(id),
        Name = title,
        Type = "Audio",
        RunTimeTicks = seconds is { } s ? TimeSpan.FromSeconds(s).Ticks : null,
        ArtistItems = [new NameGuidPair { Id = artistId == 0 ? Id(900_000 + Math.Abs(artist.GetHashCode() % 99_999)) : Id(artistId), Name = artist }],
        Path = path ?? $"/music/{artist}/{title}.flac",
        DateCreated = created,
    };

    public static DeezerTrack Track(long id, string artist, string title, int duration = 200, long artistId = 0) =>
        new(id, title, title, null, duration, 1, new DeezerArtist(artistId == 0 ? id * 10 : artistId, artist, 1, true),
            new DeezerAlbumRef(id * 100, "Album " + id, null));
}
