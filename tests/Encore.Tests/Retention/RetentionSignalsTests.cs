using Encore.Config;
using Encore.Jellyfin;
using Encore.Retention;
using Encore.Tests.EndToEnd;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;

namespace Encore.Tests.Retention;

/// <summary>Wraps the retention half of <see cref="FakeJellyfin"/> so a test can make single calls fail.</summary>
internal sealed class FlakyRetention(FakeJellyfin inner) : IRetentionJellyfin
{
    public HashSet<Guid> FailFavoritesFor { get; } = [];

    public HashSet<(Guid Playlist, Guid User)> FailEntriesFor { get; } = [];

    public Exception? FailSmartLists { get; set; }

    public Exception? FailPlaylists { get; set; }

    public int EntryReads { get; private set; }

    public Task<IReadOnlyList<BaseItemDto>> GetFavoriteAudioAsync(Guid userId, Guid? parentId = null, CancellationToken ct = default) =>
        FailFavoritesFor.Contains(userId) ? throw new JellyfinException("GET /Items", "server error", 500) : inner.GetFavoriteAudioAsync(userId, parentId, ct);

    public Task<IReadOnlyList<PlaylistRef>> GetPlaylistsAsync(Guid userId, CancellationToken ct = default) =>
        FailPlaylists is { } ex ? throw ex : inner.GetPlaylistsAsync(userId, ct);

    public Task<IReadOnlyList<Guid>> GetPlaylistEntriesAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        EntryReads++;
        return FailEntriesFor.Contains((playlistId, userId)) ? throw new JellyfinException("GET /Playlists", "boom", 500) : inner.GetPlaylistEntriesAsync(playlistId, userId, ct);
    }

    public Task<PlaylistWriteResult> AddToPlaylistAsync(Guid playlistId, Guid itemId, Guid userId, int position, CancellationToken ct = default) =>
        inner.AddToPlaylistAsync(playlistId, itemId, userId, position, ct);

    public Task<PlaylistWriteResult> RemovePlaylistEntriesAsync(Guid playlistId, IReadOnlyCollection<Guid> entryIds, CancellationToken ct = default) =>
        inner.RemovePlaylistEntriesAsync(playlistId, entryIds, ct);

    public Task MarkFavoriteAsync(Guid itemId, Guid userId, CancellationToken ct = default) => inner.MarkFavoriteAsync(itemId, userId, ct);

    public Task<UserItemData?> GetUserItemDataAsync(Guid itemId, Guid userId, CancellationToken ct = default) => inner.GetUserItemDataAsync(itemId, userId, ct);

    public Task<bool> ItemExistsAsync(Guid itemId, CancellationToken ct = default) => inner.ItemExistsAsync(itemId, ct);

    public Task<SmartListsInfo> GetSmartListsAsync(CancellationToken ct = default) =>
        FailSmartLists is { } ex ? throw ex : inner.GetSmartListsAsync(ct);
}

/// <summary>RetentionSignals (v04 §4.1, §4.2, §14 T127): all users, SmartLists exclusion and the fail-safe.</summary>
public sealed class RetentionSignalsTests
{
    private static readonly Guid Ann = Guid.Parse("11111111000000000000000000000001");
    private static readonly Guid Bob = Guid.Parse("22222222000000000000000000000002");
    private static readonly Guid Cy = Guid.Parse("33333333000000000000000000000003");
    private static readonly Guid S1 = Guid.Parse("aaaaaaaa00000000000000000000000a");
    private static readonly Guid S2 = Guid.Parse("bbbbbbbb00000000000000000000000b");
    private static readonly Guid S3 = Guid.Parse("cccccccc00000000000000000000000c");
    private static readonly Guid P1 = Guid.Parse("ddddddd100000000000000000000000d");
    private static readonly Guid P2 = Guid.Parse("ddddddd200000000000000000000000d");
    private static readonly Guid P3 = Guid.Parse("ddddddd300000000000000000000000d");
    private static readonly Guid P4 = Guid.Parse("ddddddd400000000000000000000000d");

    private readonly FakeJellyfin _fake = new() { Users = [new() { Id = Ann, Name = "ann" }, new() { Id = Bob, Name = "bob" }, new() { Id = Cy, Name = "cy" }] };
    private readonly ListLogger<RetentionSignals> _log = new();
    private readonly FlakyRetention _flaky;
    private string[] _ignore = [];

    public RetentionSignalsTests() => _flaky = new FlakyRetention(_fake);

    private void AddPlaylist(Guid id, string name, IEnumerable<Guid> viewers, params Guid[] entries)
    {
        _fake.Playlists[id] = new PlaylistRef(id, name);
        _fake.PlaylistViewers[id] = [.. viewers];
        _fake.PlaylistEntries[id] = [.. entries];
    }

    private Task<SignalsSnapshot> Read(params Guid[] tracked)
    {
        var options = new EncoreOptions
        {
            JellyfinUrl = new Uri("http://jellyfin.test:8096"),
            JellyfinApiKey = "test-key-not-real",
            AllowedUsers = ["ann"],
            DataDir = Path.GetTempPath(),
            RetentionIgnorePlaylists = _ignore,
        };
        return new RetentionSignals(_fake, _flaky, options, new ManualTimeProvider(DateTimeOffset.UnixEpoch), _log).ReadAsync(tracked);
    }

    [Fact]
    public async Task SG01_favorites_of_every_user_count_even_for_users_the_radio_never_touched()
    {
        _fake.Favorites[Ann] = [new BaseItemDto { Id = S1 }];
        _fake.Favorites[Cy] = [new BaseItemDto { Id = S2 }];

        var snap = await Read(S1, S2, S3);

        Assert.True(snap.IsFavorite(S1));
        Assert.True(snap.IsFavorite(S2));
        Assert.False(snap.IsFavorite(S3));
        Assert.Equal(3, snap.Users);
        Assert.True(snap.DeletesAllowed);
    }

    [Fact]
    public async Task SG02_a_playlist_that_only_some_users_see_counts_and_keeps_its_candidate_users()
    {
        AddPlaylist(P1, "Road trip", [Bob, Cy], S2, S1);

        var snap = await Read(S1);

        var p = Assert.Single(snap.Counted);
        Assert.Equal([Bob, Cy], p.CandidateUsers);
        var m = Assert.Single(snap.MembershipsOf(S1));
        Assert.Equal((P1, 1), (m.PlaylistId, m.Index));
        Assert.Equal([Bob, Cy], m.CandidateUsers);
        Assert.True(snap.InCountedPlaylist(S2));
        Assert.False(snap.InCountedPlaylist(S3));
    }

    [Fact]
    public async Task SG03_a_playlist_seen_by_two_users_is_listed_once_with_both_as_candidates()
    {
        AddPlaylist(P1, "Shared", [Ann, Bob], S1);

        var snap = await Read(S1);

        Assert.Equal([Ann, Bob], Assert.Single(snap.Playlists).CandidateUsers);
    }

    [Fact]
    public async Task SG04_a_song_twice_in_a_playlist_gives_two_memberships()
    {
        AddPlaylist(P1, "Loop", [Ann], S1, S2, S1);

        var snap = await Read(S1);

        Assert.Equal([0, 2], snap.MembershipsOf(S1).Select(m => m.Index));
    }

    [Fact]
    public async Task SG05_smartlists_playlists_by_plugin_id_are_skipped_and_never_counted()
    {
        AddPlaylist(P1, "Recently added", [Ann], S1);
        AddPlaylist(P2, "Hand made", [Ann], S2);
        _fake.SmartLists = new SmartListsInfo(true, true, new HashSet<Guid> { P1 });

        var snap = await Read(S1, S2);

        Assert.Equal(PlaylistSkipReason.SmartListsPlugin, Assert.Single(snap.Skipped).SkippedWhy);
        Assert.False(snap.InCountedPlaylist(S1));
        Assert.True(snap.InCountedPlaylist(S2));
        Assert.True(snap.DeletesAllowed);
        Assert.Equal(0, _flaky.EntryReads - 1);
    }

    [Theory]
    [InlineData("Newest [Smart]")]
    [InlineData("Newest [smart]  ")]
    public async Task SG06_a_name_ending_in_the_smart_suffix_is_skipped(string name)
    {
        AddPlaylist(P1, name, [Ann], S1);

        var snap = await Read(S1);

        Assert.Equal(PlaylistSkipReason.SmartSuffix, Assert.Single(snap.Skipped).SkippedWhy);
        Assert.False(snap.InCountedPlaylist(S1));
    }

    [Fact]
    public async Task SG07_a_playlist_on_the_ignore_list_is_skipped()
    {
        AddPlaylist(P1, "Party", [Ann], S1);
        _ignore = [P1.ToString("N"), "not-a-guid"];

        var snap = await Read(S1);

        Assert.Equal(PlaylistSkipReason.IgnoreList, Assert.Single(snap.Skipped).SkippedWhy);
        Assert.False(snap.InCountedPlaylist(S1));
    }

    [Fact]
    public async Task SG08_smartlists_not_installed_skips_nothing_by_plugin()
    {
        AddPlaylist(P1, "Mix", [Ann], S1);
        _fake.SmartLists = SmartListsInfo.NotInstalled;

        var snap = await Read(S1);

        Assert.True(snap.InCountedPlaylist(S1));
        Assert.False(snap.SmartListsInstalled);
        Assert.True(snap.PlaylistKeepsAllowed);
    }

    [Fact]
    public async Task SG09_smartlists_installed_but_unreadable_blocks_playlist_keeps_and_deletes_but_not_favorites()
    {
        AddPlaylist(P1, "Mix", [Ann], S1);
        _fake.Favorites[Ann] = [new BaseItemDto { Id = S2 }];
        _fake.SmartLists = new SmartListsInfo(true, false, new HashSet<Guid>());

        var snap = await Read(S1, S2);

        Assert.True(snap.SmartListsInstalled);
        Assert.False(snap.SmartListsRead);
        Assert.False(snap.PlaylistKeepsAllowed);
        Assert.False(snap.DeletesAllowed);
        Assert.False(snap.InCountedPlaylist(S1));
        Assert.True(snap.IsFavorite(S2));
        Assert.Equal("smartlists", Assert.Single(snap.Failures).What);
        Assert.Single(_log.Named("signals.failed"));
    }

    [Fact]
    public async Task SG10_the_smartlists_plugin_list_failing_to_load_at_all_is_treated_as_installed_and_unreadable()
    {
        AddPlaylist(P1, "Mix", [Ann], S1);
        _flaky.FailSmartLists = new JellyfinException("GET /Plugins", "boom", 500);

        var snap = await Read(S1);

        Assert.True(snap.SmartListsInstalled);
        Assert.False(snap.PlaylistKeepsAllowed);
        Assert.False(snap.DeletesAllowed);
        Assert.False(snap.InCountedPlaylist(S1));
    }

    [Fact]
    public async Task SG11_a_failed_users_read_gives_an_empty_snapshot_that_forbids_deletes()
    {
        _fake.UsersFailure = new JellyfinException("GET /Users", "boom", 500);

        var snap = await Read(S1);

        Assert.False(snap.DeletesAllowed);
        Assert.Equal("users", Assert.Single(snap.Failures).What);
        Assert.Empty(snap.Favorites);
    }

    [Fact]
    public async Task SG12_a_failed_favorites_read_for_one_user_keeps_the_others_and_forbids_deletes()
    {
        _fake.Favorites[Ann] = [new BaseItemDto { Id = S1 }];
        _fake.Favorites[Bob] = [new BaseItemDto { Id = S2 }];
        _flaky.FailFavoritesFor.Add(Bob);

        var snap = await Read(S1, S2);

        Assert.True(snap.IsFavorite(S1));
        Assert.False(snap.IsFavorite(S2));
        Assert.False(snap.DeletesAllowed);
        var failure = Assert.Single(snap.Failures);
        Assert.Equal(("favorites", 500), (failure.What, failure.Status));
    }

    [Fact]
    public async Task SG13_entries_are_read_with_the_next_candidate_when_the_first_fails()
    {
        AddPlaylist(P1, "Mix", [Ann, Bob], S1);
        _flaky.FailEntriesFor.Add((P1, Ann));

        var snap = await Read(S1);

        Assert.True(snap.InCountedPlaylist(S1));
        Assert.True(snap.DeletesAllowed);
    }

    [Fact]
    public async Task SG14_when_every_candidate_fails_the_playlist_is_unread_and_deletes_are_forbidden()
    {
        AddPlaylist(P1, "Mix", [Ann, Bob], S1);
        _flaky.FailEntriesFor.Add((P1, Ann));
        _flaky.FailEntriesFor.Add((P1, Bob));

        var snap = await Read(S1);

        Assert.False(snap.InCountedPlaylist(S1));
        Assert.False(snap.Counted.Single().EntriesRead);
        Assert.False(snap.DeletesAllowed);
        Assert.Equal("playlists", Assert.Single(snap.Failures).What);
    }

    [Fact]
    public async Task SG15_a_failed_playlist_listing_for_one_user_is_recorded()
    {
        _flaky.FailPlaylists = new JellyfinException("GET /Items", "boom", 503);

        var snap = await Read(S1);

        Assert.False(snap.DeletesAllowed);
        Assert.All(snap.Failures, f => Assert.Equal("playlists", f.What));
        Assert.Equal(3, snap.Failures.Count);
    }

    [Fact]
    public async Task SG16_the_summary_logs_counts_and_matches()
    {
        _fake.Favorites[Ann] = [new BaseItemDto { Id = S1 }];
        AddPlaylist(P1, "Mix", [Ann], S2);
        AddPlaylist(P2, "Old [Smart]", [Ann], S3);

        await Read(S1, S2, S3, Guid.NewGuid());

        var line = Assert.Single(_log.Named("signals.read"));
        Assert.Equal(8200, line.EventId.Id);
        Assert.Equal(3, Convert.ToInt32(line.Fields["Users"]));
        Assert.Equal(1, Convert.ToInt32(line.Fields["Favorites"]));
        Assert.Equal(1, Convert.ToInt32(line.Fields["Playlists"]));
        Assert.Equal(1, Convert.ToInt32(line.Fields["PlaylistsSkipped"]));
        Assert.Equal(2, Convert.ToInt32(line.Fields["Matched"]));
    }
}
