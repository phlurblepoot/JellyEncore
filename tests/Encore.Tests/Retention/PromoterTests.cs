using System.Net;
using System.Security.Cryptography;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch.Import;
using Encore.Jellyfin;
using Encore.Lidarr;
using Encore.Retention;
using Encore.Store;
using Encore.Tests.Fetch;
using Encore.Tests.Lidarr;
using Encore.Tests.Requests;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;

namespace Encore.Tests.Retention;

/// <summary>
/// A Jellyfin for the promoter: items with paths, a library scan on Media/Updated that reads the test folders, stored playlist
/// entries that keep dead ones until a DELETE purges them (v04 §7.4), per-user data parked when an item goes and carried to the
/// next new item (Jellyfin's carry-over, §7.3) unless <see cref="CarryUserData"/> is off.
/// </summary>
internal sealed class PromoterJellyfin : IJellyfinClient, IRetentionJellyfin
{
    public const string RadioPrefix = "/music-radio";
    public const string LibraryPrefix = "/media/music";

    public static readonly Guid LibraryId = Guid.Parse("aaaaaaaa000000000000000000000001");

    private readonly string _radioDir;
    private readonly string _libraryDir;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _lingering = [];

    public PromoterJellyfin(string radioDir, string libraryDir)
    {
        _radioDir = radioDir;
        _libraryDir = libraryDir;
    }

    public List<UserDto> Users { get; } = [];

    /// <summary>Items by id: the Jellyfin path.</summary>
    public Dictionary<Guid, string> Items { get; } = [];

    public Dictionary<(Guid User, Guid Item), UserItemData> UserData { get; } = [];

    public Dictionary<Guid, List<Guid>> Stored { get; } = [];

    public Dictionary<Guid, HashSet<Guid>> Editors { get; } = [];

    public List<(string Kind, Guid Playlist, Guid Item, Guid? User, int? Position)> Writes { get; } = [];

    public List<(Guid Item, Guid User)> FavoriteMarks { get; } = [];

    public List<string> MediaUpdated { get; } = [];

    /// <summary>How many ItemExists polls a removed item still answers true (Jellyfin's LibraryMonitorDelay).</summary>
    public int LingerPolls { get; set; }

    public bool CarryUserData { get; set; } = true;

    public int ExistsCalls { get; private set; }

    /// <summary>Removed items stop lingering: the next ItemExists says gone.</summary>
    public void StopLingering()
    {
        lock (_gate)
        {
            _lingering.Clear();
        }
    }

    /// <summary>The visible entries of a playlist (dead ones hidden).</summary>
    public List<Guid> Visible(Guid playlist) => Stored[playlist].Where(Items.ContainsKey).ToList();

    public Guid? ItemAt(string jellyfinPath) => Items.FirstOrDefault(i => i.Value == jellyfinPath) is { Value: not null } p ? p.Key : null;

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UserDto>>(Users.ToList());

    public Task ReportMediaUpdatedAsync(IReadOnlyCollection<string> jellyfinPaths, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var path in jellyfinPaths)
            {
                MediaUpdated.Add(path);
                Scan(path);
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> ItemExistsAsync(Guid itemId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ExistsCalls++;
            if (_lingering.TryGetValue(itemId, out var left) && left > 0)
            {
                _lingering[itemId] = left - 1;
                return Task.FromResult(true);
            }

            return Task.FromResult(Items.ContainsKey(itemId));
        }
    }

    public Task<IReadOnlyList<VirtualFolderInfo>> GetVirtualFoldersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VirtualFolderInfo>>([
            new VirtualFolderInfo { Name = "Music", CollectionType = "music", ItemId = LibraryId, Locations = [LibraryPrefix, RadioPrefix] },
        ]);

    public Task<IReadOnlyList<BaseItemDto>> GetRecentAudioAsync(Guid parentId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<BaseItemDto>>(Items.Select(i => Dto(i.Key, i.Value)).Reverse().Take(limit).ToList());
        }
    }

    public Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<BaseItemDto>>(ids.Where(Items.ContainsKey).Select(id => Dto(id, Items[id])).ToList());
        }
    }

    public Task<IReadOnlyList<Guid>> GetPlaylistEntriesAsync(Guid playlistId, Guid userId, CancellationToken ct = default) =>
        Stored.ContainsKey(playlistId)
            ? Task.FromResult<IReadOnlyList<Guid>>(Visible(playlistId))
            : Task.FromException<IReadOnlyList<Guid>>(new JellyfinRequestException("GetPlaylistEntries", 404));

    public Task<PlaylistWriteResult> AddToPlaylistAsync(Guid playlistId, Guid itemId, Guid userId, int position, CancellationToken ct = default)
    {
        Writes.Add(("add", playlistId, itemId, userId, position));
        if (!Stored.TryGetValue(playlistId, out var stored))
        {
            return Task.FromResult(PlaylistWriteResult.NotFound);
        }

        if (!Editors.TryGetValue(playlistId, out var editors) || !editors.Contains(userId))
        {
            return Task.FromResult(PlaylistWriteResult.Forbidden);
        }

        stored.Insert(Math.Min(position, stored.Count), itemId);
        return Task.FromResult(PlaylistWriteResult.Ok);
    }

    public Task<PlaylistWriteResult> RemovePlaylistEntriesAsync(Guid playlistId, IReadOnlyCollection<Guid> entryIds, CancellationToken ct = default)
    {
        foreach (var id in entryIds)
        {
            Writes.Add(("remove", playlistId, id, null, null));
        }

        if (!Stored.TryGetValue(playlistId, out var stored))
        {
            return Task.FromResult(PlaylistWriteResult.NotFound);
        }

        // The rebuild from live entries drops every dead one (Folder.cs, PlaylistManager.cs).
        stored.RemoveAll(e => !Items.ContainsKey(e) || entryIds.Contains(e));
        return Task.FromResult(PlaylistWriteResult.Ok);
    }

    public Task MarkFavoriteAsync(Guid itemId, Guid userId, CancellationToken ct = default)
    {
        FavoriteMarks.Add((itemId, userId));
        UserData[(userId, itemId)] = (UserData.TryGetValue((userId, itemId), out var d) ? d : new UserItemData(false, 0)) with { IsFavorite = true };
        return Task.CompletedTask;
    }

    public Task<UserItemData?> GetUserItemDataAsync(Guid itemId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(UserData.TryGetValue((userId, itemId), out var d) ? d : (Items.ContainsKey(itemId) ? new UserItemData(false, 0) : null));

    public Task<SmartListsInfo> GetSmartListsAsync(CancellationToken ct = default) => Task.FromResult(SmartListsInfo.NotInstalled);

    /// <summary>Each user's favorite song ids (read by the retention signals; the end-to-end tests use it).</summary>
    public Dictionary<Guid, List<Guid>> Favorites { get; } = [];

    /// <summary>What GET /Sessions returns (the deleter's now-playing check).</summary>
    public List<SessionInfoDto> Sessions { get; } = [];

    /// <summary>Makes every favorites read fail (a failed signal read).</summary>
    public bool FailFavorites { get; set; }

    public Task<IReadOnlyList<BaseItemDto>> GetFavoriteAudioAsync(Guid userId, Guid? parentId = null, CancellationToken ct = default) =>
        FailFavorites ? Task.FromException<IReadOnlyList<BaseItemDto>>(new JellyfinRequestException("GetFavorites", 500)) :
        Task.FromResult<IReadOnlyList<BaseItemDto>>(Favorites.TryGetValue(userId, out var list) ? list.Select(id => new BaseItemDto { Id = id }).ToList() : []);

    /// <summary>A user's playlists are the ones that user may edit.</summary>
    public Task<IReadOnlyList<PlaylistRef>> GetPlaylistsAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlaylistRef>>(Editors.Where(e => e.Value.Contains(userId)).Select(e => new PlaylistRef(e.Key, "pl")).ToList());

    public Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionInfoDto>>(Sessions.ToList());

    public Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> GetPhysicalPathsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> SearchItemsAsync(string searchTerm, Guid userId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) => throw new NotSupportedException();

    public Task SendMessageAsync(string sessionId, string header, string text, int timeoutMs, CancellationToken ct = default) => throw new NotSupportedException();

    // Callers hold _gate. Items under the folder whose file is gone leave (parking their user data); new audio files become items.
    private void Scan(string jellyfinFolder)
    {
        var prefix = jellyfinFolder.TrimEnd('/') + "/";
        foreach (var (id, path) in Items.Where(i => i.Value.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (!File.Exists(ToDisk(path)))
            {
                Items.Remove(id);
                foreach (var key in UserData.Keys.Where(k => k.Item == id).ToList())
                {
                    var data = UserData[key];
                    UserData.Remove(key);
                    _parkedByUser[key.User] = data;
                }

                if (LingerPolls > 0)
                {
                    _lingering[id] = LingerPolls;
                }
            }
        }

        // Jellyfin refreshes the nearest existing parent.
        var disk = ToDisk(jellyfinFolder);
        while (!Directory.Exists(disk) && Path.GetDirectoryName(disk) is { } up)
        {
            disk = up;
        }

        foreach (var file in Directory.EnumerateFiles(disk, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(disk, file).Replace('\\', '/');
            if (relative.Split('/').Any(s => s.StartsWith('.')) || !PromotionPaths.AudioExtensions.Contains(Path.GetExtension(file).TrimStart('.')))
            {
                continue;
            }

            var path = ToJellyfin(file);
            if (Items.ContainsValue(path))
            {
                continue;
            }

            var id = Guid.NewGuid();
            Items[id] = path;
            if (CarryUserData)
            {
                foreach (var (user, data) in _parkedByUser)
                {
                    UserData[(user, id)] = data;
                }
            }

            _parkedByUser.Clear();
        }
    }

    private readonly Dictionary<Guid, UserItemData> _parkedByUser = [];

    private string ToDisk(string jellyfin) =>
        jellyfin.StartsWith(LibraryPrefix, StringComparison.Ordinal)
            ? Path.Combine(_libraryDir, jellyfin[LibraryPrefix.Length..].TrimStart('/'))
            : Path.Combine(_radioDir, jellyfin[RadioPrefix.Length..].TrimStart('/'));

    private string ToJellyfin(string disk)
    {
        var full = Path.GetFullPath(disk);
        var library = Path.GetFullPath(_libraryDir);
        return full.StartsWith(library, StringComparison.Ordinal)
            ? LibraryPrefix + "/" + Path.GetRelativePath(library, full).Replace('\\', '/')
            : RadioPrefix + "/" + Path.GetRelativePath(Path.GetFullPath(_radioDir), full).Replace('\\', '/');
    }

    private static BaseItemDto Dto(Guid id, string path)
    {
        var segments = path.Split('/');
        return new BaseItemDto
        {
            Id = id,
            Type = "Audio",
            MediaType = "Audio",
            Name = Path.GetFileNameWithoutExtension(path),
            Path = path,
            ArtistItems = [new NameGuidPair { Name = segments.Length >= 3 ? segments[^3] : "x", Id = Guid.NewGuid() }],
        };
    }
}

/// <summary>Lidarr's command queue and tag setting, scripted per call.</summary>
internal sealed class ScriptedLidarr : IRetentionLidarr
{
    public string Tags { get; set; } = LidarrWriteAudioTags.No;

    public List<LidarrCommand> Commands { get; } = [];

    /// <summary>After this many command reads, every command reads as completed (null: never).</summary>
    public int? CompleteAfter { get; set; }

    public int CommandReads { get; private set; }

    public Task<IReadOnlyList<LidarrCommand>> GetCommandsAsync(CancellationToken ct)
    {
        CommandReads++;
        IReadOnlyList<LidarrCommand> list = CompleteAfter is { } n && CommandReads > n
            ? Commands.Select(c => c with { Status = "completed" }).ToList()
            : Commands.ToList();
        return Task.FromResult(list);
    }

    public Task<string> GetWriteAudioTagsAsync(CancellationToken ct) => Task.FromResult(Tags);
}

internal sealed class RecordingOwned : IOwnedIndex
{
    public List<Guid> Removed { get; } = [];

    public List<OwnedEntry> Added { get; } = [];

    public bool IsLoaded => true;

    public int Count => 0;

    public DateTimeOffset? RefreshedUtc => null;

    public OwnedEntry? Resolve(TrackKey key, int? durationS) => null;

    public TrackKey? KeyOf(Guid itemId) => null;

    public void Add(OwnedEntry entry) => Added.Add(entry);

    public void Remove(Guid itemId) => Removed.Add(itemId);
}

internal sealed class RecordingShares : ISharesDirty, IUploadCheck
{
    public List<string> Dirty { get; } = [];

    public int PeersUploading { get; set; }

    public void MarkDirty(string reason) => Dirty.Add(reason);

    public Task<ActiveUploadCheck> CheckActiveAsync(long rowId, string radioPath, CancellationToken ct) =>
        Task.FromResult(new ActiveUploadCheck(true, PeersUploading));
}

internal sealed class SetFreeSpace : IFreeSpace
{
    public long? Bytes { get; set; } = 50L * 1024 * 1024 * 1024 * 1024;

    public long? FreeBytes(string path) => Bytes;
}

/// <summary>Real stores, real folders (radio and library), the fakes above and an <see cref="AutoClock"/>.</summary>
internal sealed class PromoterRig : IDisposable
{
    public static readonly Guid Ann = Guid.Parse("11111111000000000000000000000001");
    public static readonly Guid Bob = Guid.Parse("22222222000000000000000000000002");
    public static readonly Guid Cat = Guid.Parse("33333333000000000000000000000003");

    public PromoterRig()
    {
        Directory.CreateDirectory(Radio);
        Directory.CreateDirectory(Library);
        var db = Path.Combine(Dir, "encore.db");
        Main = EncoreStore.Open(db, Time);
        Fetch = FetchStore.Open(db);
        Store = RetentionStore.Open(db);
        Requests = RequestStore.Open(db);
        Jellyfin = new PromoterJellyfin(Radio, Library);
        Jellyfin.Users.AddRange([new UserDto { Id = Ann, Name = "ann" }, new UserDto { Id = Bob, Name = "bob" }, new UserDto { Id = Cat, Name = "cat" }]);
        Options = new EncoreOptions
        {
            JellyfinUrl = new Uri("http://jellyfin.test:8096"),
            JellyfinApiKey = "test-key-not-real",
            AllowedUsers = ["ann"],
            DataDir = Dir,
            RadioDir = Radio,
            RadioJellyfinDir = PromoterJellyfin.RadioPrefix,
            LibraryDir = Library,
            LibraryJellyfinDir = PromoterJellyfin.LibraryPrefix,
            RetentionMode = RetentionMode.Live,
        };
    }

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "encore-promote-" + Guid.NewGuid().ToString("N"));

    public string Radio => Path.Combine(Dir, "radio");

    public string Library => Path.Combine(Dir, "library");

    public string Incoming => Path.Combine(Library, ".encore-incoming");

    public AutoClock Time { get; } = new(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero));

    public EncoreStore Main { get; }

    public FetchStore Fetch { get; }

    public RetentionStore Store { get; }

    public RequestStore Requests { get; }

    public PromoterJellyfin Jellyfin { get; }

    public FakeLidarrApi LidarrApi { get; } = NoneDefaults();

    public ScriptedLidarr Lidarr { get; } = new();

    public bool LidarrConfigured { get; set; } = true;

    public RecordingOwned Owned { get; } = new();

    public RecordingShares Shares { get; } = new();

    public SetFreeSpace Free { get; } = new();

    public ListLogger<Promoter> Log { get; } = new();

    public EncoreOptions Options { get; }

    public Promoter Make(Action<string>? checkpoint = null) =>
        new(Fetch, Store, Requests, Jellyfin, Jellyfin, Owned,
            LidarrConfigured ? new LidarrGuard(LidarrApi, Lidarr) : new LidarrGuard(null, null),
            Free, Shares, Shares, Options, Time, Log, hooks: new PromoterHooks(checkpoint));

    /// <summary>A ready row with a real radio file <c>soulseek/{artist}/{album}/{file}</c> and its Jellyfin item.</summary>
    public Song Ready(string file = "01 - Song.flac", string artist = "Artist", string album = "Album (2020)", string title = "Song", int? disc = null, string content = "audio bytes")
    {
        var item = Guid.NewGuid();
        var path = Path.Combine(Radio, SafeNames.SoulseekFolder, artist, album, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content + " " + Guid.NewGuid().ToString("N"));
        var jellyfin = $"{PromoterJellyfin.RadioPrefix}/{SafeNames.SoulseekFolder}/{artist}/{album}/{file}";
        var at = Time.GetUtcNow() - TimeSpan.FromDays(3);
        var id = Fetch.InsertFetched(new NewFetchedFile("job-" + item.ToString("N"), "artist|" + title.ToLowerInvariant(), artist, title, "deezer_radio", "soulseek", "run-1", "s1", Ann, at, DiscNo: disc));
        Fetch.UpdateFetched(id, FetchState.Ready, new FetchedUpdate(Path: path, JellyfinPath: jellyfin, JellyfinItemId: item.ToString("N"), ReadyUtc: at));
        Fetch.MarkKept(id, "favorite", at);
        Jellyfin.Items[item] = jellyfin;
        return new Song(id, item, path, Hash(path), $"{artist}/{album}");
    }

    public Guid Playlist(Guid[] entries, Guid[] editors)
    {
        var id = Guid.NewGuid();
        Jellyfin.Stored[id] = [.. entries];
        Jellyfin.Editors[id] = [.. editors];
        return id;
    }

    public static PlaylistMembership Member(Guid playlist, int index, params Guid[] candidates) => new(playlist, "pl", candidates, index);

    public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public int Events(int id) => Log.Entries.Count(e => e.EventId.Id == id);

    public LogEntry Event(int id) => Log.Entries.Last(e => e.EventId.Id == id);

    public IEnumerable<string> LibraryFiles() =>
        Directory.EnumerateFiles(Library, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Library, f).Replace('\\', '/')).Order(StringComparer.Ordinal);

    private static FakeLidarrApi NoneDefaults()
    {
        var api = new FakeLidarrApi();
        api.Roots.Clear();
        api.Roots.Add(new LidarrRootFolder(1, "/media/music", 3, 2, true, "none", "none"));
        return api;
    }

    public void Dispose()
    {
        Requests.Dispose();
        Store.Dispose();
        Fetch.Dispose();
        Main.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

internal sealed record Song(long Id, Guid Item, string Path, string Sha, string Folder);

/// <summary>The promoter (v04 §7, §14 T129).</summary>
public sealed class PromoterTests : IDisposable
{
    private readonly PromoterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string Target(string relative) => Path.Combine(_rig.Library, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task PM01_happy_path_moves_the_song_keeps_playlist_places_and_favorites_and_updates_every_record()
    {
        var song = _rig.Ready();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        _rig.Jellyfin.Items[a] = "/media/music/x/a.flac";
        _rig.Jellyfin.Items[b] = "/media/music/x/b.flac";
        _rig.Jellyfin.Items[c] = "/media/music/x/c.flac";
        var annList = _rig.Playlist([a, song.Item, b], [PromoterRig.Ann]);
        var bobList = _rig.Playlist([song.Item, c], [PromoterRig.Bob]);
        _rig.Jellyfin.UserData[(PromoterRig.Ann, song.Item)] = new UserItemData(true, 4);
        _rig.Requests.Insert(new NewRequest("r1", "song", PromoterRig.Ann.ToString("N"), "ann", "Artist", _rig.Time.GetUtcNow(), Title: "Song"));
        _rig.Requests.Update("r1", RequestState.Ready, _rig.Time.GetUtcNow(), new RequestUpdate(JellyfinItemId: song.Item.ToString("N")));
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, [
            PromoterRig.Member(annList, 1, PromoterRig.Ann, PromoterRig.Bob),
            PromoterRig.Member(bobList, 0, PromoterRig.Bob),
        ]);

        Assert.Equal(PromotionOutcome.Done, result.Outcome);
        var target = Target("Artist/Album (2020)/01 - Song.flac");
        Assert.True(File.Exists(target));
        Assert.Equal(song.Sha, PromoterRig.Hash(target));
        Assert.False(File.Exists(song.Path));
        Assert.False(Directory.Exists(Path.Combine(_rig.Radio, "soulseek", "Artist")));
        Assert.True(Directory.Exists(Path.Combine(_rig.Radio, "soulseek")));
        Assert.Empty(Directory.EnumerateFiles(_rig.Incoming));

        var newItem = _rig.Jellyfin.ItemAt("/media/music/Artist/Album (2020)/01 - Song.flac")!.Value;
        Assert.Equal([a, newItem, b], _rig.Jellyfin.Stored[annList]);
        Assert.Equal([newItem, c], _rig.Jellyfin.Stored[bobList]);
        Assert.True(_rig.Jellyfin.UserData[(PromoterRig.Ann, newItem)].IsFavorite);
        Assert.Empty(_rig.Jellyfin.FavoriteMarks);

        var row = _rig.Fetch.GetTracked(song.Id)!;
        Assert.Equal(FetchState.Promoted, row.State);
        Assert.Equal(target, row.Path);
        Assert.Equal(song.Path, row.RadioPath);
        Assert.Equal("/media/music/Artist/Album (2020)/01 - Song.flac", row.JellyfinPath);
        Assert.Equal(newItem.ToString("N"), row.JellyfinItemId);
        Assert.Equal(newItem.ToString("N"), _rig.Requests.Get("r1")!.JellyfinItemId);
        Assert.Equal([song.Item], _rig.Owned.Removed);
        Assert.Equal(newItem, Assert.Single(_rig.Owned.Added).ItemId);
        Assert.Equal(["promote"], _rig.Shares.Dirty);
        Assert.Equal(PromotionStep.Done, _rig.Store.GetPromotion(song.Id)!.Step);
        Assert.Empty(_rig.Store.ActivePromotions());
        Assert.Equal(1, _rig.Events(8520));
        Assert.Equal(1, _rig.Event(8550).Fields["FavoritesCarried"]);

        // The old album folder was reported before the new folder, and Jellyfin only saw the file after the old item was gone.
        var updates = _rig.Jellyfin.MediaUpdated;
        Assert.True(updates.IndexOf("/music-radio/soulseek/Artist/Album (2020)") < updates.IndexOf("/media/music/Artist/Album (2020)"));
    }

    [Fact]
    public async Task PM02_the_purge_DELETE_goes_once_per_playlist_after_its_adds()
    {
        var song = _rig.Ready();
        var x = Guid.NewGuid();
        _rig.Jellyfin.Items[x] = "/media/music/x/x.flac";
        var p1 = _rig.Playlist([song.Item, x], [PromoterRig.Ann]);
        var p2 = _rig.Playlist([x, song.Item], [PromoterRig.Ann]);
        using var promoter = _rig.Make();

        await promoter.PromoteAsync(song.Id, [PromoterRig.Member(p1, 0, PromoterRig.Ann), PromoterRig.Member(p2, 1, PromoterRig.Ann)]);

        foreach (var p in new[] { p1, p2 })
        {
            var writes = _rig.Jellyfin.Writes.Where(w => w.Playlist == p).ToList();
            Assert.Equal(["add", "remove"], writes.Select(w => w.Kind));
            Assert.Equal(song.Item, writes[1].Item);
            Assert.Null(writes[1].User);
        }
    }

    [Fact]
    public async Task PM03_a_song_twice_in_one_playlist_keeps_both_places_through_descending_adds()
    {
        var song = _rig.Ready();
        Guid x = Guid.NewGuid(), y = Guid.NewGuid();
        _rig.Jellyfin.Items[x] = "/media/music/x/x.flac";
        _rig.Jellyfin.Items[y] = "/media/music/x/y.flac";
        var list = _rig.Playlist([song.Item, x, song.Item, y], [PromoterRig.Ann]);
        using var promoter = _rig.Make();

        await promoter.PromoteAsync(song.Id, [PromoterRig.Member(list, 0, PromoterRig.Ann), PromoterRig.Member(list, 2, PromoterRig.Ann)]);

        var newItem = _rig.Jellyfin.ItemAt("/media/music/Artist/Album (2020)/01 - Song.flac")!.Value;
        Assert.Equal([newItem, x, newItem, y], _rig.Jellyfin.Stored[list]);
        Assert.Equal([2, 0], _rig.Jellyfin.Writes.Where(w => w.Kind == "add").Select(w => w.Position!.Value));
        Assert.Equal(2, _rig.Events(8560));
    }

    [Fact]
    public async Task PM04_the_editor_is_the_first_candidate_not_refused_and_is_stored_and_reused()
    {
        var song = _rig.Ready();
        var x = Guid.NewGuid();
        _rig.Jellyfin.Items[x] = "/media/music/x/x.flac";
        var list = _rig.Playlist([song.Item, x, song.Item], [PromoterRig.Ann]);
        using var promoter = _rig.Make();

        await promoter.PromoteAsync(song.Id, [
            PromoterRig.Member(list, 0, PromoterRig.Bob, PromoterRig.Ann),
            PromoterRig.Member(list, 2, PromoterRig.Bob, PromoterRig.Ann),
        ]);

        var adds = _rig.Jellyfin.Writes.Where(w => w.Kind == "add").Select(w => (w.User, w.Position)).ToList();
        Assert.Equal([(PromoterRig.Bob, 2), (PromoterRig.Ann, 2), (PromoterRig.Ann, 0)], adds.Select(a => (a.User!.Value, a.Position!.Value)));
        Assert.All(_rig.Store.Entries(song.Id), e => Assert.Equal(PromoterRig.Ann.ToString("N"), e.EditorUserId));
    }

    [Fact]
    public async Task PM05_every_candidate_refused_logs_8570_and_counts_an_attempt()
    {
        var song = _rig.Ready();
        var list = _rig.Playlist([song.Item], [PromoterRig.Cat]);
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, [PromoterRig.Member(list, 0, PromoterRig.Ann, PromoterRig.Bob)]);

        Assert.Equal(PromotionOutcome.Failed, result.Outcome);
        Assert.Equal(PromotionStep.Playlists, result.Step);
        Assert.Equal(403, _rig.Event(8570).Fields["Status"]);
        var promo = _rig.Store.GetPromotion(song.Id)!;
        Assert.Equal((PromotionStep.Playlists, 1, false), (promo.Step, promo.Attempts, promo.Stalled));
        Assert.Equal(1, _rig.Events(8530));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(song.Id)!.State);

        // The owner fixes the share; the next pass finishes the promotion.
        _rig.Jellyfin.Editors[list].Add(PromoterRig.Bob);
        Assert.Equal(PromotionOutcome.Done, (await promoter.PromoteAsync(song.Id, [])).Outcome);
        Assert.Single(_rig.Jellyfin.Stored[list]);
    }

    [Fact]
    public async Task PM06_a_crash_between_an_Add_and_its_mark_adds_no_second_entry()
    {
        var song = _rig.Ready();
        var x = Guid.NewGuid();
        _rig.Jellyfin.Items[x] = "/media/music/x/x.flac";
        var list = _rig.Playlist([song.Item, x, song.Item], [PromoterRig.Ann]);
        IReadOnlyList<PlaylistMembership> members = [PromoterRig.Member(list, 0, PromoterRig.Ann), PromoterRig.Member(list, 2, PromoterRig.Ann)];
        var crashed = false;
        using (var first = _rig.Make(point =>
        {
            if (point == "playlist_added" && !crashed)
            {
                crashed = true;
                throw new OperationCanceledException("killed");
            }
        }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.PromoteAsync(song.Id, members));
        }

        using var second = _rig.Make();
        var result = await second.PromoteAsync(song.Id, members);

        Assert.Equal(PromotionOutcome.Done, result.Outcome);
        var newItem = _rig.Jellyfin.ItemAt("/media/music/Artist/Album (2020)/01 - Song.flac")!.Value;
        Assert.Equal([newItem, x, newItem], _rig.Jellyfin.Stored[list]);
        Assert.Equal(2, _rig.Jellyfin.Writes.Count(w => w.Kind == "add"));
        Assert.Equal(1, _rig.Jellyfin.Writes.Count(w => w.Kind == "remove"));
    }

    [Fact]
    public async Task PM07_an_old_item_slow_to_vanish_fails_the_step_then_the_next_pass_resumes_with_a_new_media_update()
    {
        var song = _rig.Ready();
        _rig.Jellyfin.LingerPolls = 1000;
        using var promoter = _rig.Make();

        var first = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal((PromotionOutcome.Failed, PromotionStep.OldGone), (first.Outcome, first.Step));
        Assert.Contains("old_item_still_in_jellyfin", first.Reason);
        Assert.True(_rig.Jellyfin.ExistsCalls >= 60);
        Assert.DoesNotContain(_rig.LibraryFiles(), f => !f.StartsWith(".encore-incoming/", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_rig.Incoming, $"{song.Id}.flac.encore-part")));
        var reports = _rig.Jellyfin.MediaUpdated.Count;

        _rig.Jellyfin.StopLingering();
        var second = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(PromotionOutcome.Done, second.Outcome);
        Assert.Equal("/music-radio/soulseek/Artist/Album (2020)", _rig.Jellyfin.MediaUpdated[reports]);
        Assert.Equal(1, _rig.Store.GetPromotion(song.Id)!.Attempts);
    }

    [Theory]
    [InlineData("staged_written")]
    [InlineData("step:copied")]
    [InlineData("step:source_removed")]
    [InlineData("step:old_gone")]
    [InlineData("step:placed")]
    [InlineData("step:ingested")]
    [InlineData("step:verified")]
    [InlineData("step:playlists")]
    public async Task PM08_a_restart_at_any_step_resumes_without_a_second_copy_or_a_lost_file(string point)
    {
        var song = _rig.Ready();
        var x = Guid.NewGuid();
        _rig.Jellyfin.Items[x] = "/media/music/x/x.flac";
        var list = _rig.Playlist([x, song.Item], [PromoterRig.Ann]);
        _rig.Jellyfin.UserData[(PromoterRig.Ann, song.Item)] = new UserItemData(true, 1);
        IReadOnlyList<PlaylistMembership> members = [PromoterRig.Member(list, 1, PromoterRig.Ann)];
        using (var first = _rig.Make(p =>
        {
            if (p == point)
            {
                throw new OperationCanceledException("killed at " + p);
            }
        }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.PromoteAsync(song.Id, members));
        }

        // The song exists somewhere at every moment: the radio file, the staged copy or the library file.
        var staged = Path.Combine(_rig.Incoming, $"{song.Id}.flac.encore-part");
        var target = Target("Artist/Album (2020)/01 - Song.flac");
        Assert.True(File.Exists(song.Path) || (File.Exists(staged) && PromoterRig.Hash(staged) == song.Sha) || File.Exists(target));

        using var second = _rig.Make();
        var result = await second.PromoteAsync(song.Id, members);

        Assert.Equal(PromotionOutcome.Done, result.Outcome);
        Assert.Equal(["Artist/Album (2020)/01 - Song.flac"], _rig.LibraryFiles());
        Assert.Equal(song.Sha, PromoterRig.Hash(target));
        Assert.False(File.Exists(song.Path));
        var newItem = _rig.Jellyfin.ItemAt("/media/music/Artist/Album (2020)/01 - Song.flac")!.Value;
        Assert.Equal([x, newItem], _rig.Jellyfin.Stored[list]);
        Assert.Equal(1, _rig.Jellyfin.Writes.Count(w => w.Kind == "add"));
        Assert.True(_rig.Jellyfin.UserData[(PromoterRig.Ann, newItem)].IsFavorite);
        Assert.Equal(FetchState.Promoted, _rig.Fetch.GetTracked(song.Id)!.State);
        Assert.Equal(0, _rig.Store.GetPromotion(song.Id)!.Attempts);
    }

    [Fact]
    public async Task PM09_the_staged_copy_is_named_encore_part_and_takes_the_real_extension_when_placed()
    {
        var song = _rig.Ready(file: "03 - Tune.mp3", title: "Tune");
        string? stagedSeen = null;
        using var promoter = _rig.Make(p =>
        {
            if (p == "step:source_removed")
            {
                stagedSeen = Directory.EnumerateFiles(_rig.Incoming).Single();
            }
        });

        await promoter.PromoteAsync(song.Id, []);

        Assert.Equal($"{song.Id}.mp3.encore-part", Path.GetFileName(stagedSeen));
        Assert.Equal(["Artist/Album (2020)/03 - Tune.mp3"], _rig.LibraryFiles());
    }

    [Fact]
    public async Task PM10_a_hash_mismatch_keeps_the_radio_file_and_the_next_try_copies_again()
    {
        var song = _rig.Ready();
        var corrupt = true;
        using var promoter = _rig.Make(p =>
        {
            if (p == "staged_written" && corrupt)
            {
                corrupt = false;
                File.AppendAllText(Directory.EnumerateFiles(_rig.Incoming).Single(), "!");
            }
        });

        var first = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal((PromotionOutcome.Failed, PromotionStep.Copied), (first.Outcome, first.Step));
        Assert.Contains("copy_mismatch", first.Reason);
        Assert.True(File.Exists(song.Path));
        Assert.Empty(Directory.EnumerateFiles(_rig.Incoming));

        Assert.Equal(PromotionOutcome.Done, (await promoter.PromoteAsync(song.Id, [])).Outcome);
        Assert.Equal(song.Sha, PromoterRig.Hash(Target("Artist/Album (2020)/01 - Song.flac")));
    }

    [Theory]
    [InlineData("01 - Song.flac")]
    [InlineData("01 - SONG.mp3")]
    public async Task PM11_a_library_file_with_the_target_name_means_duplicate_in_library(string existing)
    {
        var song = _rig.Ready();
        Directory.CreateDirectory(Target("artist/album (2020)"));
        File.WriteAllText(Path.Combine(Target("artist/album (2020)"), existing), "the library's own copy");
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(PromotionOutcome.Duplicate, result.Outcome);
        Assert.Equal(1, _rig.Events(8580));
        Assert.Null(_rig.Store.GetPromotion(song.Id));
        Assert.True(File.Exists(song.Path));
        var row = _rig.Fetch.GetTracked(song.Id)!;
        Assert.Equal((FetchState.Ready, "favorite"), (row.State, row.KeptReason));
        Assert.Equal("the library's own copy", File.ReadAllText(Path.Combine(Target("artist/album (2020)"), existing)));
    }

    [Theory]
    [InlineData("all", "none")]
    [InlineData("none", "all")]
    [InlineData("none", "new")]
    [InlineData(null, "none")]
    public async Task PM12_Lidarr_root_folder_defaults_other_than_none_pause_promotions(string? monitor, string? newItems)
    {
        var song = _rig.Ready();
        _rig.LidarrApi.Roots[0] = new LidarrRootFolder(1, "/media/music", 3, 2, true, monitor, newItems);
        using var promoter = _rig.Make();

        var results = await promoter.RunPassAsync([song.Id], Signals(), 20);

        Assert.Equal((PromotionOutcome.Paused, PromotionPause.LidarrDefaults), (results.Single().Outcome, results.Single().Reason));
        Assert.Equal(PromotionPause.LidarrDefaults, _rig.Event(8590).Fields["Reason"]);
        Assert.True(File.Exists(song.Path));
        Assert.Null(_rig.Store.GetPromotion(song.Id));
    }

    [Theory]
    [InlineData(LidarrWriteAudioTags.AllFiles, true)]
    [InlineData(LidarrWriteAudioTags.Sync, true)]
    [InlineData(LidarrWriteAudioTags.NewFiles, false)]
    [InlineData(LidarrWriteAudioTags.No, false)]
    public async Task PM13_Lidarr_tag_writing_pauses_only_on_allFiles_or_sync(string tags, bool paused)
    {
        var song = _rig.Ready();
        _rig.Lidarr.Tags = tags;
        using var promoter = _rig.Make();

        var guard = await promoter.CheckGuardsAsync();
        var results = await promoter.RunPassAsync([song.Id], Signals(), 20);

        Assert.Equal(!paused, guard.Ok);
        Assert.Equal(paused ? PromotionOutcome.Paused : PromotionOutcome.Done, results.Single().Outcome);
        if (paused)
        {
            Assert.Equal(PromotionPause.LidarrTags, guard.Reason);
        }
    }

    [Theory]
    [InlineData("RescanFolders", "queued", true)]
    [InlineData("RetagArtist", "started", true)]
    [InlineData("RenameArtist", "queued", true)]
    [InlineData("DownloadedAlbumsScan", "started", true)]
    [InlineData("RescanFolders", "completed", false)]
    [InlineData("RefreshMonitoredDownloads", "started", false)]
    public async Task PM14_a_running_Lidarr_import_command_makes_placing_wait(string name, string status, bool waits)
    {
        var song = _rig.Ready();
        _rig.Lidarr.Commands.Add(new LidarrCommand(7, name, status));
        _rig.Lidarr.CompleteAfter = 3;
        using var promoter = _rig.Make();

        Assert.Equal(PromotionOutcome.Done, (await promoter.PromoteAsync(song.Id, [])).Outcome);

        if (waits)
        {
            var wait = _rig.Event(8510);
            Assert.False((bool)wait.Fields["PlacedAnyway"]!);
            Assert.Equal(30L, wait.Fields["WaitedSec"]);
            Assert.Equal(name, wait.Fields["Commands"]);
        }
        else
        {
            Assert.Equal(0, _rig.Events(8510));
        }
    }

    [Fact]
    public async Task PM15_a_Lidarr_command_running_over_10_minutes_is_waited_out_then_the_file_is_placed_anyway()
    {
        var song = _rig.Ready();
        _rig.Lidarr.Commands.Add(new LidarrCommand(7, "RescanFolders", "started"));
        using var promoter = _rig.Make();

        Assert.Equal(PromotionOutcome.Done, (await promoter.PromoteAsync(song.Id, [])).Outcome);

        var wait = _rig.Event(8510);
        Assert.True((bool)wait.Fields["PlacedAnyway"]!);
        Assert.Equal(600L, wait.Fields["WaitedSec"]);
        Assert.True(File.Exists(Target("Artist/Album (2020)/01 - Song.flac")));
    }

    [Fact]
    public async Task PM16_a_favorite_that_did_not_carry_over_is_repaired()
    {
        var song = _rig.Ready();
        _rig.Jellyfin.CarryUserData = false;
        _rig.Jellyfin.UserData[(PromoterRig.Bob, song.Item)] = new UserItemData(true, 2);
        _rig.Jellyfin.UserData[(PromoterRig.Cat, song.Item)] = new UserItemData(false, 5);
        using var promoter = _rig.Make();

        Assert.Equal(PromotionOutcome.Done, (await promoter.PromoteAsync(song.Id, [])).Outcome);

        var newItem = _rig.Jellyfin.ItemAt("/media/music/Artist/Album (2020)/01 - Song.flac")!.Value;
        Assert.Equal([(newItem, PromoterRig.Bob)], _rig.Jellyfin.FavoriteMarks);
        var data = _rig.Event(8550).Fields;
        Assert.Equal((2, 0, 1, 0), ((int)data["Users"]!, (int)data["FavoritesCarried"]!, (int)data["FavoritesRepaired"]!, (int)data["PlayCountsCarried"]!));
        Assert.Equal(2, _rig.Store.UserData(song.Id).Count);
    }

    [Fact]
    public async Task PM17_a_SmartLists_playlist_is_left_untouched()
    {
        var song = _rig.Ready();
        var smart = _rig.Playlist([song.Item], [PromoterRig.Ann]);
        var hand = _rig.Playlist([song.Item], [PromoterRig.Ann]);
        var signals = Signals(
            new SignalPlaylist(smart, "Mix [Smart]", [PromoterRig.Ann], PlaylistSkipReason.SmartSuffix, [song.Item], true),
            new SignalPlaylist(hand, "Mine", [PromoterRig.Ann], null, [song.Item], true));
        using var promoter = _rig.Make();

        var results = await promoter.RunPassAsync([song.Id], signals, 20);

        Assert.Equal(PromotionOutcome.Done, results.Single().Outcome);
        Assert.DoesNotContain(_rig.Jellyfin.Writes, w => w.Playlist == smart);
        Assert.Contains(_rig.Jellyfin.Writes, w => w.Playlist == hand && w.Kind == "add");
        Assert.Equal([hand.ToString("N")], _rig.Store.Entries(song.Id).Select(e => e.PlaylistId));
    }

    [Fact]
    public async Task PM18_low_disk_space_pauses_promotions_before_any_copy()
    {
        var song = _rig.Ready();
        _rig.Free.Bytes = 10L * 1024 * 1024 * 1024;
        using var promoter = _rig.Make();

        var results = await promoter.RunPassAsync([song.Id], Signals(), 20);
        Assert.Equal((PromotionOutcome.Paused, PromotionPause.DiskLow), (results.Single().Outcome, results.Single().Reason));

        // A promotion started anyway (no pass guard) stops at the copy step without counting an attempt.
        var direct = await promoter.PromoteAsync(song.Id, []);
        Assert.Equal((PromotionOutcome.Paused, PromotionStep.Copied), (direct.Outcome, direct.Step));
        Assert.Equal(0, _rig.Store.GetPromotion(song.Id)!.Attempts);
        Assert.True(File.Exists(song.Path));
        Assert.False(Directory.Exists(_rig.Incoming) && Directory.EnumerateFiles(_rig.Incoming).Any());
    }

    [Fact]
    public async Task PM19_new_folders_are_0755_and_the_file_0644()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix modes: checked on Linux CI
        }

        var song = _rig.Ready();
        using var promoter = _rig.Make();

        await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(RetentionLimits.NewFolderMode, File.GetUnixFileMode(Target("Artist")));
        Assert.Equal(RetentionLimits.NewFolderMode, File.GetUnixFileMode(Target("Artist/Album (2020)")));
        Assert.Equal(RetentionLimits.NewFileMode, File.GetUnixFileMode(Target("Artist/Album (2020)/01 - Song.flac")));
    }

    [Fact]
    public async Task PM20_five_failed_attempts_stall_the_promotion_and_it_is_not_retried()
    {
        var song = _rig.Ready();
        var list = _rig.Playlist([song.Item], [PromoterRig.Cat]);
        using var promoter = _rig.Make();
        IReadOnlyList<PlaylistMembership> members = [PromoterRig.Member(list, 0, PromoterRig.Ann)];

        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal(PromotionOutcome.Failed, (await promoter.PromoteAsync(song.Id, members)).Outcome);
        }

        Assert.Equal(PromotionOutcome.Stalled, (await promoter.PromoteAsync(song.Id, members)).Outcome);
        Assert.Equal(1, _rig.Events(8540));
        var promo = _rig.Store.GetPromotion(song.Id)!;
        Assert.Equal((5, true), (promo.Attempts, promo.Stalled));

        var writes = _rig.Jellyfin.Writes.Count;
        Assert.Equal(PromotionOutcome.Stalled, (await promoter.PromoteAsync(song.Id, members)).Outcome);
        Assert.Equal(writes, _rig.Jellyfin.Writes.Count);
        Assert.Equal(5, _rig.Events(8530));
    }

    [Fact]
    public async Task PM21_an_active_upload_defers_the_source_removal()
    {
        var song = _rig.Ready();
        _rig.Shares.PeersUploading = 1;
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal((PromotionOutcome.Deferred, PromotionStep.SourceRemoved), (result.Outcome, result.Step));
        Assert.True(File.Exists(song.Path));
        Assert.Equal(0, _rig.Store.GetPromotion(song.Id)!.Attempts);
        Assert.Empty(_rig.Owned.Removed);

        _rig.Shares.PeersUploading = 0;
        Assert.Equal(PromotionOutcome.Done, (await promoter.PromoteAsync(song.Id, [])).Outcome);
    }

    [Fact]
    public async Task PM22_the_pass_budget_counts_promotions_and_resumes_the_one_in_progress_first()
    {
        var first = _rig.Ready(file: "01 - One.flac", title: "One");
        var second = _rig.Ready(file: "02 - Two.flac", title: "Two");
        var third = _rig.Ready(file: "03 - Three.flac", title: "Three");
        _rig.Shares.PeersUploading = 1;
        using var promoter = _rig.Make();
        await promoter.PromoteAsync(third.Id, []); // deferred mid-way: in progress
        _rig.Shares.PeersUploading = 0;

        var results = await promoter.RunPassAsync([first.Id, second.Id, third.Id], Signals(), 2);

        Assert.Equal([third.Id, first.Id], results.Select(r => r.RowId));
        Assert.All(results, r => Assert.Equal(PromotionOutcome.Done, r.Outcome));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(second.Id)!.State);
    }

    [Fact]
    public async Task PM23_without_Lidarr_the_guards_are_skipped()
    {
        var song = _rig.Ready();
        _rig.LidarrConfigured = false;
        _rig.LidarrApi.Roots[0] = new LidarrRootFolder(1, "/media/music", 3, 2, true, "all", "all");
        using var promoter = _rig.Make();

        var guard = await promoter.CheckGuardsAsync();

        Assert.True(guard.Ok);
        Assert.False(guard.Lidarr.Checked);
        Assert.Empty(_rig.LidarrApi.Calls);
    }

    // ---- target path rules (v04 §7.1) ----

    [Fact]
    public void PP01_an_existing_artist_and_album_folder_is_joined_ignoring_case()
    {
        Directory.CreateDirectory(Target("ARTIST/album (2020)"));
        var paths = PromotionPaths.From(_rig.Options);

        var target = paths.Plan(Path.Combine(_rig.Radio, "soulseek", "Artist", "Album (2020)", "01 - Song.flac"), null, "Song")!;

        Assert.Equal(("ARTIST", "album (2020)", "01 - Song.flac"), (target.ArtistDir, target.AlbumDir, target.FileName));
        Assert.Equal(Target("ARTIST/album (2020)/01 - Song.flac"), target.Path);
        Assert.Equal("/media/music/ARTIST/album (2020)/01 - Song.flac", target.JellyfinPath);
        Assert.Equal("/media/music/ARTIST/album (2020)", target.JellyfinFolder);
        Assert.Null(target.Collision);
    }

    [Fact]
    public void PP02_an_existing_artist_with_a_new_album_joins_the_artist_only()
    {
        Directory.CreateDirectory(Target("artist/Other (1999)"));
        var target = PromotionPaths.From(_rig.Options).Plan(Path.Combine(_rig.Radio, "soulseek", "Artist", "Album (2020)", "01 - Song.flac"), null, "Song")!;

        Assert.Equal(Target("artist/Album (2020)/01 - Song.flac"), target.Path);
    }

    [Theory]
    [InlineData("01 - Song.flac", 2, "Song", "2-01 - Song.flac")]
    [InlineData("01 - Song.flac", 1, "Song", "01 - Song.flac")]
    [InlineData("12 - Song.flac", 3, "Song", "3-12 - Song.flac")]
    [InlineData("Song.flac", 2, "Song", "Song.flac")]
    [InlineData("01 - Song (2).flac", null, "Song", "01 - Song.flac")]
    [InlineData("01 - Part (2).flac", null, "Part (2)", "01 - Part (2).flac")]
    [InlineData("01 - Song (2).flac", null, null, "01 - Song (2).flac")]
    public void PP03_file_names_follow_the_Lidarr_layout(string radioName, int? disc, string? title, string expected)
    {
        var target = PromotionPaths.From(_rig.Options).Plan(Path.Combine(_rig.Radio, "soulseek", "A", "Singles", radioName), disc, title)!;

        Assert.Equal(expected, target.FileName);
        Assert.Equal("Singles", target.AlbumDir);
    }

    [Fact]
    public void PP04_a_path_outside_the_radio_layout_has_no_target()
    {
        var paths = PromotionPaths.From(_rig.Options);

        Assert.Null(paths.Plan(Path.Combine(_rig.Radio, "soulseek", "loose.flac"), null));
        Assert.Null(paths.Plan(Path.Combine(_rig.Dir, "elsewhere", "a", "b", "c.flac"), null));
    }

    [Fact]
    public async Task PP05_the_report_target_uses_the_row_disc_number()
    {
        var song = _rig.Ready(disc: 2);
        var target = PromotionPaths.From(_rig.Options).ReportTarget(_rig.Fetch)(_rig.Fetch.GetTracked(song.Id)!);

        Assert.Equal(Target("Artist/Album (2020)/2-01 - Song.flac"), target);
        using var promoter = _rig.Make();
        await promoter.PromoteAsync(song.Id, []);
        Assert.Equal(["Artist/Album (2020)/2-01 - Song.flac"], _rig.LibraryFiles());
    }

    // ---- Lidarr root folder defaults on the wire (T124 note) ----

    [Theory]
    [InlineData("\"none\"", "\"none\"", "none", "none")]
    [InlineData("6", "1", "none", "none")]
    [InlineData("0", "0", "all", "all")]
    public async Task LG01_root_folder_defaults_are_read_as_names_from_strings_or_numbers(string monitor, string newItems, string expectMonitor, string expectNew)
    {
        var json = "[{\"id\":1,\"path\":\"/media/music\",\"defaultMetadataProfileId\":2,\"defaultQualityProfileId\":3,\"defaultMonitorOption\":" + monitor
            + ",\"defaultNewItemMonitorOption\":" + newItems + ",\"accessible\":true}]";
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, json);
        using var http = new HttpClient(fake) { BaseAddress = new Uri("http://lidarr:8686/") };
        var client = new LidarrClient(http, new LidarrClientOptions());

        var root = Assert.Single(await client.GetRootFoldersAsync(default));

        Assert.Equal((expectMonitor, expectNew), (root.DefaultMonitorOption, root.DefaultNewItemMonitorOption));
    }

    [Fact]
    public async Task LG02_root_folder_defaults_missing_from_the_reply_read_as_null()
    {
        var fake = new FakeLidarr().Reply(HttpStatusCode.OK, "[{\"id\":1,\"path\":\"/media/music\",\"accessible\":true}]");
        using var http = new HttpClient(fake) { BaseAddress = new Uri("http://lidarr:8686/") };
        var root = Assert.Single(await new LidarrClient(http, new LidarrClientOptions()).GetRootFoldersAsync(default));

        Assert.Null(root.DefaultMonitorOption);
        Assert.Null(root.DefaultNewItemMonitorOption);
    }

    [Fact]
    public async Task LG03_an_unreadable_Lidarr_pauses_promotions()
    {
        _rig.LidarrApi.Down = new LidarrRequestException("GetRootFolders", 503);
        using var promoter = _rig.Make();

        var guard = await promoter.CheckGuardsAsync();

        Assert.Equal((false, PromotionPause.LidarrUnreadable), (guard.Ok, guard.Reason));
    }

    private static SignalsSnapshot Signals(params SignalPlaylist[] playlists) =>
        new(DateTimeOffset.UnixEpoch, 3, new HashSet<Guid>(), playlists, false, true, []);
}
