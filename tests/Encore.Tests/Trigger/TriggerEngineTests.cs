using Encore.Trigger;
using static Encore.Tests.Trigger.TriggerHarness;

namespace Encore.Tests.Trigger;

/// <summary>
/// The trigger cases from v01-design.md section 9.1: TR01-TR37 (updated for the one-song buffer, T28) and the
/// T28 cases TR38-TR64, plus a few unit checks.
/// </summary>
public sealed class TriggerEngineTests
{
    private static readonly Guid A = Item("A"), B = Item("B"), C = Item("C"), D = Item("D");
    private static readonly Guid N = Item("N"), Nm1 = Item("N-1"), Nm2 = Item("N-2"), M = Item("M");
    private static readonly Guid W = Item("W"), X = Item("X"), Y = Item("Y"), Z = Item("Z"), V = Item("V"), U = Item("U");
    private static readonly Guid U1 = Item("U1"), U2 = Item("U2");
    private static readonly Guid A4 = Item("A4"), A5 = Item("A5"), A6 = Item("A6");
    private static readonly Guid B1 = Item("B1");
    private static readonly Guid E1 = Item("E1"), E2 = Item("E2"), E3 = Item("E3"), E4 = Item("E4");

    private static TriggerDecision.Fire AssertFire(TriggerOutcome outcome, FireKind kind, QueueModel model, FireRule? rule = null)
    {
        var fire = Assert.IsType<TriggerDecision.Fire>(outcome.Decision);
        Assert.Equal(kind, fire.Kind);
        Assert.Equal(model, fire.Model);
        if (rule is { } expected)
        {
            Assert.Equal(expected, fire.Rule);
        }

        Assert.Equal(InstanceState.Firing, outcome.Memory!.Instance!.State);
        return fire;
    }

    private static TriggerDecision.Skip AssertSkip(TriggerOutcome outcome, SkipReason reason)
    {
        var skip = Assert.IsType<TriggerDecision.Skip>(outcome.Decision);
        Assert.Equal(reason, skip.Reason);
        return skip;
    }

    private static void AssertNone(TriggerOutcome outcome) => Assert.IsType<TriggerDecision.None>(outcome.Decision);

    private static SendVerdict AssertVerdict(TriggerOutcome outcome, DeliveryVerdict verdict, string? resend = null)
    {
        var single = Assert.Single(outcome.AllVerdicts);
        Assert.Equal(verdict, single.Verdict);
        Assert.Equal(resend, single.Resend);
        return single;
    }

    /// <summary>A Finamp track change: the stop half (no item), then the new instance.</summary>
    private static TriggerSession[] FinampChange(Guid current, Guid[] upcoming, Func<TriggerSession, TriggerSession>? change = null)
    {
        var next = Finamp(current, upcoming, change);
        return [Idle(next), next];
    }

    /// <summary>Finamp N-2 then N-1 (the near-end start), marked as sent with <paramref name="sent"/>.</summary>
    private static TriggerHarness FinampStartedAtSecondToLast(Guid[] sent, TriggerHarness? harness = null)
    {
        var h = harness ?? new TriggerHarness();
        h.Feed(Finamp(Nm2, [Nm1, N]));
        AssertFire(h.FeedAll(FinampChange(Nm1, [N])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);
        h.MarkFired(sent);
        h.Run = RunOf(sent);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        return h;
    }

    [Fact]
    public void TR01_Finamp_second_to_last_song_fires_start()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(Nm2, [Nm1, N])), SkipReason.NotLast);

        var outcome = h.FeedAll(FinampChange(Nm1, [N]));

        AssertFire(outcome, FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);
        Assert.Equal(1, ((TriggerDecision.Fire)outcome.Decision).Attempt);
    }

    [Fact]
    public void TR02_Web_last_song_fires_start_by_the_fallback()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full, FireRule.LastSong);
    }

    [Fact]
    public void TR03_Finamp_mid_album_is_not_last()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(X, [Y, Z])), SkipReason.NotLast);
        AssertSkip(h.FeedAll(FinampChange(Y, [Z, V])), SkipReason.NotLast);
    }

    [Fact]
    public void TR04_Web_mid_album_is_not_last()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Web([A, B, C], 0)), SkipReason.NotLast);
    }

    [Fact]
    public void TR05_Not_allow_listed_user_is_skipped_and_logged_once_per_session()
    {
        var h = new TriggerHarness();
        TriggerSession Other(TriggerSession s) => s with { UserId = OtherUser, UserName = "guest" };

        var first = AssertSkip(h.Feed(Finamp(Nm2, [Nm1, N], Other)), SkipReason.NotAllowed);
        var outcomes = FinampChange(Nm1, [N], Other).Select(h.Feed).ToList();

        Assert.True(first.ShouldLog);
        Assert.All(outcomes, o => Assert.False(AssertSkip(o, SkipReason.NotAllowed).ShouldLog));
    }

    [Fact]
    public void TR06_Finamp_that_never_reports_a_queue_never_fires()
    {
        var h = new TriggerHarness();
        // T110: each song looks like a song on its own, so the single-song start waits for its near end (these
        // snapshots carry no song length, so it never comes).
        AssertSkip(h.Feed(Finamp(A, [])), SkipReason.SingleWaiting);
        foreach (var song in new[] { B, C, D, E1 })
        {
            AssertSkip(h.FeedAll(FinampChange(song, [])), SkipReason.SingleWaiting);
        }

        Assert.DoesNotContain(h.Outcomes, o => o.Decision is TriggerDecision.Fire);
    }

    [Fact]
    public void TR07_No_remote_control_is_skipped()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Web([A, B, C], 2, s => s with { SupportsRemoteControl = false })), SkipReason.NoRemoteControl);

        var h2 = new TriggerHarness();
        AssertSkip(h2.Feed(Web([A, B, C], 2, s => s with { SupportsRemoteControl = null })), SkipReason.NoRemoteControl);
    }

    [Fact]
    public void TR08_RepeatAll_is_skipped()
    {
        var h = new TriggerHarness();
        TriggerSession All(TriggerSession s) => s with { RepeatMode = "RepeatAll" };
        h.Feed(Finamp(Nm2, [Nm1, N], All));
        AssertSkip(h.FeedAll(FinampChange(Nm1, [N], All)), SkipReason.RepeatOn);
        AssertSkip(h.FeedAll(FinampChange(N, [], All)), SkipReason.RepeatOn);
    }

    [Fact]
    public void TR09_RepeatOne_is_skipped()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Web([A, B, C], 2, s => s with { RepeatMode = "RepeatOne" })), SkipReason.RepeatOn);
    }

    [Fact]
    public void TR10_Same_state_twice_fires_once()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full);

        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        var second = h.Feed(Web([A, B, C], 2));
        AssertNone(second);
        Assert.False(second.InstanceStarted);

        h.MarkFired();
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        AssertNone(h.Feed(Web([A, B, C], 2)));
        Assert.Single(h.Outcomes, o => o.Decision is TriggerDecision.Fire);
    }

    [Fact]
    public void TR11_Duplicate_after_restart_is_blocked_until_the_key_expires()
    {
        // The store fired this exact state 30 minutes before the restart.
        var snapshot = Web([A, B, C], 2);
        var key = DedupeKey.Build(SessionId, C, "playlistItem2", snapshot.NowPlayingQueue, null, QueueModel.Full);
        var h = new TriggerHarness();
        var firedAt = h.Time.GetUtcNow() - TimeSpan.FromMinutes(30);
        h.IsDuplicate = k => k == key && h.Time.GetUtcNow() - firedAt < TriggerLimits.DedupeTtl;

        AssertSkip(h.Feed(snapshot), SkipReason.Duplicate);

        h.Time.Advance(TimeSpan.FromMinutes(31)); // 61 minutes after the stored fire
        var fire = AssertFire(h.Feed(snapshot), FireKind.Start, QueueModel.Full);
        Assert.Equal(key, fire.DedupeKey);
    }

    [Fact]
    public void TR12_Finamp_top_up_when_one_Encore_song_is_left()
    {
        var h = new TriggerHarness { Run = RunOf(E1, E2) };
        AssertSkip(h.Feed(Finamp(N, [E1, E2])), SkipReason.NotLast);
        AssertFire(h.FeedAll(FinampChange(E1, [E2])), FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR13_Web_top_up_when_one_Encore_song_is_left()
    {
        var h = new TriggerHarness { Run = RunOf(E1, E2) };
        AssertFire(h.Feed(Web([A, B, C, D, N, E1, E2], 5)), FireKind.TopUp, QueueModel.Full, FireRule.NearEnd);
    }

    [Fact]
    public void TR14_User_songs_after_Encores_wait_until_one_is_left()
    {
        // (a) two songs to come.
        var h = new TriggerHarness { Run = RunOf(E1, E2) };
        h.Feed(Finamp(N, [E1, E2, U]));
        AssertSkip(h.FeedAll(FinampChange(E1, [E2, U])), SkipReason.NotLast);

        // (b) the one song left is the user's: a new start.
        AssertFire(h.FeedAll(FinampChange(E2, [U])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);

        // (c) the user queued two songs.
        var two = new TriggerHarness { Run = RunOf(E1, E2) };
        two.Feed(Finamp(E1, [E2, U1, U2]));
        AssertSkip(two.FeedAll(FinampChange(E2, [U1, U2])), SkipReason.NotLast);

        var web = new TriggerHarness { Run = RunOf(E1, E2) };
        AssertSkip(web.Feed(Web([A, E1, E2, U1, U2], 2)), SkipReason.NotLast);
    }

    [Theory]
    [InlineData("Episode")]
    [InlineData("AudioBook")]
    [InlineData("MusicVideo")]
    [InlineData(null)]
    public void TR15_Not_audio_is_skipped(string? type)
    {
        var h = new TriggerHarness();
        var snapshot = Web([A, B, C], 2, s => s with { NowPlayingItem = new NowPlaying(C, type) });
        AssertSkip(h.Feed(snapshot), SkipReason.NotAudio);
    }

    [Fact]
    public void TR16_Encore_own_session_is_ignored_without_memory()
    {
        var h = new TriggerHarness();
        var encore = new TriggerSession
        {
            Id = "f00dbabe00000000000000000000beef",
            UserId = Guid.Empty,
            Client = "JellyEncore",
            DeviceId = "jellyencore-1",
            SupportsRemoteControl = false,
        };
        var outcome = h.Feed(encore);
        AssertNone(outcome);
        Assert.Null(outcome.Memory);

        var noUser = h.Feed(Web([A, B, C], 2, s => s with { UserId = null }));
        AssertNone(noUser);
        Assert.Null(noUser.Memory);

        var ownDevice = h.Feed(Web([A, B, C], 2, s => s with { DeviceId = OwnDeviceId }));
        AssertNone(ownDevice);
        Assert.Null(ownDevice.Memory);
    }

    [Fact]
    public void TR17_Finamp_without_history_is_skipped()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(X, [])), SkipReason.SingleWaiting); // T110: a song on its own waits for its near end
        AssertSkip(new TriggerHarness().Feed(Finamp(X, [Y, Z])), SkipReason.NotLast);
        AssertSkip(new TriggerHarness().Feed(Finamp(X, [Y])), SkipReason.NoHistory);
    }

    [Fact]
    public void TR18_Finamp_start_from_idle_after_an_album_ended_is_stale()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(Nm1, [N]));
        AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Start, QueueModel.Upcoming, FireRule.LastSong);
        h.MarkFired();

        var idle = Idle(Finamp(N, []));
        h.FeedAll(idle, idle, idle);
        h.Time.Advance(TimeSpan.FromMinutes(10));

        AssertSkip(h.Feed(Finamp(B1, [])), SkipReason.SingleWaiting); // T110: now the single-song start
    }

    [Fact]
    public void TR19_Finamp_jump_straight_to_the_last_song_fires_by_the_fallback()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(B, [C, D, X]));
        AssertFire(h.FeedAll(FinampChange(X, [])), FireKind.Start, QueueModel.Upcoming, FireRule.LastSong);
    }

    [Fact]
    public void TR20_Finamp_missed_instance_in_poll_mode_fires_by_the_fallback()
    {
        // W was never seen: poll mode went straight from V to X.
        var h = new TriggerHarness();
        h.Feed(Finamp(V, [W, X]));
        AssertFire(h.Feed(Finamp(X, [])), FireKind.Start, QueueModel.Upcoming, FireRule.LastSong);
    }

    [Fact]
    public void TR21_Web_stale_list_is_skipped()
    {
        // The current entry #9 is not in the stored list.
        var h = new TriggerHarness();
        var missing = Web([A, B, C], 2, s => s with { NowPlayingItem = new NowPlaying(D, "Audio"), PlaylistItemId = "playlistItem9" });
        var skip = AssertSkip(h.Feed(missing), SkipReason.SingleWaiting);
        Assert.Equal(QueueModel.Unknown, skip.Model);

        // #2 is in the list but with another item id (a list from before a page reload).
        var h2 = new TriggerHarness();
        var otherItem = Web([A, B, C], 2, s => s with { NowPlayingItem = new NowPlaying(D, "Audio") });
        AssertSkip(h2.Feed(otherItem), SkipReason.SingleWaiting);

        // A new web session's first song: the server still holds its initial [].
        var h3 = new TriggerHarness();
        var empty = Web([A], 0, s => s with { NowPlayingQueue = [] });
        AssertSkip(h3.Feed(empty), SkipReason.SingleWaiting);

        // A song the run added keeps stale_queue: it is never a song on its own (T110).
        var h4 = new TriggerHarness { Run = RunOf(D) };
        AssertSkip(h4.Feed(missing), SkipReason.StaleQueue);
    }

    [Fact]
    public void TR22_Web_shuffle_fires_on_the_last_element_by_the_fallback()
    {
        var h = new TriggerHarness();
        var shuffled = Web([C, A, D, B], 3, s => s with { PlaybackOrder = "Shuffle" });
        AssertFire(h.Feed(shuffled), FireKind.Start, QueueModel.Full, FireRule.LastSong);
    }

    [Fact]
    public void TR23_Unknown_client_is_skipped()
    {
        var h = new TriggerHarness();
        var symfonium = Finamp(X, [Y], s => s with { Client = "Symfonium", PlaylistItemId = null });
        var skip = AssertSkip(h.Feed(symfonium), SkipReason.UnknownClient);
        Assert.Equal(QueueModel.Unknown, skip.Model);
    }

    [Fact]
    public void TR24_Paused_on_the_last_song_still_fires()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([A, B, C], 2, s => s with { IsPaused = true })), FireKind.Start, QueueModel.Full);
    }

    [Fact]
    public void TR25_Session_cooldown_then_fires()
    {
        var h = new TriggerHarness();
        h.LastSessionFireAt = h.Time.GetUtcNow() - TimeSpan.FromSeconds(10);
        AssertSkip(h.Feed(Web([A, B, C], 2)), SkipReason.RateLimited);

        h.Time.Advance(TimeSpan.FromSeconds(15)); // 25 s after the last fire
        AssertFire(h.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full);
    }

    [Fact]
    public void TR26_Hourly_caps_are_rate_limited()
    {
        var session = new TriggerHarness { SessionItemsLastHour = 30 };
        AssertSkip(session.Feed(Web([A, B, C], 2)), SkipReason.RateLimited);

        var global = new TriggerHarness { GlobalItemsLastHour = 119 };
        AssertSkip(global.Feed(Web([A, B, C], 2)), SkipReason.RateLimited);

        var underCap = new TriggerHarness { SessionItemsLastHour = 28, GlobalItemsLastHour = 118 };
        AssertFire(underCap.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full);
    }

    [Fact]
    public void Hourly_counts_are_read_only_when_the_engine_reaches_the_rate_limit_guard()
    {
        var calls = 0;
        var h = new TriggerHarness { SessionItemsSource = () => ++calls, GlobalItemsSource = () => ++calls };

        AssertSkip(h.Feed(Web([A, B, C], 0)), SkipReason.NotLast);
        Assert.Equal(0, calls);

        AssertFire(h.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TR27_Run_cap_is_skipped()
    {
        var h = new TriggerHarness { Run = new RunInfo(C, TriggerLimits.RunCap) };
        AssertSkip(h.Feed(Web([A, B, C], 2)), SkipReason.RunCap);
    }

    [Fact]
    public void TR27b_A_capped_old_run_does_not_block_a_fresh_web_start()
    {
        // The run's last added song E1 is not in this fresh queue, so this fire is a start with a fresh cap.
        var h = new TriggerHarness { Run = new RunInfo(E1, TriggerLimits.RunCap) };
        AssertFire(h.Feed(Web([A, B, C], 1)), FireKind.Start, QueueModel.Full, FireRule.NearEnd);
    }

    [Fact]
    public void TR27c_A_capped_old_run_does_not_block_a_fresh_Finamp_start()
    {
        var h = new TriggerHarness { Run = new RunInfo(E2, TriggerLimits.RunCap, [E1, E2]) };
        AssertSkip(h.Feed(Finamp(Nm2, [Nm1, N])), SkipReason.NotLast);
        AssertFire(h.FeedAll(FinampChange(Nm1, [N])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR27d_A_start_still_obeys_the_hourly_limit_when_the_old_run_is_capped()
    {
        var h = new TriggerHarness { Run = new RunInfo(E1, TriggerLimits.RunCap), SessionItemsLastHour = 29 };
        AssertSkip(h.Feed(Web([A, B, C], 1)), SkipReason.RateLimited);
    }

    [Fact]
    public void TR28_Failed_fire_retries_once_after_15_seconds_then_gives_up()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full);
        Assert.False(h.MarkFailed());
        Assert.Equal(InstanceState.FireFailed, h.Memory!.Instance!.State);

        h.Time.Advance(TimeSpan.FromSeconds(10));
        AssertNone(h.Feed(Web([A, B, C], 2)));

        h.Time.Advance(TimeSpan.FromSeconds(5)); // 15 s after the failure
        var retry = AssertFire(h.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full);
        Assert.Equal(2, retry.Attempt);

        Assert.True(h.MarkFailed());
        Assert.Equal(InstanceState.GaveUp, h.Memory!.Instance!.State);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        AssertNone(h.Feed(Web([A, B, C], 2)));
        Assert.Equal(2, h.Outcomes.Count(o => o.Decision is TriggerDecision.Fire));
    }

    [Fact]
    public void TR29_Repeat_turned_off_mid_song_fires()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(W, [X]));
        h.Feed(Idle(Finamp(W, [X])));
        AssertSkip(h.Feed(Finamp(X, [], s => s with { RepeatMode = "RepeatAll" })), SkipReason.RepeatOn);

        h.Time.Advance(TimeSpan.FromSeconds(5));
        var outcome = h.Feed(Finamp(X, []));
        Assert.False(outcome.InstanceStarted);
        AssertFire(outcome, FireKind.Start, QueueModel.Upcoming);
    }

    [Fact]
    public void TR30_Stop_gap_race_keeps_the_entry_queue()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(Nm1, [N]));
        // The server applied the stop's queue before clearing the item.
        h.Feed(Finamp(Nm1, []));
        h.Feed(Idle(Finamp(Nm1, [])));

        var outcome = h.Feed(Finamp(N, []));

        AssertFire(outcome, FireKind.Start, QueueModel.Upcoming);
        Assert.Equal([new QueueEntry(N, "queue")], outcome.Memory!.PrevEntryQueue);
    }

    [Fact]
    public void TR31_Replay_of_the_same_song_after_a_gap_is_stale()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(W, [X]));
        AssertFire(h.FeedAll(FinampChange(X, [])), FireKind.Start, QueueModel.Upcoming);
        h.MarkFired();

        h.Feed(Idle(Finamp(X, [])));
        h.Time.Advance(TimeSpan.FromMinutes(5));
        var replay = h.Feed(Finamp(X, []));

        Assert.True(replay.InstanceStarted);
        AssertSkip(replay, SkipReason.SingleWaiting);
    }

    [Fact]
    public void TR32_Allow_list_matches_by_id_after_a_rename()
    {
        var h = new TriggerHarness();
        var renamed = Web([A, B, C], 2, s => s with { UserName = "nate" });
        AssertFire(h.Feed(renamed), FireKind.Start, QueueModel.Full);
    }

    [Fact]
    public void TR33_Dedupe_key_differs_per_top_up_and_holds_item_and_prev_signature()
    {
        var h = new TriggerHarness { Run = RunOf(E1, E2) };
        h.Feed(Finamp(N, [E1, E2]));
        var first = AssertFire(h.FeedAll(FinampChange(E1, [E2])), FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
        h.MarkFired(E3, E4);

        h.Run = RunOf(E1, E2, E3, E4);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Time.Advance(TimeSpan.FromMinutes(4));
        AssertSkip(h.FeedAll(FinampChange(E2, [E3, E4])), SkipReason.NotLast);
        h.Time.Advance(TimeSpan.FromMinutes(4));
        var second = AssertFire(h.FeedAll(FinampChange(E3, [E4])), FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);

        Assert.NotEqual(first.DedupeKey, second.DedupeKey);
        var source = AlbumSource.ToString("N");
        Assert.Equal(
            $"{SessionId}|{E1:N}|{source}|{DedupeKey.Signature([new QueueEntry(E2, "queue")])}|{DedupeKey.Signature([new QueueEntry(E1, "queue"), new QueueEntry(E2, "queue")])}",
            first.DedupeKey);
        Assert.Contains(E3.ToString("N"), second.DedupeKey);
        Assert.EndsWith(DedupeKey.Signature([new QueueEntry(E3, "queue"), new QueueEntry(E4, "queue")]), second.DedupeKey);
    }

    [Fact]
    public void TR34_Web_single_song_fires_without_history()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([X], 0)), FireKind.Start, QueueModel.Full, FireRule.LastSong);
    }

    [Fact]
    public void TR35_Finamp_top_up_with_batch_1_uses_the_top_up_clause()
    {
        var h = new TriggerHarness { Batch = 1 };
        h.Feed(Finamp(M, [N]));
        AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Start, QueueModel.Upcoming, FireRule.LastSong);
        h.MarkFired(E1);

        // Encore added E1 while N played; the add is invisible until the next stop, so E1's entry is [] and prev is N's [].
        h.Run = RunOf(E1);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Time.Advance(TimeSpan.FromMinutes(4));
        var outcome = h.FeedAll(FinampChange(E1, []));

        // Not already_queued: the current song E1 is one of the previous instance's sent songs.
        AssertFire(outcome, FireKind.TopUp, QueueModel.Upcoming, FireRule.LastSong);
        Assert.Empty(outcome.Memory!.PrevEntryQueue!);
        Assert.Equal([E1], outcome.Memory.PrevSentIds);
    }

    [Fact]
    public void TR36_Top_up_clause_needs_history()
    {
        // Fresh memory after a restart; the run was rebuilt from the store.
        var h = new TriggerHarness { Run = new RunInfo(E1, 2) };
        AssertSkip(h.Feed(Finamp(E1, [])), SkipReason.NoHistory);
    }

    [Fact]
    public void TR37_Finamp_album_replayed_within_the_hour_fires_again()
    {
        var h = new TriggerHarness { IsDuplicate = static _ => true };
        h.Feed(Finamp(Nm2, [Nm1, N]));
        var first = AssertFire(h.FeedAll(FinampChange(Nm1, [N])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);
        h.MarkFired(E1, E2);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.SessionItemsLastHour = 2;

        h.Feed(Idle(Finamp(Nm1, [N])));
        h.Time.Advance(TimeSpan.FromMinutes(40));
        h.FeedAll(FinampChange(Nm2, [Nm1, N]));
        var second = AssertFire(h.FeedAll(FinampChange(Nm1, [N])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);

        Assert.Equal(first.DedupeKey, second.DedupeKey);
    }

    // ---- T28: the one-song buffer ----

    [Fact]
    public void TR38_Fallback_fires_when_the_near_end_fire_was_missed()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(Nm1, [N])), SkipReason.NoHistory);
        AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Start, QueueModel.Upcoming, FireRule.LastSong);
    }

    [Fact]
    public void TR39_Web_second_to_last_fires_start()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([A, B, C], 1)), FireKind.Start, QueueModel.Full, FireRule.NearEnd);
    }

    [Fact]
    public void TR40_Finamp_near_end_needs_history()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(Nm1, [N])), SkipReason.NoHistory);
    }

    [Fact]
    public void TR41_Finamp_stale_one_entry_list_after_idle_is_skipped()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(A4, [A5, A6])), SkipReason.NotLast);
        var idle = Idle(Finamp(A4, [A5, A6]));
        h.FeedAll(idle, idle, idle);
        h.Time.Advance(TimeSpan.FromMinutes(10));

        AssertSkip(h.Feed(Finamp(B1, [A6])), SkipReason.StaleQueue);
    }

    [Fact]
    public void TR42_Finamp_jump_to_the_second_to_last_fires()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(B, [C, D, X, Y]));
        AssertFire(h.FeedAll(FinampChange(X, [Y])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR43_Web_shuffle_fires_on_the_next_to_last_element()
    {
        var h = new TriggerHarness();
        var shuffled = Web([C, A, D, B], 2, s => s with { PlaybackOrder = "Shuffle" });
        AssertFire(h.Feed(shuffled), FireKind.Start, QueueModel.Full, FireRule.NearEnd);
    }

    [Fact]
    public void TR44_Near_end_right_after_a_re_send()
    {
        // N-1 sent E1, E2 (fired at the near end); at N they are missing, so N re-sends them.
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));
        AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Resend, QueueModel.Upcoming, FireRule.Resend);
        h.MarkFired(E1, E2);
        h.Run = RunOf(E1, E2, E1, E2);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Pending = [h.SendNow(8, [E1, E2], isResend: true)];

        // E1 starts; N's entry was [] (the re-send was invisible), so freshness comes from the run's songs.
        h.Time.Advance(TimeSpan.FromSeconds(200));
        var outcome = h.FeedAll(FinampChange(E1, [E2]));

        AssertVerdict(outcome, DeliveryVerdict.Confirmed);
        AssertFire(outcome, FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR45_Batch_1_tops_up_one_song_at_a_time()
    {
        var h = new TriggerHarness { Batch = 1 };
        h.Feed(Finamp(Nm2, [Nm1, N]));
        AssertFire(h.FeedAll(FinampChange(Nm1, [N])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);
        h.MarkFired(E1);
        h.Run = RunOf(E1);
        h.LastSessionFireAt = h.Time.GetUtcNow();

        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertFire(h.FeedAll(FinampChange(N, [E1])), FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
        h.MarkFired(E2);
        h.Run = RunOf(E1, E2);
        h.LastSessionFireAt = h.Time.GetUtcNow();

        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertFire(h.FeedAll(FinampChange(E1, [E2])), FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR46_Already_queued_blocks_the_fallback_after_a_lost_send()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Time.Advance(TimeSpan.FromMinutes(3));

        // Nothing pending (the store failed, so nothing was armed); the songs are not in N's report.
        AssertSkip(h.FeedAll(FinampChange(N, [])), SkipReason.AlreadyQueued);
        Assert.Single(h.Outcomes, o => o.Decision is TriggerDecision.Fire);
    }

    [Fact]
    public void TR47_Already_queued_keeps_dry_run_to_one_fire_per_queue_end()
    {
        // Finamp: dry-run songs never show up.
        var finamp = FinampStartedAtSecondToLast([E1, E2]);
        finamp.Time.Advance(TimeSpan.FromMinutes(3));
        AssertSkip(finamp.FeedAll(FinampChange(N, [])), SkipReason.AlreadyQueued);
        Assert.Single(finamp.Outcomes, o => o.Decision is TriggerDecision.Fire);

        // Web: b/#1 fires a dry run, then c/#2 is the last song.
        var web = new TriggerHarness();
        AssertFire(web.Feed(Web([A, B, C], 1)), FireKind.Start, QueueModel.Full, FireRule.NearEnd);
        web.MarkFired(E1, E2);
        web.LastSessionFireAt = web.Time.GetUtcNow();
        web.Time.Advance(TimeSpan.FromMinutes(3));
        AssertSkip(web.Feed(Web([A, B, C], 2)), SkipReason.AlreadyQueued);
        Assert.Single(web.Outcomes, o => o.Decision is TriggerDecision.Fire);
    }

    [Fact]
    public void TR48_Already_queued_does_not_block_the_radio_song()
    {
        // The poll missed N: straight from N-1 to E1.
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        AssertFire(h.FeedAll(FinampChange(E1, [E2])), FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR49_Verdict_confirmed_when_the_songs_are_in_the_report()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));

        var outcome = h.FeedAll(FinampChange(N, [E1, E2]));

        Assert.Equal(7, AssertVerdict(outcome, DeliveryVerdict.Confirmed).TriggerId);
        AssertSkip(outcome, SkipReason.NotLast);
    }

    [Fact]
    public void TR50_Verdict_confirmed_by_the_current_song_then_fallback_top_up()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];

        // The poll missed N and E1.
        h.Time.Advance(TimeSpan.FromSeconds(300));
        var outcome = h.Feed(Finamp(E2, []));

        AssertVerdict(outcome, DeliveryVerdict.Confirmed);
        AssertFire(outcome, FireKind.TopUp, QueueModel.Upcoming, FireRule.LastSong);
    }

    [Fact]
    public void TR51_Web_fallback_top_up_on_the_last_Encore_song()
    {
        var h = new TriggerHarness { Run = RunOf(E1, E2) };
        AssertFire(h.Feed(Web([A, B, C, D, N, E1, E2], 6)), FireKind.TopUp, QueueModel.Full, FireRule.LastSong);
    }

    [Fact]
    public void TR52_Missing_send_is_re_sent_Finamp()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));

        var outcome = h.FeedAll(FinampChange(N, []));

        AssertVerdict(outcome, DeliveryVerdict.Missing, ResendNames.Pending);
        var fire = AssertFire(outcome, FireKind.Resend, QueueModel.Upcoming, FireRule.Resend);
        Assert.Equal([E1, E2], fire.ItemIds);
        Assert.Equal($"{SessionId}|resend|7", fire.DedupeKey);
    }

    [Fact]
    public void TR53_Missing_send_is_re_sent_web()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([A, B, C], 1)), FireKind.Start, QueueModel.Full, FireRule.NearEnd);
        h.MarkFired(E1, E2);
        h.Run = RunOf(E1, E2);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Pending = [h.SendNow(3, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));

        var outcome = h.Feed(Web([A, B, C], 2));

        AssertVerdict(outcome, DeliveryVerdict.Missing, ResendNames.Pending);
        Assert.Equal([E1, E2], AssertFire(outcome, FireKind.Resend, QueueModel.Full, FireRule.Resend).ItemIds);
    }

    [Fact]
    public void TR54_A_report_inside_the_delivery_lag_defers_the_verdict()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(1));

        var outcome = h.FeedAll(FinampChange(N, []));

        AssertVerdict(outcome, DeliveryVerdict.Deferred);
        AssertSkip(outcome, SkipReason.AlreadyQueued);
    }

    [Fact]
    public void TR55_A_poll_report_gets_the_poll_interval_on_top_of_the_lag()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.FromPoll = true;
        h.PollSeconds = 10;
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(8));
        AssertVerdict(h.FeedAll(FinampChange(N, [])), DeliveryVerdict.Deferred);

        // Socket reports need only the 3 s lag; a poll needs 13 s.
        Assert.Equal(TimeSpan.FromSeconds(13), DeliveryJudge.Lag(fromPoll: true, pollSeconds: 10));
        Assert.Equal(TimeSpan.FromSeconds(3), DeliveryJudge.Lag(fromPoll: false, pollSeconds: 10));
    }

    [Fact]
    public void TR56_Deferred_then_confirmed_at_the_next_change()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(1));
        AssertVerdict(h.FeedAll(FinampChange(N, [])), DeliveryVerdict.Deferred);

        h.Time.Advance(TimeSpan.FromSeconds(199));
        var outcome = h.FeedAll(FinampChange(E1, [E2]));

        AssertVerdict(outcome, DeliveryVerdict.Confirmed);
        AssertFire(outcome, FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR57_A_lost_re_send_is_not_sent_again()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(8, [E1, E2], isResend: true)];
        h.Time.Advance(TimeSpan.FromSeconds(60));

        var outcome = h.FeedAll(FinampChange(N, []));

        AssertVerdict(outcome, DeliveryVerdict.Missing, ResendNames.AlreadyResent);
        AssertSkip(outcome, SkipReason.AlreadyQueued);
        Assert.Empty(outcome.Memory!.Instance!.ResendIds);
    }

    [Fact]
    public void TR58_No_re_send_while_the_user_has_two_songs_left()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));

        var outcome = h.FeedAll(FinampChange(N, [U1, U2]));
        AssertVerdict(outcome, DeliveryVerdict.Missing, ResendNames.SkippedUserSongs);
        AssertSkip(outcome, SkipReason.NotLast);

        h.Pending = [];
        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertFire(h.FeedAll(FinampChange(U1, [U2])), FireKind.Start, QueueModel.Upcoming, FireRule.NearEnd);
    }

    [Fact]
    public void TR59_Re_send_with_one_user_song_left()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));

        var outcome = h.FeedAll(FinampChange(N, [U]));

        AssertVerdict(outcome, DeliveryVerdict.Missing, ResendNames.Pending);
        Assert.Equal([E1, E2], AssertFire(outcome, FireKind.Resend, QueueModel.Upcoming, FireRule.Resend).ItemIds);
    }

    [Fact]
    public void TR60_Re_send_waits_for_the_cooldown()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.LastSessionFireAt = h.Time.GetUtcNow() - TimeSpan.FromSeconds(10);

        AssertSkip(h.FeedAll(FinampChange(N, [])), SkipReason.RateLimited);
        h.Pending = [];

        h.Time.Advance(TimeSpan.FromSeconds(11));
        var outcome = h.Feed(Finamp(N, []));
        Assert.False(outcome.InstanceStarted);
        AssertFire(outcome, FireKind.Resend, QueueModel.Upcoming, FireRule.Resend);
    }

    [Fact]
    public void TR61_Re_send_counts_against_the_caps()
    {
        var capped = FinampStartedAtSecondToLast([E1, E2]);
        capped.Run = new RunInfo(E2, TriggerLimits.RunCap, [E1, E2]);
        capped.Pending = [capped.SendNow(7, [E1, E2])];
        capped.Time.Advance(TimeSpan.FromSeconds(60));
        AssertSkip(capped.FeedAll(FinampChange(N, [])), SkipReason.RunCap);

        var hourly = FinampStartedAtSecondToLast([E1, E2]);
        hourly.SessionItemsLastHour = 29;
        hourly.Pending = [hourly.SendNow(7, [E1, E2])];
        hourly.Time.Advance(TimeSpan.FromSeconds(60));
        AssertSkip(hourly.FeedAll(FinampChange(N, [])), SkipReason.RateLimited);
    }

    [Fact]
    public void TR62_Re_send_waits_while_repeat_is_on()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));

        // The verdict is given even under repeat; the re-send itself waits.
        var outcome = h.FeedAll(FinampChange(N, [], s => s with { RepeatMode = "RepeatAll" }));
        AssertVerdict(outcome, DeliveryVerdict.Missing, ResendNames.Pending);
        AssertSkip(outcome, SkipReason.RepeatOn);
        h.Pending = [];

        h.Time.Advance(TimeSpan.FromSeconds(5));
        AssertFire(h.Feed(Finamp(N, [])), FireKind.Resend, QueueModel.Upcoming, FireRule.Resend);
    }

    [Fact]
    public void TR63_Re_send_is_retried_on_a_clear_error_then_gives_up()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];
        h.Time.Advance(TimeSpan.FromSeconds(60));
        AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Resend, QueueModel.Upcoming);
        h.Pending = [];
        Assert.False(h.MarkFailed());

        h.Time.Advance(TimeSpan.FromSeconds(15));
        var retry = AssertFire(h.Feed(Finamp(N, [])), FireKind.Resend, QueueModel.Upcoming);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal([E1, E2], retry.ItemIds);

        Assert.True(h.MarkFailed());
        h.Time.Advance(TimeSpan.FromMinutes(1));
        AssertNone(h.Feed(Finamp(N, [])));
    }

    [Fact]
    public void TR64_Top_up_when_a_user_song_sits_before_Encores_last()
    {
        var h = new TriggerHarness { Run = RunOf(E1, E2) };
        AssertSkip(h.Feed(Finamp(E1, [U, E2])), SkipReason.NotLast);
        AssertFire(h.FeedAll(FinampChange(U, [E2])), FireKind.TopUp, QueueModel.Upcoming, FireRule.NearEnd);
    }

    // ---- unit checks ----

    [Fact]
    public void Skip_reason_is_logged_once_per_instance()
    {
        var h = new TriggerHarness();
        var first = AssertSkip(h.Feed(Web([A, B, C, D], 0)), SkipReason.NotLast);
        var again = AssertSkip(h.Feed(Web([A, B, C, D], 0)), SkipReason.NotLast);
        var nextSong = AssertSkip(h.Feed(Web([A, B, C, D], 1)), SkipReason.NotLast);

        Assert.True(first.ShouldLog);
        Assert.False(again.ShouldLog);
        Assert.True(nextSong.ShouldLog);
    }

    [Fact]
    public void New_instance_starts_on_item_change_playlist_item_change_and_after_idle()
    {
        var h = new TriggerHarness();
        Assert.True(h.Feed(Web([A, B, A], 0)).InstanceStarted);
        Assert.False(h.Feed(Web([A, B, A], 0)).InstanceStarted);
        Assert.True(h.Feed(Web([A, B, A], 2)).InstanceStarted); // same item, new PlaylistItemId
        Assert.False(h.Feed(Idle(Web([A, B, A], 2))).InstanceStarted);
        Assert.True(h.Feed(Web([A, B, A], 2)).InstanceStarted);
        Assert.Equal(3, h.Memory!.InstanceCount);
    }

    [Fact]
    public void Mark_fired_ends_the_instance_records_sent_ids_and_ignores_other_states()
    {
        var h = new TriggerHarness();
        h.Feed(Web([A, B, C, D], 0));
        var notFiring = h.Memory!;
        Assert.Same(notFiring, TriggerEngine.MarkFired(notFiring, [E1]));
        Assert.False(TriggerEngine.MarkFailed(notFiring, h.Time.GetUtcNow()).GaveUp);

        h.Feed(Web([A, B, C, D], 2));
        h.MarkFired(E1, E2);
        Assert.Equal(InstanceState.Fired, h.Memory!.Instance!.State);
        Assert.Equal([E1, E2], h.Memory.Instance.SentIds);

        h.Feed(Web([A, B, C, D], 3));
        Assert.Equal([E1, E2], h.Memory!.PrevSentIds);
        Assert.Empty(h.Memory.Instance!.SentIds);
    }

    [Fact]
    public void Verdicts_are_given_only_on_the_first_snapshot_of_an_instance()
    {
        var h = FinampStartedAtSecondToLast([E1, E2]);
        h.Pending = [h.SendNow(7, [E1, E2])];

        // Still the instance that sent: no verdict, however late.
        h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(h.Feed(Finamp(Nm1, [N])).AllVerdicts);
    }

    [Fact]
    public void Delivery_judge_is_per_send()
    {
        var t0 = new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
        var later = t0 + TimeSpan.FromSeconds(60);

        // One song of the send shows up: the whole send arrived (the other was removed by the user).
        Assert.Equal(DeliveryVerdict.Confirmed, DeliveryJudge.Judge([E1, E2], N, [new QueueEntry(E2, "queue")], t0, later, false, 10));
        Assert.Equal(DeliveryVerdict.Confirmed, DeliveryJudge.Judge([E1, E2], E1, [], t0, later, false, 10));
        Assert.Equal(DeliveryVerdict.Missing, DeliveryJudge.Judge([E1, E2], N, [], t0, later, false, 10));
        Assert.Equal(DeliveryVerdict.Deferred, DeliveryJudge.Judge([E1, E2], N, [], t0, t0 + TimeSpan.FromSeconds(2), false, 10));
        Assert.Equal(DeliveryVerdict.Missing, DeliveryJudge.Judge([E1, E2], N, [], t0, t0 + TimeSpan.FromSeconds(3), false, 10));
    }

    [Fact]
    public void Dedupe_key_shape_for_the_full_model()
    {
        var queue = new[] { new QueueEntry(A, "playlistItem0"), new QueueEntry(C, "playlistItem1") };
        var key = DedupeKey.Build("s1", C, "playlistItem1", queue, [new QueueEntry(B, "x")], QueueModel.Full);

        Assert.Equal($"s1|{C:N}|playlistItem1|{DedupeKey.Signature(queue)}|-", key);
        Assert.Equal("null", DedupeKey.Signature(null));
        Assert.Equal("empty", DedupeKey.Signature([]));
        Assert.Matches("^[0-9a-f]{16}$", DedupeKey.Signature(queue));
        Assert.NotEqual(DedupeKey.Signature(queue), DedupeKey.Signature([.. queue.Reverse()]));
    }

    [Fact]
    public void Wire_names_match_the_design()
    {
        Assert.Equal(
            "not_allowed,not_audio,no_remote_control,repeat_on,unknown_client,stale_queue,no_history,not_last,duplicate,rate_limited,run_cap,already_queued,queue_first,single_waiting",
            string.Join(',', Enum.GetValues<SkipReason>().Select(r => r.ToWire())));
        Assert.Equal("start,topup,resend", string.Join(',', Enum.GetValues<FireKind>().Select(k => k.ToWire())));
        Assert.Equal("near_end,last_song,resend,single", string.Join(',', Enum.GetValues<FireRule>().Select(r => r.ToWire())));
        Assert.Equal("upcoming", QueueModel.Upcoming.ToWire());
    }
}
