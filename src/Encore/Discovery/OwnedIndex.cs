using System.Diagnostics;
using Encore.Jellyfin;
using Encore.Picks;

namespace Encore.Discovery;

/// <summary>One song the server owns, by its <see cref="TrackKey"/> (v0.2 design section 3.5).</summary>
public sealed record OwnedEntry(Guid ItemId, TrackKey Key, string Title, Guid? ArtistId, string ArtistName,
    int? DurationS, string? Path, DateTimeOffset? Created, bool InRadioDir);

/// <summary>Every Jellyfin Audio item by TrackKey, music-radio included (design section 5.2). Thread safe.</summary>
public interface IOwnedIndex
{
    /// <summary>False until the first load finished; live planning then uses the v0.1 chain only.</summary>
    bool IsLoaded { get; }

    /// <summary>The server copy of the song, or null when the server has none (it then needs a fetch).</summary>
    OwnedEntry? Resolve(TrackKey key, int? durationS);

    /// <summary>The key of an owned item, for exclusions; null when the item is unknown or has no key.</summary>
    TrackKey? KeyOf(Guid itemId);

    /// <summary>Adds a song right after it was ingested, so it is owned at once (before the next refresh).</summary>
    void Add(OwnedEntry entry);

    int Count { get; }

    DateTimeOffset? RefreshedUtc { get; }
}

/// <summary>
/// The owned index. <see cref="RefreshAsync"/> reads every Audio item in pages of 500 and swaps the whole index; a
/// failed refresh keeps the old one and logs <c>owned_index.failed</c>. <see cref="RunAsync"/> refreshes now and every
/// 30 minutes (the fetch coordinator owns that loop; fetch mode off never loads the index).
/// </summary>
public sealed class OwnedIndex : IOwnedIndex
{
    public const int PageSize = 500;

    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);

    private readonly IJellyfinClient _jellyfin;
    private readonly string _radioPrefix;
    private readonly ILogger<OwnedIndex> _logger;
    private readonly TimeProvider _time;
    private readonly int _pageSize;
    private readonly TimeSpan _refreshEvery;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly List<OwnedEntry> _addedDuringRefresh = [];

    private Dictionary<TrackKey, List<OwnedEntry>> _byKey = [];
    private Dictionary<Guid, OwnedEntry> _byId = [];
    private bool _loaded;
    private bool _refreshInProgress;
    private DateTimeOffset? _refreshedUtc;

    /// <param name="radioJellyfinDir">ENCORE_RADIO_JELLYFIN_DIR: the music-radio folder as Jellyfin sees it.</param>
    public OwnedIndex(IJellyfinClient jellyfin, string radioJellyfinDir, ILogger<OwnedIndex> logger,
        TimeProvider? time = null, int pageSize = PageSize, TimeSpan? refreshEvery = null)
    {
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        ArgumentException.ThrowIfNullOrWhiteSpace(radioJellyfinDir);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _radioPrefix = radioJellyfinDir.TrimEnd('/') + "/";
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _pageSize = pageSize;
        _refreshEvery = refreshEvery ?? RefreshEvery;
    }

    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    public DateTimeOffset? RefreshedUtc
    {
        get
        {
            lock (_gate)
            {
                return _refreshedUtc;
            }
        }
    }

    /// <summary>
    /// The entry for a Jellyfin Audio item: key from its Name and <c>Picker.ArtistOf(item).Name</c> (else the first
    /// named artist); null when the item has no id or no key.
    /// </summary>
    public static OwnedEntry? CreateEntry(BaseItemDto item, string radioJellyfinDir)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(radioJellyfinDir);
        if (item.Id == Guid.Empty)
        {
            return null;
        }

        var (artistId, artistName) = ArtistOf(item);
        if (!TrackKey.TryCreate(artistName, item.Name, out var key))
        {
            return null;
        }

        var prefix = radioJellyfinDir.TrimEnd('/') + "/";
        int? duration = item.RunTimeTicks is { } ticks ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalSeconds) : null;
        return new OwnedEntry(item.Id, key, item.Name ?? "", artistId, artistName ?? "", duration, item.Path,
            item.DateCreated, item.Path is { } p && p.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The artist a song is filed under: <c>Picker.ArtistOf</c>, else the first artist that has a name (no id then).</summary>
    public static (Guid? Id, string? Name) ArtistOf(BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Picker.ArtistOf(item) is { Name: { Length: > 0 } name } pair && !string.IsNullOrWhiteSpace(name))
        {
            return (pair.Id, name);
        }

        var named = item.ArtistItems?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name))
            ?? item.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name));
        return named is null ? (null, null) : (named.Id == Guid.Empty ? null : named.Id, named.Name);
    }

    public OwnedEntry? Resolve(TrackKey key, int? durationS)
    {
        if (key.Value is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out var list) || list.Count == 0)
            {
                return null;
            }

            if (list.Count == 1)
            {
                return list[0];
            }

            return list
                .OrderBy(e => durationS is { } d && e.DurationS is { } ed ? Math.Abs(d - ed) : int.MaxValue)
                .ThenBy(e => e.InRadioDir)
                .ThenByDescending(e => e.Created ?? DateTimeOffset.MinValue)
                .ThenBy(e => e.ItemId)
                .First();
        }
    }

    public TrackKey? KeyOf(Guid itemId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(itemId, out var entry) ? entry.Key : null;
        }
    }

    public void Add(OwnedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Key.Value is null || entry.ItemId == Guid.Empty)
        {
            return;
        }

        lock (_gate)
        {
            Put(_byKey, _byId, entry);
            if (_refreshInProgress)
            {
                _addedDuringRefresh.Add(entry);
            }
        }
    }

    /// <summary>Reloads every Audio item and swaps the index. False (old index kept, failure logged) when Jellyfin failed.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _refreshInProgress = true;
                _addedDuringRefresh.Clear();
            }

            var started = Stopwatch.GetTimestamp();
            var byKey = new Dictionary<TrackKey, List<OwnedEntry>>();
            var byId = new Dictionary<Guid, OwnedEntry>();
            var items = 0;
            try
            {
                var start = 0;
                while (true)
                {
                    var page = await _jellyfin.GetAudioPageAsync(start, _pageSize, ct).ConfigureAwait(false);
                    foreach (var item in page.Items)
                    {
                        items++;
                        if (CreateEntry(item, _radioPrefix) is { } entry)
                        {
                            Put(byKey, byId, entry);
                        }
                    }

                    start += page.Items.Count;
                    if (page.Items.Count == 0 || start >= page.TotalRecordCount)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.OwnedIndexFailed(Describe(e));
                return false;
            }

            int keys;
            int radioItems;
            lock (_gate)
            {
                foreach (var added in _addedDuringRefresh)
                {
                    if (!byId.ContainsKey(added.ItemId))
                    {
                        Put(byKey, byId, added);
                    }
                }

                _byKey = byKey;
                _byId = byId;
                _loaded = true;
                _refreshedUtc = _time.GetUtcNow();
                keys = byKey.Count;
                radioItems = byId.Values.Count(e => e.InRadioDir);
            }

            _logger.OwnedIndexLoaded(items, keys, radioItems, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return true;
        }
        finally
        {
            lock (_gate)
            {
                _refreshInProgress = false;
                _addedDuringRefresh.Clear();
            }

            _refreshing.Release();
        }
    }

    /// <summary>Refreshes now, then every 30 minutes, until <paramref name="ct"/> is cancelled. Never throws.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                await Task.Delay(_refreshEvery, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static void Put(Dictionary<TrackKey, List<OwnedEntry>> byKey, Dictionary<Guid, OwnedEntry> byId, OwnedEntry entry)
    {
        if (byId.TryGetValue(entry.ItemId, out var old) && byKey.TryGetValue(old.Key, out var oldList))
        {
            oldList.RemoveAll(e => e.ItemId == entry.ItemId);
            if (oldList.Count == 0)
            {
                byKey.Remove(old.Key);
            }
        }

        byId[entry.ItemId] = entry;
        if (!byKey.TryGetValue(entry.Key, out var list))
        {
            byKey[entry.Key] = list = [];
        }

        list.Add(entry);
    }

    /// <summary>Kind and status only; Jellyfin exception messages never hold the key or a URL.</summary>
    internal static string Describe(Exception e) => e switch
    {
        JellyfinException j => j.Message,
        DiscoveryException d => $"{d.Kind}: {d.Message}",
        _ => e.GetType().Name,
    };
}
