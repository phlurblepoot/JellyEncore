using Encore.Retention;
using Encore.Store;

namespace Encore.Tests.Retention;

/// <summary>RetentionPlanner (v04 §3, §14 T127): every row of the table, in order, plus the fail-safe cases.</summary>
public sealed class RetentionPlannerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);
    private static readonly PlannerSettings Settings = new(30, 3, 3);
    private static readonly Guid Song = Guid.Parse("aaaaaaaa00000000000000000000000a");
    private static readonly Guid User = Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f");
    private static readonly Guid Playlist = Guid.Parse("bbbbbbbb00000000000000000000000b");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-planner-" + Guid.NewGuid().ToString("N"));

    public RetentionPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static TrackedFile Row(
        long id = 1, double ageDays = 1, int uploads = 0, int plays = 0, bool requested = false, string? keptReason = null,
        DateTimeOffset? keptUtc = null, Guid? item = null, string state = "ready", bool noReadyTime = false) => new(
        id, "key-" + id, "Artist", "Title " + id, state, "/radio/soulseek/a/" + id + ".flac", "/media/radio/" + id + ".flac",
        (item ?? Song).ToString("N"), requested, keptReason, keptUtc, null, null, null, uploads, plays,
        noReadyTime ? null : Now - TimeSpan.FromDays(ageDays), User.ToString("N"), null, null);

    private static SignalsSnapshot Signals(
        bool favorite = false, bool inPlaylist = false, string? skippedWhy = null, bool installed = false, bool smartRead = true,
        IEnumerable<SignalFailure>? failures = null)
    {
        var playlists = new List<SignalPlaylist>();
        if (inPlaylist)
        {
            playlists.Add(new SignalPlaylist(Playlist, "Mix", [User], skippedWhy, skippedWhy is null ? [Guid.NewGuid(), Song] : [], skippedWhy is null));
        }

        return new SignalsSnapshot(Now, 1, favorite ? new HashSet<Guid> { Song } : new HashSet<Guid>(), playlists, installed, smartRead, [.. failures ?? []]);
    }

    private static PlannedRow One(TrackedFile row, SignalsSnapshot? signals = null, params long[] inProgress) =>
        Assert.Single(RetentionPlanner.Plan([row], inProgress.ToHashSet(), signals ?? Signals(), Now, Settings));

    [Fact]
    public void T1_a_promotion_in_progress_resumes_whatever_else_is_true()
    {
        var p = One(Row(ageDays: 99, uploads: 9, keptReason: "favorite", keptUtc: Now), Signals(), 1);
        Assert.Equal("promote", p.Decision);
        Assert.Equal("resume", p.Reason);
        Assert.Null(p.RecordKeepAs);
    }

    [Fact]
    public void T2_a_requested_song_is_promoted_and_kept_as_requested()
    {
        var p = One(Row(ageDays: 99, uploads: 9, requested: true, keptReason: "requested"), Signals(favorite: true));
        Assert.Equal(("promote", "requested", "requested"), (p.Decision, p.Reason, p.RecordKeepAs));
    }

    [Fact]
    public void T3_a_favorite_is_promoted_and_kept_as_favorite()
    {
        var p = One(Row(ageDays: 99), Signals(favorite: true, inPlaylist: true));
        Assert.Equal(("promote", "favorite", "favorite"), (p.Decision, p.Reason, p.RecordKeepAs));
    }

    [Fact]
    public void T4_a_song_in_a_counted_playlist_is_promoted_and_kept_as_playlist()
    {
        var p = One(Row(ageDays: 99), Signals(inPlaylist: true));
        Assert.Equal(("promote", "playlist", "playlist"), (p.Decision, p.Reason, p.RecordKeepAs));
    }

    [Fact]
    public void T5_enough_user_started_plays_promote_and_keep_as_plays()
    {
        var p = One(Row(ageDays: 99, plays: 3));
        Assert.Equal(("promote", "plays", "plays"), (p.Decision, p.Reason, p.RecordKeepAs));
        Assert.Equal("wait", One(Row(plays: 2)).Decision);
    }

    [Fact]
    public void T6_a_kept_row_that_is_not_promoted_stays_a_keep_even_when_old_and_uploaded()
    {
        var p = One(Row(ageDays: 99, uploads: 9, keptReason: "favorite", keptUtc: Now - TimeSpan.FromDays(40)));
        Assert.Equal(("keep", "favorite"), (p.Decision, p.Reason));
        Assert.Null(p.RecordKeepAs);
    }

    [Fact]
    public void T7_a_song_that_reaches_the_age_limit_is_deleted_for_age()
    {
        var p = One(Row(ageDays: 30));
        Assert.Equal(("delete", "age"), (p.Decision, p.Reason));
        Assert.Equal("wait", One(Row(ageDays: 29.9)).Decision);
    }

    [Fact]
    public void T8_a_song_uploaded_enough_times_is_deleted_for_uploads()
    {
        var p = One(Row(ageDays: 2, uploads: 3));
        Assert.Equal(("delete", "uploads"), (p.Decision, p.Reason));
        Assert.Equal("wait", One(Row(ageDays: 2, uploads: 2)).Decision);
    }

    [Fact]
    public void T9_age_is_named_when_both_limits_are_reached()
    {
        Assert.Equal("age", One(Row(ageDays: 31, uploads: 5)).Reason);
    }

    [Fact]
    public void T10_waiting_reports_what_is_left()
    {
        var p = One(Row(ageDays: 10, uploads: 1, plays: 2));
        Assert.Equal("wait", p.Decision);
        Assert.Equal(20, p.DaysLeft!.Value, 3);
        Assert.Equal(2, p.UploadsLeft);
        Assert.Equal(1, p.PlaysLeft);
    }

    [Fact]
    public void T11_the_keep_signals_win_over_a_due_delete()
    {
        Assert.Equal("promote", One(Row(ageDays: 99, uploads: 9), Signals(favorite: true)).Decision);
        Assert.Equal("promote", One(Row(ageDays: 99, uploads: 9, requested: true)).Decision);
        Assert.Equal("promote", One(Row(ageDays: 99, uploads: 9), Signals(inPlaylist: true)).Decision);
    }

    [Fact]
    public void T12_the_first_matching_keep_signal_names_the_reason()
    {
        Assert.Equal("requested", One(Row(requested: true, plays: 5), Signals(favorite: true, inPlaylist: true)).Reason);
        Assert.Equal("favorite", One(Row(plays: 5), Signals(favorite: true, inPlaylist: true)).Reason);
        Assert.Equal("playlist", One(Row(plays: 5), Signals(inPlaylist: true)).Reason);
    }

    [Fact]
    public void T13_the_keep_is_recorded_only_when_the_row_has_no_kept_time_yet()
    {
        Assert.Null(One(Row(keptReason: "favorite", keptUtc: Now - TimeSpan.FromDays(1)), Signals(favorite: true)).RecordKeepAs);
        Assert.Equal("favorite", One(Row(), Signals(favorite: true)).RecordKeepAs);
    }

    [Fact]
    public void T14_a_song_kept_earlier_stays_kept_after_the_favorite_is_removed()
    {
        var row = Row(ageDays: 60, keptReason: "favorite", keptUtc: Now - TimeSpan.FromDays(20));
        Assert.Equal("keep", One(row, Signals()).Decision);
    }

    [Fact]
    public void T15_a_failed_signal_read_turns_deletes_into_skips_but_keeps_still_count()
    {
        var failed = Signals(favorite: true, failures: [new SignalFailure("favorites", 500, "boom")]);
        Assert.False(failed.DeletesAllowed);
        var due = One(Row(ageDays: 99), Signals(failures: [new SignalFailure("favorites", 500, "boom")]));
        Assert.Equal(("skip", "signals_failed", "age"), (due.Decision, due.Reason, due.WouldDelete));
        Assert.Equal("promote", One(Row(ageDays: 99), failed).Decision);
        Assert.Equal("wait", One(Row(ageDays: 1), Signals(failures: [new SignalFailure("users", null, "x")])).Decision);
    }

    [Fact]
    public void T16_with_smartlists_unreadable_a_playlist_does_not_keep_and_nothing_is_deleted()
    {
        var unreadable = Signals(inPlaylist: true, installed: true, smartRead: false, failures: [new SignalFailure("smartlists", null, "x")]);
        Assert.False(unreadable.PlaylistKeepsAllowed);
        Assert.Equal("skip", One(Row(ageDays: 99), unreadable).Decision);
        var young = One(Row(ageDays: 1), unreadable);
        Assert.Equal("wait", young.Decision);
        Assert.Null(young.RecordKeepAs);
    }

    [Fact]
    public void T17_with_smartlists_unreadable_other_keeps_are_still_recorded()
    {
        var unreadable = Signals(favorite: true, inPlaylist: true, installed: true, smartRead: false, failures: [new SignalFailure("smartlists", null, "x")]);
        Assert.Equal(("promote", "favorite"), (One(Row(), unreadable).Decision, One(Row(), unreadable).Reason));
        Assert.Equal("requested", One(Row(requested: true), unreadable).Reason);
    }

    [Fact]
    public void T18_a_skipped_playlist_never_keeps_a_song()
    {
        foreach (var why in new[] { PlaylistSkipReason.SmartListsPlugin, PlaylistSkipReason.SmartSuffix, PlaylistSkipReason.IgnoreList })
        {
            Assert.Equal("delete", One(Row(ageDays: 99), Signals(inPlaylist: true, skippedWhy: why)).Decision);
        }
    }

    [Fact]
    public void T19_rows_that_are_not_ready_are_not_considered()
    {
        var rows = new[] { Row(1, state: "downloading"), Row(2, state: "failed"), Row(3, state: "deleted"), Row(4, state: "promoted"), Row(5) };
        var plan = RetentionPlanner.Plan(rows, new HashSet<long>(), Signals(), Now, Settings);
        Assert.Equal(5, Assert.Single(plan).Row.Id);
    }

    [Fact]
    public void T20_a_row_without_a_ready_time_has_age_zero_and_waits()
    {
        var p = One(Row(noReadyTime: true));
        Assert.Equal(("wait", 0), (p.Decision, p.AgeDays));
    }

    [Fact]
    public void T21_a_row_with_no_jellyfin_id_cannot_match_a_favorite_or_playlist()
    {
        var row = Row() with { JellyfinItemId = null };
        Assert.Equal("wait", One(row, Signals(favorite: true, inPlaylist: true)).Decision);
    }

    [Fact]
    public void T22_the_target_path_is_only_asked_for_promotions()
    {
        var plan = RetentionPlanner.Plan([Row(1, requested: true), Row(2)], new HashSet<long>(), Signals(), Now, Settings, r => "/library/" + r.Id + ".flac");
        Assert.Equal("/library/1.flac", plan[0].TargetPath);
        Assert.Null(plan[1].TargetPath);
    }

    [Fact]
    public void DP1_a_song_the_library_already_has_is_decided_dedupe_with_its_reason_and_the_library_copy_as_target()
    {
        Func<TrackedFile, string?> copy = r => r.Id is 1 or 2 or 4 ? "/media/music/Artist/Single (2020)/01 - Title.flac" : null;
        var plan = RetentionPlanner.Plan(
            [Row(1, requested: true), Row(2, plays: 5), Row(3, plays: 5), Row(4, ageDays: 99)], new HashSet<long>(), Signals(), Now, Settings,
            r => "/library/" + r.Id + ".flac", copy);

        Assert.Equal(("dedupe", "requested", "requested"), (plan[0].Decision, plan[0].Reason, plan[0].RecordKeepAs));
        Assert.Equal("/media/music/Artist/Single (2020)/01 - Title.flac", plan[0].TargetPath);
        Assert.Equal(("dedupe", "plays"), (plan[1].Decision, plan[1].Reason));
        Assert.Equal(("promote", "/library/3.flac"), (plan[2].Decision, plan[2].TargetPath));
        Assert.Equal("delete", plan[3].Decision); // only songs that would be promoted are deduped
    }

    [Fact]
    public void DP2_a_promotion_in_progress_resumes_even_when_the_library_has_the_song()
    {
        var p = Assert.Single(RetentionPlanner.Plan([Row()], new HashSet<long> { 1 }, Signals(), Now, Settings, null, _ => "/media/music/x.flac"));
        Assert.Equal(("promote", "resume"), (p.Decision, p.Reason));
    }

    [Fact]
    public void U1_untracked_audio_is_listed_and_known_hidden_and_non_audio_files_are_not()
    {
        var soul = Path.Combine(_dir, "soulseek");
        Directory.CreateDirectory(Path.Combine(soul, "A", "B"));
        Directory.CreateDirectory(Path.Combine(_dir, ".encore-trash", "2026-11-01"));
        var known = Path.Combine(soul, "A", "B", "01 - Known.flac");
        var stray = Path.Combine(soul, "A", "B", "02 - Stray.mp3");
        File.WriteAllText(known, "x");
        File.WriteAllText(stray, "x");
        File.WriteAllText(Path.Combine(soul, "A", "B", "cover.jpg"), "x");
        File.WriteAllText(Path.Combine(soul, "A", "B", ".hidden.flac"), "x");
        File.WriteAllText(Path.Combine(_dir, ".encore-trash", "2026-11-01", "1.flac"), "x");

        var found = UntrackedFiles.Find(_dir, [known]);

        Assert.Equal([stray.Replace('\\', '/')], found);
    }

    [Fact]
    public void U2_a_missing_radio_folder_lists_nothing()
    {
        Assert.Empty(UntrackedFiles.Find(Path.Combine(_dir, "nope"), []));
    }
}
