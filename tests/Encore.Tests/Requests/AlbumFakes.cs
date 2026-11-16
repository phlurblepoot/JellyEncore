using System.Globalization;
using System.Text.Json.Nodes;
using Encore.Lidarr;
using Encore.Requests.Albums;

namespace Encore.Tests.Requests;

/// <summary>
/// An in-memory Lidarr behind <see cref="ILidarrClient"/>: a metadata catalog (what a lookup finds outside the database),
/// the database's artists and albums, and every write in <see cref="Writes"/> (the tests assert the full list).
/// It behaves like Lidarr 3.1.6 as lidarr-api.md describes it: POST album adds a missing artist from the body and answers
/// 400 "already been added" for an album it has; lookup rows carry no statistics.
/// </summary>
internal sealed class FakeLidarrApi : ILidarrClient
{
    public sealed class Album
    {
        public int Id { get; set; }
        public required string Fid { get; init; }
        public required string Title { get; init; }
        public int ArtistId { get; set; }
        public bool Monitored { get; set; }
        public DateTimeOffset? Release { get; set; } = new DateTimeOffset(1997, 5, 21, 0, 0, 0, TimeSpan.Zero);
        public int Tracks { get; set; } = 12;
        public LidarrAlbumStatistics? Stats { get; set; }
    }

    public sealed class Artist
    {
        public int Id { get; init; }
        public required string Fid { get; init; }
        public required string Name { get; init; }
        public bool Monitored { get; set; }
        public required JsonObject Raw { get; set; }
    }

    public sealed record CatalogEntry(string Fid, string Title, string ArtistFid, string ArtistName, DateTimeOffset? Release, int Tracks);

    private int _nextAlbum = 300;
    private int _nextArtist = 40;

    public Dictionary<string, CatalogEntry> Catalog { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Album> Albums { get; } = [];

    public List<Artist> Artists { get; } = [];

    public List<LidarrRootFolder> Roots { get; } = [new(1, "/media/music", 3, 2, true)];

    /// <summary>Every write, in order: "POST album {fid}", "PUT album/monitor [ids] {bool}", "PUT artist/{id}".</summary>
    public List<string> Writes { get; } = [];

    public List<LidarrAddAlbum> Posts { get; } = [];

    /// <summary>The bodies of artist PUTs (GET body with only monitored changed).</summary>
    public List<JsonObject> ArtistPuts { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>When set, every call throws it (Lidarr unreachable or key rejected).</summary>
    public Exception? Down { get; set; }

    /// <summary>Lidarr ignores an artist PUT (the after-check must catch it).</summary>
    public bool IgnoreArtistMonitor { get; set; }

    /// <summary>The next POST album finds that someone else added the album a moment ago.</summary>
    public bool PostRace { get; set; }

    public int Lookups => Calls.Count(c => c.StartsWith("lookup ", StringComparison.Ordinal));

    public CatalogEntry AddCatalog(string fid, string title, string artistFid, string artistName, DateTimeOffset? release = null, int tracks = 12)
    {
        var entry = new CatalogEntry(fid, title, artistFid, artistName, release ?? new DateTimeOffset(1997, 5, 21, 0, 0, 0, TimeSpan.Zero), tracks);
        Catalog[fid] = entry;
        return entry;
    }

    public Artist AddArtist(string fid, string name, bool monitored)
    {
        var id = ++_nextArtist;
        var raw = new JsonObject
        {
            ["id"] = id,
            ["status"] = "continuing",
            ["artistName"] = name,
            ["foreignArtistId"] = fid,
            ["path"] = "/media/music/" + name,
            ["qualityProfileId"] = 7,
            ["metadataProfileId"] = 8,
            ["monitored"] = monitored,
            ["monitorNewItems"] = "all",
            ["rootFolderPath"] = "/media/music",
            ["tags"] = new JsonArray(5),
        };
        var artist = new Artist { Id = id, Fid = fid, Name = name, Monitored = monitored, Raw = raw };
        Artists.Add(artist);
        return artist;
    }

    /// <summary>Puts a catalog album in the database under an artist already there.</summary>
    public Album AddAlbum(string fid, Artist artist, bool monitored, LidarrAlbumStatistics? stats = null)
    {
        var entry = Catalog[fid];
        var album = new Album { Id = ++_nextAlbum, Fid = fid, Title = entry.Title, ArtistId = artist.Id, Monitored = monitored, Release = entry.Release, Tracks = entry.Tracks, Stats = stats };
        Albums.Add(album);
        return album;
    }

    public Album AlbumOf(string fid) => Albums.Single(a => string.Equals(a.Fid, fid, StringComparison.OrdinalIgnoreCase));

    public Artist ArtistOf(int id) => Artists.Single(a => a.Id == id);

    public Task<LidarrStatus> GetStatusAsync(CancellationToken ct) => Run("status", () => new LidarrStatus("Lidarr", "3.1.6.5079"));

    public Task<IReadOnlyList<LidarrRootFolder>> GetRootFoldersAsync(CancellationToken ct) => Run("rootfolder", () => (IReadOnlyList<LidarrRootFolder>)Roots.ToList());

    public Task<IReadOnlyList<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct) => Run("lookup " + term, () =>
    {
        IEnumerable<string> fids;
        if (term.StartsWith("lidarr:", StringComparison.OrdinalIgnoreCase))
        {
            fids = [term["lidarr:".Length..]];
        }
        else
        {
            fids = Catalog.Values
                .Where(c => c.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || c.ArtistName.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Fid);
        }

        var list = new List<LidarrAlbum>();
        foreach (var fid in fids)
        {
            if (Albums.FirstOrDefault(a => string.Equals(a.Fid, fid, StringComparison.OrdinalIgnoreCase)) is { } db)
            {
                list.Add(Map(db, withStats: false));
            }
            else if (Catalog.TryGetValue(fid, out var c))
            {
                list.Add(new LidarrAlbum(
                    0, c.Title, "", 0, c.Fid, false, "Album", [], c.Release,
                    [new LidarrImage("cover", "https://img.test/" + c.Fid + ".jpg", "https://img.test/" + c.Fid + ".jpg")],
                    "https://img.test/" + c.Fid + ".jpg",
                    [new LidarrRelease(null, c.Title, c.Tracks, "CD", false)],
                    new LidarrArtistRef(0, c.ArtistName, c.ArtistFid, false), null));
            }
        }

        return (IReadOnlyList<LidarrAlbum>)list;
    });

    public Task<LidarrArtist?> FindArtistByForeignIdAsync(string foreignArtistId, CancellationToken ct) => Run("artist?mbId", () =>
        Artists.FirstOrDefault(a => string.Equals(a.Fid, foreignArtistId, StringComparison.OrdinalIgnoreCase)) is { } a ? MapArtist(a) : null);

    public Task<LidarrArtist?> GetArtistAsync(int id, CancellationToken ct) => Run("artist/" + id, () =>
        Artists.FirstOrDefault(a => a.Id == id) is { } a ? MapArtist(a) : null);

    public Task<LidarrArtist?> SetArtistMonitoredAsync(int id, bool monitored, CancellationToken ct) => Run("put artist", () =>
    {
        var artist = Artists.FirstOrDefault(a => a.Id == id);
        if (artist is null)
        {
            return (LidarrArtist?)null;
        }

        var body = (JsonObject)artist.Raw.DeepClone();
        body["monitored"] = monitored;
        Writes.Add("PUT artist/" + id.ToString(CultureInfo.InvariantCulture));
        ArtistPuts.Add(body);
        if (!IgnoreArtistMonitor)
        {
            artist.Monitored = monitored;
            artist.Raw = body;
        }

        return MapArtist(artist);
    });

    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByArtistAsync(int artistId, CancellationToken ct) => Run("album?artistId", () =>
        (IReadOnlyList<LidarrAlbum>)Albums.Where(a => a.ArtistId == artistId).Select(a => Map(a, true)).ToList());

    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByForeignIdAsync(string foreignAlbumId, bool includeAllArtistAlbums, CancellationToken ct) => Run("album?foreignAlbumId", () =>
        (IReadOnlyList<LidarrAlbum>)Albums.Where(a => string.Equals(a.Fid, foreignAlbumId, StringComparison.OrdinalIgnoreCase)).Select(a => Map(a, true)).ToList());

    public Task<IReadOnlyList<LidarrAlbum>> GetAlbumsByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct) => Run("album?albumIds=" + string.Join(",", ids), () =>
        (IReadOnlyList<LidarrAlbum>)Albums.Where(a => ids.Contains(a.Id)).Select(a => Map(a, true)).ToList());

    public Task<LidarrAlbum?> GetAlbumAsync(int id, CancellationToken ct) => Run("album/" + id, () =>
        Albums.FirstOrDefault(a => a.Id == id) is { } a ? Map(a, true) : null);

    public Task<LidarrAlbum> AddAlbumAsync(LidarrAddAlbum request, CancellationToken ct) => Run("post album", () =>
    {
        Writes.Add("POST album " + request.ForeignAlbumId);
        Posts.Add(request);
        if (PostRace)
        {
            PostRace = false;
            var someone = Artists.First();
            AddAlbum(request.ForeignAlbumId, someone, monitored: false);
        }

        if (Albums.Any(a => string.Equals(a.Fid, request.ForeignAlbumId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LidarrValidationException("AddAlbum", [new LidarrValidationFailure("ForeignAlbumId", "This album has already been added.")]);
        }

        var artistFid = request.Artist["foreignArtistId"]!.GetValue<string>();
        var artist = Artists.FirstOrDefault(a => string.Equals(a.Fid, artistFid, StringComparison.OrdinalIgnoreCase));
        if (artist is null)
        {
            // AddAlbumService: a missing artist is added from the body with its monitored flag (no addOptions pass).
            var name = request.Artist["artistName"]!.GetValue<string>();
            artist = AddArtist(artistFid, name, request.Artist["monitored"]?.GetValue<bool>() ?? false);
            artist.Raw["rootFolderPath"] = request.Artist["rootFolderPath"]?.GetValue<string>();
            artist.Raw["path"] = request.Artist["rootFolderPath"]?.GetValue<string>() + "/" + name;
            artist.Raw["monitorNewItems"] = request.Artist["monitorNewItems"]?.GetValue<string>();
        }

        var album = AddAlbum(request.ForeignAlbumId, artist, monitored: true);
        return Map(album, true);
    });

    public Task<IReadOnlyList<LidarrAlbum>> SetAlbumsMonitoredAsync(IReadOnlyCollection<int> albumIds, bool monitored, CancellationToken ct) => Run("put album/monitor", () =>
    {
        Writes.Add($"PUT album/monitor [{string.Join(",", albumIds)}] {(monitored ? "true" : "false")}");
        foreach (var album in Albums.Where(a => albumIds.Contains(a.Id)))
        {
            album.Monitored = monitored;
        }

        return (IReadOnlyList<LidarrAlbum>)Albums.Where(a => albumIds.Contains(a.Id)).Select(a => Map(a, true)).ToList();
    });

    private Task<T> Run<T>(string call, Func<T> body)
    {
        Calls.Add(call);
        if (Down is { } down)
        {
            return Task.FromException<T>(down);
        }

        try
        {
            return Task.FromResult(body());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private LidarrAlbum Map(Album a, bool withStats)
    {
        var artist = Artists.FirstOrDefault(x => x.Id == a.ArtistId);
        return new LidarrAlbum(
            a.Id, a.Title, "", a.ArtistId, a.Fid, a.Monitored, "Album", [], a.Release,
            [new LidarrImage("cover", "/MediaCover/Albums/" + a.Id + "/cover.jpg", "https://img.test/" + a.Fid + ".jpg")], null,
            [new LidarrRelease(null, a.Title, a.Tracks, "CD", true)],
            artist is null ? null : new LidarrArtistRef(artist.Id, artist.Name, artist.Fid, artist.Monitored),
            withStats ? a.Stats : null);
    }

    private static LidarrArtist MapArtist(Artist a) =>
        new(a.Id, a.Name, a.Fid, a.Monitored, a.Raw["monitorNewItems"]?.GetValue<string>(), (JsonObject)a.Raw.DeepClone());
}

/// <summary>The Jellyfin side of album requests: albums a person can see, and the nudges sent.</summary>
internal sealed class FakeAlbumLibrary : IAlbumLibrary
{
    public List<(string Artist, string Title, Guid Id)> Albums { get; } = [];

    public List<string> Nudges { get; } = [];

    public List<(Guid User, string Title)> Finds { get; } = [];

    public Exception? Down { get; set; }

    public Task<Guid?> FindAlbumAsync(Guid userId, string artist, string title, CancellationToken ct)
    {
        Finds.Add((userId, title));
        if (Down is { } down)
        {
            return Task.FromException<Guid?>(down);
        }

        var hit = Albums.FirstOrDefault(a => AlbumMatch.Same(a.Artist, a.Title, artist, title));
        return Task.FromResult<Guid?>(hit.Id == Guid.Empty ? null : hit.Id);
    }

    public Task NudgeAsync(string jellyfinPath, CancellationToken ct)
    {
        if (Down is { } down)
        {
            return Task.FromException(down);
        }

        Nudges.Add(jellyfinPath);
        return Task.CompletedTask;
    }
}
