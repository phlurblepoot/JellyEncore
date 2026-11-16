using Encore.Config;
using Encore.Jellyfin;

namespace Encore.Retention;

/// <summary>Why a playlist does not count (v04 §4.2). The text is what the report prints.</summary>
public static class PlaylistSkipReason
{
    public const string SmartListsPlugin = "smartlists_plugin";
    public const string SmartSuffix = "smart_suffix";
    public const string IgnoreList = "ignore_list";
}

/// <summary>
/// One playlist seen in the pass. <see cref="CandidateUsers"/> are the users whose list holds it (no API gives the owner; the
/// promoter finds the editor by trying them). <see cref="SkippedWhy"/> is null for a counted playlist.
/// <see cref="EntriesRead"/> is false when the entries were not read (skipped, the plugin list was unreadable, or every read failed).
/// </summary>
public sealed record SignalPlaylist(Guid Id, string Name, IReadOnlyList<Guid> CandidateUsers, string? SkippedWhy, IReadOnlyList<Guid> Entries, bool EntriesRead)
{
    public bool Counted => SkippedWhy is null;
}

/// <summary>A signal read that failed (8210). <see cref="What"/> is users, favorites, playlists or smartlists.</summary>
public sealed record SignalFailure(string What, int? Status, string Error);

/// <summary>Where a song sits in a counted playlist: the playlist, its candidate users and the 0-based index in the visible entry list.</summary>
public sealed record PlaylistMembership(Guid PlaylistId, string PlaylistName, IReadOnlyList<Guid> CandidateUsers, int Index);

/// <summary>
/// What one read of Jellyfin said (v04 §4.1, §4.2). Immutable. A failed read never loses keeps already found, but it forbids deletes
/// (<see cref="DeletesAllowed"/>), and an unreadable SmartLists list forbids new playlist keeps (<see cref="PlaylistKeepsAllowed"/>, invariant 10).
/// </summary>
public sealed class SignalsSnapshot
{
    public SignalsSnapshot(
        DateTimeOffset readUtc,
        int users,
        IReadOnlySet<Guid> favorites,
        IReadOnlyList<SignalPlaylist> playlists,
        bool smartListsInstalled,
        bool smartListsRead,
        IReadOnlyList<SignalFailure> failures)
    {
        ReadUtc = readUtc;
        Users = users;
        Favorites = favorites;
        Playlists = playlists;
        SmartListsInstalled = smartListsInstalled;
        SmartListsRead = smartListsRead;
        Failures = failures;
    }

    public DateTimeOffset ReadUtc { get; }

    public int Users { get; }

    /// <summary>Item ids favorited by any user.</summary>
    public IReadOnlySet<Guid> Favorites { get; }

    public IReadOnlyList<SignalPlaylist> Playlists { get; }

    public bool SmartListsInstalled { get; }

    /// <summary>True when the plugin is not installed, or installed and its list was read.</summary>
    public bool SmartListsRead { get; }

    public IReadOnlyList<SignalFailure> Failures { get; }

    /// <summary>No signal read failed: a delete decision can be trusted.</summary>
    public bool DeletesAllowed => Failures.Count == 0;

    /// <summary>False when SmartLists is installed but its list was unreadable: no playlist can be trusted as hand-made.</summary>
    public bool PlaylistKeepsAllowed => !SmartListsInstalled || SmartListsRead;

    /// <summary>
    /// True when every user's playlists and every counted playlist's entries were read, so <see cref="MembershipsOf"/> lists every
    /// place a song holds. A promotion records its playlist places from this (step <c>snapshot</c>), so it starts only when true:
    /// an incomplete read would drop the song from the playlists it missed.
    /// </summary>
    public bool PlaylistsComplete =>
        PlaylistKeepsAllowed && !Failures.Any(f => f.What is "users" or "playlists" or "smartlists" or "signals");

    public IEnumerable<SignalPlaylist> Counted => Playlists.Where(p => p.Counted);

    public IEnumerable<SignalPlaylist> Skipped => Playlists.Where(p => !p.Counted);

    public bool IsFavorite(Guid itemId) => Favorites.Contains(itemId);

    /// <summary>Counted playlists that hold the item. Always empty while <see cref="PlaylistKeepsAllowed"/> is false.</summary>
    public IReadOnlyList<PlaylistMembership> MembershipsOf(Guid itemId)
    {
        if (!PlaylistKeepsAllowed)
        {
            return [];
        }

        var found = new List<PlaylistMembership>();
        foreach (var playlist in Counted)
        {
            for (var i = 0; i < playlist.Entries.Count; i++)
            {
                if (playlist.Entries[i] == itemId)
                {
                    found.Add(new PlaylistMembership(playlist.Id, playlist.Name, playlist.CandidateUsers, i));
                }
            }
        }

        return found;
    }

    public bool InCountedPlaylist(Guid itemId) => MembershipsOf(itemId).Count > 0;

    /// <summary>An empty snapshot with the given failures, for a pass that could not read at all.</summary>
    public static SignalsSnapshot Failed(DateTimeOffset at, params SignalFailure[] failures) =>
        new(at, 0, new HashSet<Guid>(), [], false, true, failures);
}

/// <summary>
/// Reads the retention signals from Jellyfin (v04 §4.1, §4.2): every user's favorite songs and every counted playlist's entries.
/// SmartLists playlists (by the plugin's list, the <c>[Smart]</c> name suffix, or ENCORE_RETENTION_IGNORE_PLAYLISTS) are listed as skipped, never
/// counted. Every call is per user; no failure escapes: it is recorded and logged (8210) and the pass skips its deletes.
/// </summary>
public sealed class RetentionSignals
{
    private readonly IJellyfinClient _jellyfin;
    private readonly IRetentionJellyfin _retention;
    private readonly HashSet<Guid> _ignored;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionSignals> _log;

    public RetentionSignals(IJellyfinClient jellyfin, IRetentionJellyfin retention, EncoreOptions options, TimeProvider time, ILogger<RetentionSignals> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _jellyfin = jellyfin;
        _retention = retention;
        _time = time;
        _log = log;
        _ignored = [];
        foreach (var text in options.RetentionIgnorePlaylists)
        {
            if (Guid.TryParse(text, out var id))
            {
                _ignored.Add(id);
            }
        }
    }

    /// <summary>True when the playlist name ends with the SmartLists suffix (ignoring case and trailing blanks).</summary>
    public static bool HasSmartSuffix(string? name) =>
        name is not null && name.TrimEnd().EndsWith(RetentionLimits.SmartListSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads everything. <paramref name="trackedItemIds"/> only feeds the 8200 "matched" count.</summary>
    public async Task<SignalsSnapshot> ReadAsync(IReadOnlyCollection<Guid> trackedItemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackedItemIds);
        var now = _time.GetUtcNow();
        var failures = new List<SignalFailure>();

        IReadOnlyList<UserDto> users;
        try
        {
            users = await _jellyfin.GetUsersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex, ct))
        {
            Fail(failures, "users", ex);
            return SignalsSnapshot.Failed(now, [.. failures]);
        }

        // Favorites: one read per user. A failed user is recorded; the others still count.
        var favorites = new HashSet<Guid>();
        foreach (var user in users)
        {
            try
            {
                foreach (var item in await _retention.GetFavoriteAudioAsync(user.Id, null, ct).ConfigureAwait(false))
                {
                    favorites.Add(item.Id);
                }
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                Fail(failures, "favorites", ex);
            }
        }

        // SmartLists: a failure of GET /Plugins itself means "installed" is unknown, taken as installed and unreadable.
        var smartInstalled = false;
        var smartRead = true;
        var smartIds = new HashSet<Guid>();
        try
        {
            var info = await _retention.GetSmartListsAsync(ct).ConfigureAwait(false);
            smartInstalled = info.Installed;
            smartRead = info.ReadOk;
            if (info is { Installed: true, ReadOk: true })
            {
                smartIds.UnionWith(info.PlaylistIds);
            }

            if (info is { Installed: true, ReadOk: false })
            {
                failures.Add(new SignalFailure("smartlists", null, "the SmartLists plugin is installed but its list could not be read"));
                _log.SignalsFailed("smartlists", null, "the SmartLists plugin is installed but its list could not be read");
            }
        }
        catch (Exception ex) when (IsFailure(ex, ct))
        {
            smartInstalled = true;
            smartRead = false;
            Fail(failures, "smartlists", ex);
        }

        // Playlists: each user's list; candidates in the users' order.
        var byId = new Dictionary<Guid, (string Name, List<Guid> Candidates)>();
        var order = new List<Guid>();
        foreach (var user in users)
        {
            try
            {
                foreach (var playlist in await _retention.GetPlaylistsAsync(user.Id, ct).ConfigureAwait(false))
                {
                    if (!byId.TryGetValue(playlist.Id, out var entry))
                    {
                        entry = (playlist.Name ?? string.Empty, []);
                        byId[playlist.Id] = entry;
                        order.Add(playlist.Id);
                    }

                    entry.Candidates.Add(user.Id);
                }
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                Fail(failures, "playlists", ex);
            }
        }

        var keepsAllowed = !smartInstalled || smartRead;
        var playlists = new List<SignalPlaylist>(order.Count);
        foreach (var id in order)
        {
            var (name, candidates) = byId[id];
            var why = smartIds.Contains(id) ? PlaylistSkipReason.SmartListsPlugin
                : HasSmartSuffix(name) ? PlaylistSkipReason.SmartSuffix
                : _ignored.Contains(id) ? PlaylistSkipReason.IgnoreList
                : null;
            if (why is not null || !keepsAllowed)
            {
                playlists.Add(new SignalPlaylist(id, name, candidates, why, [], false));
                continue;
            }

            var entries = await ReadEntriesAsync(id, candidates, failures, ct).ConfigureAwait(false);
            playlists.Add(new SignalPlaylist(id, name, candidates, null, entries ?? [], entries is not null));
        }

        var snapshot = new SignalsSnapshot(now, users.Count, favorites, playlists, smartInstalled, smartRead, failures);
        var matched = trackedItemIds.Count(id => snapshot.IsFavorite(id) || snapshot.InCountedPlaylist(id));
        _log.SignalsRead(snapshot.Users, snapshot.Favorites.Count, snapshot.Counted.Count(), snapshot.Skipped.Count(), smartInstalled, matched);
        return snapshot;
    }

    /// <summary>Reads a playlist's entries with the first candidate that works; null when every candidate failed.</summary>
    private async Task<IReadOnlyList<Guid>?> ReadEntriesAsync(Guid playlistId, IReadOnlyList<Guid> candidates, List<SignalFailure> failures, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var user in candidates)
        {
            try
            {
                return await _retention.GetPlaylistEntriesAsync(playlistId, user, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFailure(ex, ct))
            {
                last = ex;
            }
        }

        if (last is not null)
        {
            Fail(failures, "playlists", last);
        }

        return null;
    }

    private void Fail(List<SignalFailure> failures, string what, Exception ex)
    {
        var status = (ex as JellyfinException)?.StatusCode;
        var error = ex is JellyfinException ? ex.Message : ex.GetType().Name;
        failures.Add(new SignalFailure(what, status, error));
        _log.SignalsFailed(what, status, error);
    }

    /// <summary>Every failure is recorded except the shutdown of the whole pass.</summary>
    private static bool IsFailure(Exception ex, CancellationToken ct) => !(ex is OperationCanceledException && ct.IsCancellationRequested);
}
