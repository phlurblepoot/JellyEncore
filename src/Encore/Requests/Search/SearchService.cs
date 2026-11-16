using System.Diagnostics;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Requests.Albums;
using Encore.Requests.Api;
using Encore.Store;

namespace Encore.Requests.Search;

/// <summary>What a search returns (design 5). <see cref="Albums"/> is empty when Lidarr is not configured.</summary>
public sealed record SearchResponse(
    SearchLibrary Library,
    IReadOnlyList<SongRow> Songs,
    IReadOnlyList<AlbumRow> Albums,
    IReadOnlyList<string> Warnings);

/// <summary>What the person's own Jellyfin library holds for the text (the injected script ignores it; Jellyfin web shows it).</summary>
public sealed record SearchLibrary(IReadOnlyList<LibrarySong> Songs, IReadOnlyList<LibraryAlbum> Albums);

public sealed record LibrarySong(string ItemId, string Artist, string Title, string? Album);

public sealed record LibraryAlbum(string ItemId, string Artist, string Title, int? Year);

/// <summary>An in-progress request for a song. <see cref="Mine"/> is true when the signed-in person made it (only then can they open or cancel it).</summary>
public sealed record ActiveRequestRef(string Id, string State, string? Detail, bool Mine, DateTimeOffset? NextTryUtc = null);

/// <summary>
/// One Deezer song. <see cref="Owned"/> rows carry <see cref="ItemId"/> and are shown as "In your library", not requestable.
/// There is no year (search rows have no release date) and no preview link (never read).
/// </summary>
public sealed record SongRow(
    long DeezerTrackId,
    string Title,
    string Artist,
    string Album,
    int DurationS,
    string? CoverUrl,
    bool Explicit,
    bool Owned,
    string? ItemId,
    bool Requestable,
    ActiveRequestRef? Request);

/// <summary>Search timeouts. The defaults are the design values; tests shorten them.</summary>
public sealed record SearchOptions
{
    public TimeSpan JellyfinTimeout { get; init; } = RequestLimits.SearchJellyfinTimeout;

    public TimeSpan DeezerTimeout { get; init; } = RequestLimits.SearchDeezerTimeout;

    public TimeSpan LidarrTimeout { get; init; } = RequestLimits.SearchLidarrTimeout;
}

/// <summary>Which sections a search asks for.</summary>
[Flags]
public enum SearchKinds
{
    None = 0,
    Song = 1,
    Album = 2,
    All = Song | Album,
}

/// <summary>
/// Merges the requester's Jellyfin library and a Deezer text search (design 5 items 1, 2 and 4). A Deezer row is owned
/// when the owned index (or, failing that, the library results) holds the song; owned rows are not requestable. A row
/// with an in-progress request carries it. Each source has its own timeout; a source that fails or times out gives an
/// empty part and a warning, never an error. Albums come from Lidarr's catalog (item 3) when Lidarr is configured.
/// </summary>
public sealed class SearchService(
    IJellyfinClient jellyfin,
    IDeezerClient deezer,
    IOwnedIndex? owned,
    RequestStore store,
    ILogger<SearchService> logger,
    SearchOptions? options = null,
    AlbumSearch? albums = null)
{
    private readonly SearchOptions _options = options ?? new SearchOptions();

    /// <summary>Is the text a valid query (2 to 100 characters after trimming)?</summary>
    public static bool TryNormalize(string? q, out string query)
    {
        query = (q ?? string.Empty).Trim();
        return query.Length >= RequestLimits.SearchQueryMin && query.Length <= RequestLimits.SearchQueryMax;
    }

    /// <summary>The kinds named in <c>kinds=song,album</c>; null for an unknown name. Empty or missing means both.</summary>
    public static SearchKinds? ParseKinds(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds))
        {
            return SearchKinds.All;
        }

        var result = SearchKinds.None;
        foreach (var part in kinds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "song":
                    result |= SearchKinds.Song;
                    break;
                case "album":
                    result |= SearchKinds.Album;
                    break;
                default:
                    return null;
            }
        }

        return result == SearchKinds.None ? null : result;
    }

    public async Task<SearchResponse> SearchAsync(Guid userId, string query, SearchKinds kinds, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var warnings = new List<string>();
        var songsWanted = (kinds & SearchKinds.Song) != 0;
        var albumsWanted = (kinds & SearchKinds.Album) != 0;

        var libraryTask = Source("jellyfin", _options.JellyfinTimeout, warnings,
            token => jellyfin.SearchItemsAsync(query, userId, RequestLimits.SearchLibraryLimit, token), ct);
        var deezerTask = songsWanted
            ? Source("deezer", _options.DeezerTimeout, warnings,
                token => deezer.SearchTracksAsync(query, RequestLimits.SearchDeezerLimit, token), ct)
            : Task.FromResult<IReadOnlyList<DeezerSearchTrack>?>(null);
        var lidarrTask = albumsWanted && albums is not null
            ? Source("lidarr", _options.LidarrTimeout, warnings, token => albums.LookupAsync(query, token), ct)
            : Task.FromResult<AlbumLookup?>(null);
        await Task.WhenAll(libraryTask, deezerTask, lidarrTask).ConfigureAwait(false);

        var items = libraryTask.Result?.Items ?? [];
        var librarySongs = new List<LibrarySong>();
        var libraryAlbums = new List<LibraryAlbum>();
        var libraryKeys = new Dictionary<TrackKey, Guid>();
        foreach (var item in items)
        {
            if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            if (string.Equals(item.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                var artist = OwnedIndex.ArtistOf(item).Name ?? item.AlbumArtist ?? string.Empty;
                if (TrackKey.TryCreate(artist, item.Name, out var key))
                {
                    libraryKeys.TryAdd(key, item.Id);
                }

                if (songsWanted)
                {
                    librarySongs.Add(new LibrarySong(Key(item.Id), artist, item.Name, item.Album));
                }
            }
            else if (string.Equals(item.Type, "MusicAlbum", StringComparison.OrdinalIgnoreCase))
            {
                var artist = item.AlbumArtist ?? item.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name))?.Name ?? string.Empty;
                libraryAlbums.Add(new LibraryAlbum(Key(item.Id), artist, item.Name, item.ProductionYear));
            }
        }

        var songs = new List<SongRow>();
        foreach (var track in deezerTask.Result ?? [])
        {
            songs.Add(ToRow(track, userId, libraryKeys));
        }

        IReadOnlyList<AlbumRow> albumRows = [];
        if (lidarrTask.Result is { } lookup)
        {
            IReadOnlyList<RequestRow> active = [];
            try
            {
                active = store.Active();
            }
            catch (StoreException)
            {
                // The rows still show; they just do not say that a request exists.
            }

            albumRows = AlbumSearch.BuildRows(lookup, libraryAlbums, active, Key(userId), DateTimeOffset.UtcNow);
        }

        if (!albumsWanted)
        {
            libraryAlbums.Clear();
        }

        logger.Searched(userId, query, librarySongs.Count + libraryAlbums.Count, songs.Count, albumRows.Count, clock.ElapsedMilliseconds);
        return new SearchResponse(new SearchLibrary(librarySongs, libraryAlbums), songs, albumRows, warnings);
    }

    /// <summary>The row's title as shown: Deezer's title, with the version note added in brackets when the title lacks it.</summary>
    public static string DisplayTitle(string title, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return title;
        }

        var note = version.Trim().Trim('(', ')', '[', ']').Trim();
        if (note.Length == 0 || title.Contains(note, StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        return $"{title} ({note})";
    }

    private SongRow ToRow(DeezerSearchTrack track, Guid userId, Dictionary<TrackKey, Guid> libraryKeys)
    {
        // Deezer's own title is the key text, as in the radio's candidates, so a request and a radio fetch of one song agree.
        var hasKey = TrackKey.TryCreate(track.ArtistName, track.Title, out var key);
        string? itemId = null;
        if (hasKey)
        {
            if (owned?.Resolve(key, track.DurationS > 0 ? track.DurationS : null) is { } entry)
            {
                itemId = Key(entry.ItemId);
            }
            else if (libraryKeys.TryGetValue(key, out var libraryId))
            {
                itemId = Key(libraryId);
            }
        }

        var isOwned = itemId is not null;
        ActiveRequestRef? request = null;
        if (hasKey && !isOwned)
        {
            try
            {
                if (store.ActiveForKey(key.Value).FirstOrDefault() is { } active)
                {
                    request = new ActiveRequestRef(active.Id, active.State, active.Detail, active.UserId == Key(userId), active.NextTryUtc);
                }
            }
            catch (StoreException)
            {
                // The row still shows; it just does not say that a request exists.
            }
        }

        return new SongRow(
            track.Id,
            DisplayTitle(track.Title, track.TitleVersion),
            track.ArtistName,
            track.AlbumTitle,
            track.DurationS,
            track.CoverUrl,
            track.Explicit,
            isOwned,
            itemId,
            !isOwned,
            request);
    }

    private static string Key(Guid id) => RequestUserKey.Of(id);

    /// <summary>One source under its own timeout. A failure or timeout adds a warning and returns null; the caller's own cancel still cancels.</summary>
    private async Task<T?> Source<T>(string name, TimeSpan timeout, List<string> warnings, Func<CancellationToken, Task<T>> call, CancellationToken ct)
        where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await call(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.SourceFailed(name, "timeout");
            Warn(name, "timeout", warnings);
            return null;
        }
        catch (Exception ex)
        {
            // The messages of the two clients hold no query string, key or token (design 13).
            logger.SourceFailed(name, ex.GetType().Name + ": " + ex.Message);
            Warn(name, "failed", warnings);
            return null;
        }
    }

    private static void Warn(string source, string reason, List<string> warnings)
    {
        lock (warnings)
        {
            warnings.Add($"{source}: {reason}");
        }
    }
}
