using Encore.Trigger;
using static Encore.Tests.Trigger.TriggerHarness;

namespace Encore.Tests.Trigger;

/// <summary>The T15 cases from v01-design.md section 9.1 (TR01-TR37), plus a few unit checks.</summary>
public sealed class TriggerEngineTests
{
    private static readonly Guid A = Item("A"), B = Item("B"), C = Item("C"), D = Item("D");
    private static readonly Guid N = Item("N"), Nm1 = Item("N-1"), M = Item("M");
    private static readonly Guid W = Item("W"), X = Item("X"), Y = Item("Y"), Z = Item("Z"), V = Item("V"), U = Item("U");
    private static readonly Guid B1 = Item("B1");
    private static readonly Guid E1 = Item("E1"), E2 = Item("E2"), E3 = Item("E3"), E4 = Item("E4");

    private static TriggerDecision.Fire AssertFire(TriggerOutcome outcome, FireKind kind, QueueModel model)
    {
        var fire = Assert.IsType<TriggerDecision.Fire>(outcome.Decision);
        Assert.Equal(kind, fire.Kind);
        Assert.Equal(model, fire.Model);
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

    /// <summary>A Finamp track change: the stop half (no item), then the new instance.</summary>
    private static TriggerSession[] FinampChange(Guid current, Guid[] upcoming, Func<TriggerSession, TriggerSession>? change = null)
    {
        var next = Finamp(current, upcoming, change);
        return [Idle(next), next];
    }

    [Fact]
    public void TR01_Finamp_last_song_fires_start()
    {
        var h = new TriggerHarness();
        var first = h.Feed(Finamp(Nm1, [N]));
        AssertSkip(first, SkipReason.NotLast);

        var outcome = h.FeedAll(FinampChange(N, []));

        AssertFire(outcome, FireKind.Start, QueueModel.Upcoming);
        Assert.Equal(1, ((TriggerDecision.Fire)outcome.Decision).Attempt);
    }

    [Fact]
    public void TR02_Web_last_song_fires_start()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([A, B, C], 2)), FireKind.Start, QueueModel.Full);
    }

    [Fact]
    public void TR03_Finamp_mid_album_is_not_last()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(X, [Y, Z])), SkipReason.NotLast);
        AssertSkip(h.FeedAll(FinampChange(Y, [Z])), SkipReason.NotLast);
    }

    [Fact]
    public void TR04_Web_mid_album_is_not_last()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Web([A, B, C], 1)), SkipReason.NotLast);
    }

    [Fact]
    public void TR05_Not_allow_listed_user_is_skipped_and_logged_once_per_session()
    {
        var h = new TriggerHarness();
        TriggerSession Other(TriggerSession s) => s with { UserId = OtherUser, UserName = "guest" };

        var first = AssertSkip(h.Feed(Finamp(Nm1, [N], Other)), SkipReason.NotAllowed);
        var outcomes = FinampChange(N, [], Other).Select(h.Feed).ToList();

        Assert.True(first.ShouldLog);
        Assert.All(outcomes, o => Assert.False(AssertSkip(o, SkipReason.NotAllowed).ShouldLog));
    }

    [Fact]
    public void TR06_Finamp_that_never_reports_a_queue_never_fires()
    {
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(A, [])), SkipReason.NoHistory);
        foreach (var song in new[] { B, C, D, E1 })
        {
            AssertSkip(h.FeedAll(FinampChange(song, [])), SkipReason.StaleQueue);
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
        h.Feed(Finamp(Nm1, [N], All));
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
    public void TR12_Finamp_top_up_on_the_last_Encore_song()
    {
        var h = new TriggerHarness { Run = new RunInfo(E2, 2) };
        h.Feed(Finamp(E1, [E2]));
        AssertFire(h.FeedAll(FinampChange(E2, [])), FireKind.TopUp, QueueModel.Upcoming);
    }

    [Fact]
    public void TR13_Web_top_up_on_the_last_Encore_song()
    {
        var h = new TriggerHarness { Run = new RunInfo(E2, 2) };
        AssertFire(h.Feed(Web([A, B, C, D, N, E1, E2], 6)), FireKind.TopUp, QueueModel.Full);
    }

    [Fact]
    public void TR14_Top_up_blocked_when_the_user_queued_songs_after_Encore()
    {
        var h = new TriggerHarness { Run = new RunInfo(E2, 2) };
        h.Feed(Finamp(E1, [E2, U]));
        AssertSkip(h.FeedAll(FinampChange(E2, [U])), SkipReason.NotLast);

        var web = new TriggerHarness { Run = new RunInfo(E2, 2) };
        AssertSkip(web.Feed(Web([A, E1, E2, U], 2)), SkipReason.NotLast);
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
        AssertSkip(h.Feed(Finamp(X, [])), SkipReason.NoHistory);
    }

    [Fact]
    public void TR18_Finamp_start_from_idle_after_an_album_ended_is_stale()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(Nm1, [N]));
        AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Start, QueueModel.Upcoming);
        h.MarkFired();

        var idle = Idle(Finamp(N, []));
        h.FeedAll(idle, idle, idle);
        h.Time.Advance(TimeSpan.FromMinutes(10));

        AssertSkip(h.Feed(Finamp(B1, [])), SkipReason.StaleQueue);
    }

    [Fact]
    public void TR19_Finamp_jump_straight_to_the_last_song_fires()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(B, [C, D, X]));
        AssertFire(h.FeedAll(FinampChange(X, [])), FireKind.Start, QueueModel.Upcoming);
    }

    [Fact]
    public void TR20_Finamp_missed_instance_in_poll_mode_fires()
    {
        // W was never seen: poll mode went straight from V to X.
        var h = new TriggerHarness();
        h.Feed(Finamp(V, [W, X]));
        AssertFire(h.Feed(Finamp(X, [])), FireKind.Start, QueueModel.Upcoming);
    }

    [Fact]
    public void TR21_Web_stale_list_is_skipped()
    {
        // The current entry #9 is not in the stored list.
        var h = new TriggerHarness();
        var missing = Web([A, B, C], 2, s => s with { NowPlayingItem = new NowPlaying(D, "Audio"), PlaylistItemId = "playlistItem9" });
        var skip = AssertSkip(h.Feed(missing), SkipReason.StaleQueue);
        Assert.Equal(QueueModel.Unknown, skip.Model);

        // #2 is in the list but with another item id (a list from before a page reload).
        var h2 = new TriggerHarness();
        var otherItem = Web([A, B, C], 2, s => s with { NowPlayingItem = new NowPlaying(D, "Audio") });
        AssertSkip(h2.Feed(otherItem), SkipReason.StaleQueue);

        // A new web session's first song: the server still holds its initial [].
        var h3 = new TriggerHarness();
        var empty = Web([A], 0, s => s with { NowPlayingQueue = [] });
        AssertSkip(h3.Feed(empty), SkipReason.StaleQueue);
    }

    [Fact]
    public void TR22_Web_shuffle_fires_on_the_last_element()
    {
        var h = new TriggerHarness();
        var shuffled = Web([C, A, D, B], 3, s => s with { PlaybackOrder = "Shuffle" });
        AssertFire(h.Feed(shuffled), FireKind.Start, QueueModel.Full);
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
    public void TR27_Run_cap_is_skipped()
    {
        var h = new TriggerHarness { Run = new RunInfo(C, TriggerLimits.RunCap) };
        AssertSkip(h.Feed(Web([A, B, C], 2)), SkipReason.RunCap);
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
        AssertSkip(replay, SkipReason.StaleQueue);
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
        var h = new TriggerHarness { Run = new RunInfo(E2, 2) };
        h.Feed(Finamp(E1, [E2]));
        var first = AssertFire(h.FeedAll(FinampChange(E2, [])), FireKind.TopUp, QueueModel.Upcoming);
        h.MarkFired();

        h.Run = new RunInfo(E4, 4);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Time.Advance(TimeSpan.FromMinutes(4));
        h.FeedAll(FinampChange(E3, [E4]));
        var second = AssertFire(h.FeedAll(FinampChange(E4, [])), FireKind.TopUp, QueueModel.Upcoming);

        Assert.NotEqual(first.DedupeKey, second.DedupeKey);
        var source = AlbumSource.ToString("N");
        Assert.Equal(
            $"{SessionId}|{E2:N}|{source}|empty|{DedupeKey.Signature([new QueueEntry(E2, "queue")])}",
            first.DedupeKey);
        Assert.Contains(E4.ToString("N"), second.DedupeKey);
        Assert.EndsWith(DedupeKey.Signature([new QueueEntry(E4, "queue")]), second.DedupeKey);
    }

    [Fact]
    public void TR34_Web_single_song_fires_without_history()
    {
        var h = new TriggerHarness();
        AssertFire(h.Feed(Web([X], 0)), FireKind.Start, QueueModel.Full);
    }

    [Fact]
    public void TR35_Finamp_top_up_with_batch_1_uses_the_top_up_clause()
    {
        var h = new TriggerHarness { Batch = 1 };
        h.Feed(Finamp(M, [N]));
        AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Start, QueueModel.Upcoming);
        h.MarkFired();

        // Encore added E1 while N played; the add is invisible until the next stop, so E1's entry is [] and prev is N's [].
        h.Run = new RunInfo(E1, 1);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Time.Advance(TimeSpan.FromMinutes(4));
        var outcome = h.FeedAll(FinampChange(E1, []));

        AssertFire(outcome, FireKind.TopUp, QueueModel.Upcoming);
        Assert.Empty(outcome.Memory!.PrevEntryQueue!);
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
        h.Feed(Finamp(Nm1, [N]));
        var first = AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Start, QueueModel.Upcoming);
        h.MarkFired();
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.SessionItemsLastHour = 2;

        h.Feed(Idle(Finamp(N, [])));
        h.Time.Advance(TimeSpan.FromMinutes(40));
        h.FeedAll(FinampChange(Nm1, [N]));
        var second = AssertFire(h.FeedAll(FinampChange(N, [])), FireKind.Start, QueueModel.Upcoming);

        Assert.Equal(first.DedupeKey, second.DedupeKey);
    }

    [Fact]
    public void Skip_reason_is_logged_once_per_instance()
    {
        var h = new TriggerHarness();
        var first = AssertSkip(h.Feed(Web([A, B, C], 1)), SkipReason.NotLast);
        var again = AssertSkip(h.Feed(Web([A, B, C], 1)), SkipReason.NotLast);
        var nextSong = AssertSkip(h.Feed(Web([A, B, C, D], 2)), SkipReason.NotLast);

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
    public void Mark_fired_ends_the_instance_and_ignores_other_states()
    {
        var h = new TriggerHarness();
        h.Feed(Web([A, B, C], 1));
        var notFiring = h.Memory!;
        Assert.Same(notFiring, TriggerEngine.MarkFired(notFiring));
        Assert.False(TriggerEngine.MarkFailed(notFiring, h.Time.GetUtcNow()).GaveUp);

        h.Feed(Web([A, B, C], 2));
        h.MarkFired();
        Assert.Equal(InstanceState.Fired, h.Memory!.Instance!.State);
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
            "not_allowed,not_audio,no_remote_control,repeat_on,unknown_client,stale_queue,no_history,not_last,duplicate,rate_limited,run_cap",
            string.Join(',', Enum.GetValues<SkipReason>().Select(r => r.ToWire())));
        Assert.Equal("topup", FireKind.TopUp.ToWire());
        Assert.Equal("upcoming", QueueModel.Upcoming.ToWire());
    }
}
