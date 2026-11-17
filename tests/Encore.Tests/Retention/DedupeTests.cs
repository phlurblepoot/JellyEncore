using Encore.Config;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Retention;
using Encore.Store;

namespace Encore.Tests.Retention;

/// <summary>
/// T141: a song the main library already has (same artist and title key, any album or edition) is never promoted. Its favorites, playlist
/// places and requests move to the library copy and the radio copy goes to the trash; a failure keeps the radio copy.
/// </summary>
public sealed class DedupeTests : IDisposable
{
    private const string LibraryPath = "/media/music/Artist/Song (2019)/01 - Song.flac";

    private readonly PromoterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static string Key(string artist, string title) => TrackKey.Create(artist, title)!.Value.Value;

    /// <summary>The library's own copy: a real file under the library folder, a Jellyfin item and an owned-index entry outside music-radio.</summary>
    private Guid LibraryCopy(string title = "Song", string path = LibraryPath)
    {
        var item = Guid.NewGuid();
        var disk = Path.Combine(_rig.Library, path["/media/music/".Length..].Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        File.WriteAllText(disk, "library bytes");
        _rig.Jellyfin.Items[item] = path;
        _rig.Owned.Library.Add(new OwnedEntry(item, TrackKey.Create("Artist", title)!.Value, title, null, "Artist", 200, path,
            _rig.Time.GetUtcNow() - TimeSpan.FromDays(400), InRadioDir: false));
        return item;
    }

    private Song Radio(string title = "Song") => _rig.Ready(file: "03 - " + title + ".flac", album: "Welcome (2021)", title: title, trackKey: Key("Artist", title));

    private string TrashOf(Song song) =>
        Path.Combine(_rig.Radio, Deleter.TrashFolder, _rig.Time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), song.Id + ".flac");

    [Fact]
    public async Task DD01_the_same_song_on_another_album_and_edition_is_not_promoted_and_the_radio_copy_goes_to_the_trash()
    {
        var library = LibraryCopy();
        var song = Radio();
        var before = _rig.LibraryFiles().ToList();
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(PromotionOutcome.InLibrary, result.Outcome);
        Assert.Equal(before, _rig.LibraryFiles()); // nothing new in the library: no second copy
        Assert.False(File.Exists(song.Path));
        Assert.True(File.Exists(TrashOf(song)));
        Assert.Equal(song.Sha, PromoterRig.Hash(TrashOf(song)));
        Assert.False(Directory.Exists(Path.Combine(_rig.Radio, "soulseek", "Artist")));
        var row = _rig.Fetch.GetTracked(song.Id)!;
        Assert.Equal(FetchState.Deleted, row.State);
        Assert.Equal(FetchStore.DeleteReasonInLibrary, row.DeleteReason);
        Assert.Equal([song.Item], _rig.Owned.Removed);
        Assert.Empty(_rig.Owned.Added);
        Assert.Equal(["dedupe"], _rig.Shares.Dirty);
        Assert.Contains("/music-radio/soulseek/Artist/Welcome (2021)", _rig.Jellyfin.MediaUpdated);
        Assert.Equal(PromotionStep.Done, _rig.Store.GetPromotion(song.Id)!.Step);
        Assert.Empty(_rig.Store.ActivePromotions());
        Assert.Equal(1, _rig.Events(8600));
        Assert.Equal(1, _rig.Events(8610));
        Assert.Equal(0, _rig.Events(8520));
        Assert.Equal(library.ToString("N"), _rig.Event(8610).Fields["LibraryItemId"]);

        // A later call sees the finished dedupe.
        Assert.Equal(PromotionOutcome.InLibrary, (await promoter.PromoteAsync(song.Id, [])).Outcome);
    }

    [Theory]
    [InlineData("Song (Live)")]
    [InlineData("Song (Remix)")]
    [InlineData("Song - Acoustic")]
    public async Task DD02_a_live_remix_or_acoustic_version_is_still_promoted(string title)
    {
        LibraryCopy();
        Assert.NotEqual(Key("Artist", "Song"), Key("Artist", title));
        var song = Radio(title);
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(PromotionOutcome.Done, result.Outcome);
        Assert.Equal(FetchState.Promoted, _rig.Fetch.GetTracked(song.Id)!.State);
        Assert.Equal(0, _rig.Events(8600));
    }

    [Fact]
    public async Task DD03_favorites_and_playlist_places_move_to_the_library_copy()
    {
        var library = LibraryCopy();
        var song = Radio();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        _rig.Jellyfin.Items[a] = "/media/music/x/a.flac";
        _rig.Jellyfin.Items[b] = "/media/music/x/b.flac";
        var annList = _rig.Playlist([a, song.Item, b], [PromoterRig.Ann]);
        var bobList = _rig.Playlist([library, song.Item], [PromoterRig.Bob]); // the library copy is there already, on its own
        _rig.Jellyfin.UserData[(PromoterRig.Ann, song.Item)] = new UserItemData(true, 2);
        _rig.Jellyfin.UserData[(PromoterRig.Bob, song.Item)] = new UserItemData(false, 1);
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, [
            PromoterRig.Member(annList, 1, PromoterRig.Ann),
            PromoterRig.Member(bobList, 1, PromoterRig.Bob),
        ]);

        Assert.Equal(PromotionOutcome.InLibrary, result.Outcome);
        Assert.Equal([a, library, b], _rig.Jellyfin.Stored[annList]);
        Assert.Equal([library, library], _rig.Jellyfin.Stored[bobList]); // both places kept
        Assert.Equal([(library, PromoterRig.Ann)], _rig.Jellyfin.FavoriteMarks);
        Assert.True(_rig.Jellyfin.UserData[(PromoterRig.Ann, library)].IsFavorite);
        Assert.False(_rig.Jellyfin.UserData.TryGetValue((PromoterRig.Bob, library), out var bob) && bob.IsFavorite);
        Assert.Equal(2, _rig.Events(8560));
    }

    [Fact]
    public async Task DD04_a_requested_song_takes_the_dedupe_path_and_its_request_points_at_the_library_copy()
    {
        var library = LibraryCopy();
        var song = Radio();
        _rig.Fetch.MarkRequested(Key("Artist", "Song"));
        _rig.Requests.Insert(new NewRequest("r1", "song", PromoterRig.Ann.ToString("N"), "ann", "Artist", _rig.Time.GetUtcNow(), Title: "Song"));
        _rig.Requests.Update("r1", RequestState.Ready, _rig.Time.GetUtcNow(), new RequestUpdate(JellyfinItemId: song.Item.ToString("N")));
        Assert.True(_rig.Fetch.GetTracked(song.Id)!.Requested);
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(PromotionOutcome.InLibrary, result.Outcome);
        Assert.Equal(library.ToString("N"), _rig.Requests.Get("r1")!.JellyfinItemId);
        Assert.Equal(FetchState.Deleted, _rig.Fetch.GetTracked(song.Id)!.State);
    }

    [Fact]
    public async Task DD05_a_refused_playlist_write_keeps_the_radio_copy_and_its_playlist_place()
    {
        LibraryCopy();
        var song = Radio();
        var x = Guid.NewGuid();
        _rig.Jellyfin.Items[x] = "/media/music/x/x.flac";
        var list = _rig.Playlist([song.Item, x], [PromoterRig.Cat]); // only cat may edit; the candidate is ann
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, [PromoterRig.Member(list, 0, PromoterRig.Ann)]);

        Assert.Equal(PromotionOutcome.Failed, result.Outcome);
        Assert.True(File.Exists(song.Path));
        Assert.Equal(song.Sha, PromoterRig.Hash(song.Path));
        var row = _rig.Fetch.GetTracked(song.Id)!;
        Assert.Equal(FetchState.Ready, row.State);
        Assert.NotNull(row.KeptReason);
        Assert.Equal([song.Item, x], _rig.Jellyfin.Stored[list]);
        Assert.Empty(_rig.Owned.Removed);
        Assert.Empty(_rig.Shares.Dirty);
        Assert.Equal(1, _rig.Events(8620));
        Assert.Equal(0, _rig.Events(8610));
    }

    [Fact]
    public async Task DD06_a_failed_favorite_keeps_the_radio_copy_and_a_later_pass_finishes_without_a_second_playlist_entry()
    {
        var library = LibraryCopy();
        var song = Radio();
        var x = Guid.NewGuid();
        _rig.Jellyfin.Items[x] = "/media/music/x/x.flac";
        var list = _rig.Playlist([x, song.Item], [PromoterRig.Ann]);
        _rig.Jellyfin.UserData[(PromoterRig.Ann, song.Item)] = new UserItemData(true, 0);
        _rig.Jellyfin.FailMarkFavorite = true;
        using var promoter = _rig.Make();
        var members = new[] { PromoterRig.Member(list, 1, PromoterRig.Ann) };

        var first = await promoter.PromoteAsync(song.Id, members);

        Assert.Equal(PromotionOutcome.Failed, first.Outcome);
        Assert.True(File.Exists(song.Path));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(song.Id)!.State);
        Assert.Equal([x, song.Item], _rig.Jellyfin.Stored[list]);
        Assert.Equal(1, _rig.Events(8620));

        _rig.Jellyfin.FailMarkFavorite = false;
        var second = await promoter.PromoteAsync(song.Id, members);

        Assert.Equal(PromotionOutcome.InLibrary, second.Outcome);
        Assert.Equal([x, library], _rig.Jellyfin.Stored[list]);
        Assert.True(_rig.Jellyfin.UserData[(PromoterRig.Ann, library)].IsFavorite);
        Assert.False(File.Exists(song.Path));
    }

    [Fact]
    public async Task DD07_a_row_without_a_kept_reason_is_marked_kept_when_the_move_fails()
    {
        LibraryCopy();
        var fresh = Radio();
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(_rig.Dir, "encore.db")))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE fetched_files SET kept_reason = NULL, kept_utc = NULL WHERE id = " + fresh.Id;
            cmd.ExecuteNonQuery();
        }

        var list = _rig.Playlist([fresh.Item], [PromoterRig.Cat]);
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(fresh.Id, [PromoterRig.Member(list, 0, PromoterRig.Ann)]);

        Assert.Equal(PromotionOutcome.Failed, result.Outcome);
        Assert.Equal(Promoter.KeptDedupeFailed, _rig.Fetch.GetTracked(fresh.Id)!.KeptReason);
        Assert.True(File.Exists(fresh.Path));
    }

    [Fact]
    public async Task DD08_no_promotion_starts_before_the_owned_index_is_loaded()
    {
        LibraryCopy();
        var song = Radio();
        _rig.Owned.Loaded = false;
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(PromotionOutcome.Deferred, result.Outcome);
        Assert.Equal("owned_index_not_loaded", result.Reason);
        Assert.Null(_rig.Store.GetPromotion(song.Id));
        Assert.True(File.Exists(song.Path));
    }

    [Fact]
    public async Task DD09_a_library_copy_jellyfin_no_longer_lists_does_not_stop_the_promotion()
    {
        var library = LibraryCopy();
        _rig.Jellyfin.Items.Remove(library);
        var song = Radio();
        using var promoter = _rig.Make();

        var result = await promoter.PromoteAsync(song.Id, []);

        Assert.Equal(PromotionOutcome.Done, result.Outcome);
        Assert.Equal(0, _rig.Events(8600));
    }

    [Fact]
    public async Task DD10_a_song_being_uploaded_waits_with_its_places_already_moved_and_finishes_next_time()
    {
        var library = LibraryCopy();
        var song = Radio();
        var list = _rig.Playlist([song.Item], [PromoterRig.Ann]);
        _rig.Shares.PeersUploading = 1;
        using var promoter = _rig.Make();
        var members = new[] { PromoterRig.Member(list, 0, PromoterRig.Ann) };

        var first = await promoter.PromoteAsync(song.Id, members);

        Assert.Equal(PromotionOutcome.Deferred, first.Outcome);
        Assert.Equal("uploading", first.Reason);
        Assert.True(File.Exists(song.Path));
        Assert.Equal([library], _rig.Jellyfin.Stored[list]);

        _rig.Shares.PeersUploading = 0;
        var second = await promoter.PromoteAsync(song.Id, members);

        Assert.Equal(PromotionOutcome.InLibrary, second.Outcome);
        Assert.Equal([library], _rig.Jellyfin.Stored[list]);
        Assert.False(File.Exists(song.Path));
    }

    [Fact]
    public async Task DD11_a_pass_counts_a_dedupe_against_its_budget()
    {
        LibraryCopy();
        var song = Radio();
        var other = _rig.Ready(file: "01 - Other.flac", album: "B (2020)", title: "Other", trackKey: Key("Artist", "Other"));
        using var promoter = _rig.Make();
        var signals = new SignalsSnapshot(_rig.Time.GetUtcNow(), 3, new HashSet<Guid>(), [], false, true, []);

        var results = await promoter.RunPassAsync([song.Id, other.Id], signals, 1);

        var only = Assert.Single(results);
        Assert.Equal((song.Id, PromotionOutcome.InLibrary), (only.RowId, only.Outcome));
        Assert.Equal(FetchState.Ready, _rig.Fetch.GetTracked(other.Id)!.State);
    }

    [Fact]
    public void DD12_the_report_lists_dedupe_songs_with_the_library_copy()
    {
        var song = Radio();
        var row = _rig.Fetch.GetTracked(song.Id)!;
        var planned = new PlannedRow(row, RetentionDecision.Dedupe, "requested", null, 3, 0, 0, null, null, null, LibraryPath);
        var pass = new PassResult("p1", _rig.Time.GetUtcNow(), RetentionMode.Live, new PlannerSettings(30, 3, 3), [planned], [],
            new SignalsSnapshot(_rig.Time.GetUtcNow(), 3, new HashSet<Guid>(), [], false, true, []), 1);

        var model = RetentionReport.Build(pass);
        var text = RetentionReport.ToText(model);

        Assert.Equal(1, model.Counts[RetentionDecision.Dedupe]);
        Assert.Contains("DEDUPE (1)", text);
        Assert.Contains("library has it: " + LibraryPath, text);
    }
}
