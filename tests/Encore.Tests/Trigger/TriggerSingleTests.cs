using Encore.Trigger;
using static Encore.Tests.Trigger.TriggerHarness;

namespace Encore.Tests.Trigger;

/// <summary>
/// T110: a song played on its own from a stopped player (the server still holds a stale or empty list) starts the
/// radio once, when the song is near its end (<see cref="TriggerLimits.SingleLead"/> or less left), with rule
/// <c>single</c>. Jellyfin Web and Finamp shapes; a real queue reported first leaves it to the position rules.
/// </summary>
public sealed class TriggerSingleTests
{
    private static readonly Guid S = Item("S"), T = Item("T"), U = Item("U"), Y = Item("Y"), Z = Item("Z");
    private static readonly Guid A = Item("A"), B = Item("B"), C = Item("C");
    private static readonly Guid E1 = Item("E1"), E2 = Item("E2"), E3 = Item("E3"), E4 = Item("E4");

    private const double Length = 200;

    /// <summary>The snapshot at <paramref name="seconds"/> into a song of <paramref name="length"/> seconds.</summary>
    private static TriggerSession At(TriggerSession s, double seconds, double? length = Length) => s with
    {
        PositionTicks = TimeSpan.FromSeconds(seconds).Ticks,
        NowPlayingItem = s.NowPlayingItem! with { RunTimeTicks = length is { } l ? TimeSpan.FromSeconds(l).Ticks : null },
    };

    /// <summary>The near-end point of a 200 s song: 90 s or less left.</summary>
    private static double NearEnd => Length - TriggerLimits.SingleLead.TotalSeconds;

    /// <summary>A web song played on its own: the server still holds the session's initial empty list.</summary>
    private static TriggerSession WebSingle(Guid song, double seconds, string entry = "playlistItem0", Func<TriggerSession, TriggerSession>? change = null)
    {
        var s = At(Web([song], 0, x => x with { NowPlayingQueue = [], PlaylistItemId = entry }), seconds);
        return change is null ? s : change(s);
    }

    private static TriggerSession FinampSingle(Guid song, double seconds, Guid[]? upcoming = null, Func<TriggerSession, TriggerSession>? change = null) =>
        At(Finamp(song, upcoming ?? [], change), seconds);

    private static TriggerDecision.Fire AssertSingleFire(TriggerOutcome outcome, QueueModel model)
    {
        var fire = Assert.IsType<TriggerDecision.Fire>(outcome.Decision);
        Assert.Equal(FireKind.Start, fire.Kind);
        Assert.Equal(FireRule.Single, fire.Rule);
        Assert.Equal(model, fire.Model);
        Assert.Equal("single", fire.Rule.ToWire());
        Assert.Equal(InstanceState.Firing, outcome.Memory!.Instance!.State);
        return fire;
    }

    private static TriggerDecision.Skip AssertSkip(TriggerOutcome outcome, SkipReason reason)
    {
        var skip = Assert.IsType<TriggerDecision.Skip>(outcome.Decision);
        Assert.Equal(reason, skip.Reason);
        return skip;
    }

    private static int Fires(TriggerHarness h) => h.Outcomes.Count(o => o.Decision is TriggerDecision.Fire);

    [Fact]
    public void SG01_Web_single_from_idle_fires_at_the_near_end_and_not_before()
    {
        var h = new TriggerHarness();

        var first = AssertSkip(h.Feed(WebSingle(S, 0)), SkipReason.SingleWaiting);
        Assert.True(first.ShouldLog);
        Assert.False(AssertSkip(h.Feed(WebSingle(S, 60)), SkipReason.SingleWaiting).ShouldLog);
        AssertSkip(h.Feed(WebSingle(S, NearEnd - 1)), SkipReason.SingleWaiting);
        Assert.Equal(0, Fires(h));

        var fire = AssertSingleFire(h.Feed(WebSingle(S, NearEnd)), QueueModel.Unknown);
        Assert.Equal(1, fire.Attempt);
        h.MarkFired(E1, E2);

        Assert.IsType<TriggerDecision.None>(h.Feed(WebSingle(S, NearEnd + 5)).Decision);
        Assert.Equal(1, Fires(h));
    }

    [Fact]
    public void SG02_Web_single_with_a_stale_list_from_an_earlier_queue_fires()
    {
        // The last stop reported an old album [A,B,C]; the new song is entry #9, which that list does not hold.
        var h = new TriggerHarness();
        var stale = At(Web([A, B, C], 2, s => s with { NowPlayingItem = new NowPlaying(S, "Audio"), PlaylistItemId = "playlistItem9" }), NearEnd);

        AssertSingleFire(h.Feed(stale), QueueModel.Unknown);
    }

    [Fact]
    public void SG03_Finamp_single_from_idle_fires_with_no_history_since_Encore_started()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(FinampSingle(S, 10)), SkipReason.SingleWaiting);

        AssertSingleFire(h.Feed(FinampSingle(S, NearEnd)), QueueModel.Upcoming);
    }

    [Fact]
    public void SG04_Finamp_single_from_idle_after_an_album_ended_fires()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(Y, [Z]));
        var last = h.FeedAll(Idle(Finamp(Z, [])), Finamp(Z, []));
        Assert.Equal(FireRule.LastSong, Assert.IsType<TriggerDecision.Fire>(last.Decision).Rule);
        h.MarkFired(); // no picks, so nothing is waiting to show up (9b stays out of it)
        h.Feed(Idle(Finamp(Z, [])));
        h.Time.Advance(TimeSpan.FromMinutes(30));

        // The album ended with Z (list []); S from idle: the previous list [] does not hold S, a fresh play.
        AssertSkip(h.Feed(FinampSingle(S, 0)), SkipReason.SingleWaiting);
        AssertSingleFire(h.Feed(FinampSingle(S, NearEnd + 30)), QueueModel.Upcoming);
    }

    [Fact]
    public void SG05_Finamp_list_holding_only_the_current_song_counts_as_single()
    {
        var h = new TriggerHarness();
        AssertSingleFire(h.Feed(FinampSingle(S, NearEnd, [S])), QueueModel.Upcoming);
    }

    [Fact]
    public void SG06_A_real_queue_reported_before_the_near_end_point_leaves_it_to_the_position_rules()
    {
        // Finamp: S on its own waits; the next report (a track change) carries a real list, and the position rules decide.
        var h = new TriggerHarness();
        AssertSkip(h.Feed(FinampSingle(S, 20)), SkipReason.SingleWaiting);
        h.Feed(Idle(Finamp(T, [U, Y])));
        AssertSkip(h.Feed(FinampSingle(T, NearEnd + 50, [U, Y])), SkipReason.NotLast);
        h.Feed(Idle(Finamp(U, [Y])));
        var near = h.Feed(FinampSingle(U, 5, [Y]));
        Assert.Equal(FireRule.NearEnd, Assert.IsType<TriggerDecision.Fire>(near.Decision).Rule);

        // Web: a list that holds the current entry is the Full model, never the single start.
        var web = new TriggerHarness();
        AssertSkip(web.Feed(At(Web([A, B, C], 0), NearEnd)), SkipReason.NotLast);

        // Finamp: a stale list with other songs cannot be told from a real queue, so it stays with the position rules.
        var stale = new TriggerHarness();
        AssertSkip(stale.Feed(FinampSingle(S, NearEnd, [Y, Z])), SkipReason.NotLast);
        var staleOne = new TriggerHarness();
        AssertSkip(staleOne.Feed(FinampSingle(S, NearEnd, [Y])), SkipReason.NoHistory);
    }

    [Fact]
    public void SG07_Paused_waits_and_fires_after_resume()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(WebSingle(S, NearEnd + 10, change: s => s with { IsPaused = true })), SkipReason.SingleWaiting);
        AssertSkip(h.Feed(FinampSingle(S, NearEnd + 10, change: s => s with { IsPaused = true })), SkipReason.SingleWaiting);
        Assert.Equal(0, Fires(h));

        AssertSingleFire(h.Feed(FinampSingle(S, NearEnd + 11)), QueueModel.Upcoming);
    }

    [Fact]
    public void SG08_Repeat_on_never_fires()
    {
        foreach (var mode in new[] { "RepeatOne", "RepeatAll" })
        {
            var h = new TriggerHarness();
            AssertSkip(h.Feed(WebSingle(S, NearEnd, change: s => s with { RepeatMode = mode })), SkipReason.RepeatOn);
            var f = new TriggerHarness();
            AssertSkip(f.Feed(FinampSingle(S, NearEnd, change: s => s with { RepeatMode = mode })), SkipReason.RepeatOn);
        }
    }

    [Fact]
    public void SG09_A_song_the_run_added_behaves_as_today()
    {
        var finamp = new TriggerHarness { Run = RunOf(E1, E2) };
        AssertSkip(finamp.Feed(FinampSingle(E2, NearEnd)), SkipReason.NoHistory);

        var web = new TriggerHarness { Run = RunOf(E1, E2) };
        AssertSkip(web.Feed(WebSingle(E2, NearEnd)), SkipReason.StaleQueue);
    }

    [Fact]
    public void SG10_Not_allowed_not_audio_and_no_remote_control_behave_as_today()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(WebSingle(S, NearEnd, change: s => s with { UserId = OtherUser })), SkipReason.NotAllowed);
        var v = new TriggerHarness();
        AssertSkip(v.Feed(WebSingle(S, NearEnd, change: s => s with { NowPlayingItem = s.NowPlayingItem! with { Type = "Video" } })), SkipReason.NotAudio);
        var r = new TriggerHarness();
        AssertSkip(r.Feed(FinampSingle(S, NearEnd, change: s => s with { SupportsRemoteControl = false })), SkipReason.NoRemoteControl);
    }

    [Fact]
    public void SG11_Web_album_from_idle_fires_once_at_track_one_and_the_real_list_does_not_fire_again()
    {
        var h = new TriggerHarness();
        AssertSingleFire(h.Feed(At(Web([A, B, C], 0, s => s with { NowPlayingQueue = [] }), NearEnd)), QueueModel.Unknown);
        h.MarkFired(E1, E2);
        h.Run = RunOf(E1, E2);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Time.Advance(TimeSpan.FromSeconds(100));

        // The track change carries the real list: the album, then the radio's songs.
        Guid[] full = [A, B, C, E1, E2];
        h.Feed(Idle(Web(full, 0)));
        AssertSkip(h.Feed(At(Web(full, 1), NearEnd)), SkipReason.NotLast);
        h.Feed(Idle(Web(full, 1)));
        AssertSkip(h.Feed(At(Web(full, 2), NearEnd)), SkipReason.NotLast);
        Assert.Equal(1, Fires(h));

        // At E1 one song is left and it is the run's: a top-up, not a second start.
        h.Feed(Idle(Web(full, 2)));
        var topUp = Assert.IsType<TriggerDecision.Fire>(h.Feed(Web(full, 3)).Decision);
        Assert.Equal(FireKind.TopUp, topUp.Kind);
        Assert.Equal(FireRule.NearEnd, topUp.Rule);
    }

    [Fact]
    public void SG12_Finamp_album_from_idle_fires_once_and_the_queue_first_report_folds()
    {
        var h = new TriggerHarness();
        AssertSingleFire(h.Feed(FinampSingle(S, NearEnd)), QueueModel.Upcoming);
        h.MarkFired(E1, E2);
        h.Run = RunOf(E1, E2);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Time.Advance(TimeSpan.FromSeconds(100));

        // The album was [S,T,U]. At the change to T, Finamp reports S again with the list after T (T75), then T.
        h.Feed(Idle(Finamp(S, [])));
        var queueFirst = h.Feed(Finamp(S, [U, E1, E2]));
        Assert.False(queueFirst.InstanceStarted);
        AssertSkip(queueFirst, SkipReason.QueueFirst);
        AssertSkip(h.Feed(FinampSingle(T, NearEnd, [U, E1, E2])), SkipReason.NotLast);
        h.Feed(Idle(Finamp(U, [E1, E2])));
        AssertSkip(h.Feed(FinampSingle(U, NearEnd, [E1, E2])), SkipReason.NotLast);
        Assert.Equal(1, Fires(h));

        h.Feed(Idle(Finamp(E1, [E2])));
        var topUp = Assert.IsType<TriggerDecision.Fire>(h.Feed(Finamp(E1, [E2])).Decision);
        Assert.Equal(FireKind.TopUp, topUp.Kind);
    }

    [Fact]
    public void SG13_Finamp_single_then_the_radio_queue_first_report_folds_and_E1_tops_up()
    {
        var h = new TriggerHarness();
        AssertSingleFire(h.Feed(FinampSingle(S, NearEnd)), QueueModel.Upcoming);
        h.MarkFired(E1, E2);
        h.Run = RunOf(E1, E2);
        h.Time.Advance(TimeSpan.FromSeconds(100));

        h.Feed(Idle(Finamp(S, [])));
        AssertSkip(h.Feed(Finamp(S, [E2])), SkipReason.QueueFirst);
        var topUp = Assert.IsType<TriggerDecision.Fire>(h.Feed(Finamp(E1, [E2])).Decision);
        Assert.Equal(FireKind.TopUp, topUp.Kind);
        Assert.Equal(FireRule.NearEnd, topUp.Rule);
    }

    [Fact]
    public void SG14_One_fire_per_play_across_duplicate_and_flip_flop_reports()
    {
        // Web: duplicates of the firing snapshot, then a stop/start flip-flop of the same entry.
        var web = new TriggerHarness();
        AssertSingleFire(web.Feed(WebSingle(S, NearEnd)), QueueModel.Unknown);
        Assert.IsType<TriggerDecision.None>(web.Feed(WebSingle(S, NearEnd)).Decision);
        web.MarkFired(E1, E2);
        Assert.IsType<TriggerDecision.None>(web.Feed(WebSingle(S, NearEnd + 1)).Decision);
        web.Feed(Idle(WebSingle(S, 0)));
        web.Time.Advance(TimeSpan.FromSeconds(30));
        var again = web.Feed(WebSingle(S, NearEnd + 2));
        Assert.True(again.InstanceStarted);
        AssertSkip(again, SkipReason.Duplicate);
        Assert.Equal(1, Fires(web));

        // Finamp, a fire that found no songs (nothing to show up later): the flip-flop still does not fire again.
        var finamp = new TriggerHarness();
        AssertSingleFire(finamp.Feed(FinampSingle(S, NearEnd)), QueueModel.Upcoming);
        finamp.MarkFired();
        for (var i = 0; i < 3; i++)
        {
            finamp.Feed(Idle(Finamp(S, [])));
            finamp.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.IsNotType<TriggerDecision.Fire>(finamp.Feed(FinampSingle(S, NearEnd + i)).Decision);
        }

        Assert.Equal(1, Fires(finamp));

        // A different song afterwards is a new play: it may start the radio again.
        finamp.Feed(Idle(Finamp(T, [])));
        finamp.Time.Advance(TimeSpan.FromMinutes(5));
        AssertSingleFire(finamp.Feed(FinampSingle(T, NearEnd)), QueueModel.Upcoming);
    }

    [Fact]
    public void SG15_A_restart_in_the_middle_of_the_song_does_not_fire_again_but_a_later_replay_does()
    {
        var keys = new HashSet<string>();
        var h = new TriggerHarness();
        h.IsDuplicate = keys.Contains;
        var fire = AssertSingleFire(h.Feed(FinampSingle(S, NearEnd)), QueueModel.Upcoming);
        keys.Add(fire.DedupeKey);
        Assert.StartsWith($"{SessionId}|single|{S:N}|", fire.DedupeKey);

        // Restart: memory is gone; 20 s later the same play reports again (start time drifts a few seconds).
        h.Memory = null;
        h.Time.Advance(TimeSpan.FromSeconds(20));
        AssertSkip(h.Feed(FinampSingle(S, NearEnd + 23)), SkipReason.Duplicate);

        // The same song played again 10 minutes later is a new play.
        var later = new TriggerHarness { IsDuplicate = keys.Contains };
        later.Time.Advance(TimeSpan.FromMinutes(10));
        AssertSingleFire(later.Feed(FinampSingle(S, NearEnd)), QueueModel.Upcoming);
    }

    [Fact]
    public void SG16_A_failed_fire_retries_in_the_same_play_after_the_delay()
    {
        var h = new TriggerHarness();
        AssertSingleFire(h.Feed(WebSingle(S, NearEnd)), QueueModel.Unknown);
        Assert.False(h.MarkFailed());

        Assert.IsType<TriggerDecision.None>(h.Feed(WebSingle(S, NearEnd + 5)).Decision);
        h.Time.Advance(TriggerLimits.FireRetryDelay);
        var retry = AssertSingleFire(h.Feed(WebSingle(S, NearEnd + 20)), QueueModel.Unknown);
        Assert.Equal(2, retry.Attempt);
    }

    [Fact]
    public void SG17_Rate_limits_apply()
    {
        var h = new TriggerHarness { LastSessionFireAt = new DateTimeOffset(2026, 11, 20, 17, 59, 55, TimeSpan.Zero) };
        AssertSkip(h.Feed(WebSingle(S, NearEnd)), SkipReason.RateLimited);

        var hourly = new TriggerHarness { SessionItemsLastHour = TriggerLimits.SessionItemsPerHour };
        AssertSkip(hourly.Feed(FinampSingle(S, NearEnd)), SkipReason.RateLimited);
    }

    [Fact]
    public void SG18_Unknown_length_waits_and_a_short_song_fires_at_once()
    {
        var unknown = new TriggerHarness();
        AssertSkip(unknown.Feed(At(Finamp(S, []), 500, length: null)), SkipReason.SingleWaiting);

        var shortSong = new TriggerHarness();
        AssertSingleFire(shortSong.Feed(At(Finamp(S, []), 0, length: 60)), QueueModel.Upcoming);
    }

    [Fact]
    public void SG19_A_run_in_progress_does_not_stop_a_new_song_on_its_own_from_starting_a_new_run()
    {
        // The user replaced the radio's queue with one song S: S is not the run's, so it is a new start.
        var h = new TriggerHarness { Run = RunOf(E3, E4) };
        h.Feed(Finamp(E3, [E4]));
        h.Feed(Idle(Finamp(S, [])));
        AssertSingleFire(h.Feed(FinampSingle(S, NearEnd)), QueueModel.Upcoming);
    }
}
