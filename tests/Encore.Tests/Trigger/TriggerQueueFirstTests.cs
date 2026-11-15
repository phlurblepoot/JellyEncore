using Encore.Trigger;
using static Encore.Tests.Trigger.TriggerHarness;

namespace Encore.Tests.Trigger;

/// <summary>
/// T75: Finamp reports each song change twice, about 20 ms apart: first the OLD song with the NEW upcoming list
/// (the queue-first report), then the new song with that list. Live 2026-11-15 03:40, session 4e440a00:
/// inst 6 b7f8 [3], inst 7 b7f8 [2], inst 8 3f50 [2], inst 9 3f50 [1], inst 10 b3d4 [1] gave stale_queue twice and no start.
/// The skip reason is checked by its wire name, so these tests also build against the engine before the fix.
/// </summary>
public sealed class TriggerQueueFirstTests
{
    private static readonly Guid Before = Item("a1c0"), S6 = Item("b7f8"), S8 = Item("3f50"), S10 = Item("b3d4"), Last = Item("last");
    private static readonly Guid E1 = Item("E1"), E2 = Item("E2"), B1 = Item("B1");

    private static List<TriggerDecision.Fire> Fires(TriggerHarness h) => [.. h.Outcomes.Select(o => o.Decision).OfType<TriggerDecision.Fire>()];

    private static void AssertSkip(TriggerOutcome outcome, string wire)
    {
        var skip = Assert.IsType<TriggerDecision.Skip>(outcome.Decision);
        Assert.Equal(wire, skip.Reason.ToWire());
    }

    /// <summary>A Finamp song change in the live order: the stop, the old song with the new list, then the new song.</summary>
    private static TriggerSession[] QueueFirstChange(Guid old, Guid next, Guid[] upcoming) =>
        [Idle(Finamp(old, upcoming)), Finamp(old, upcoming), Finamp(next, upcoming)];

    /// <summary>The older order: the stop, then the new song with the new list.</summary>
    private static TriggerSession[] ItemFirstChange(Guid next, Guid[] upcoming) =>
        [Idle(Finamp(next, upcoming)), Finamp(next, upcoming)];

    /// <summary>History up to inst 6: a song before b7f8, then b7f8 with three songs to come.</summary>
    private static TriggerHarness AtInstance6()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(Before, [S6, S8, S10, Last]));
        h.Time.Advance(TimeSpan.FromMinutes(3));
        h.FeedAll(ItemFirstChange(S6, [S8, S10, Last]));
        return h;
    }

    [Fact]
    public void QF01_Live_queue_first_sequence_fires_one_near_end_start_at_the_second_to_last_song()
    {
        var h = AtInstance6();

        h.Time.Advance(TimeSpan.FromMinutes(3));
        var change7 = QueueFirstChange(S6, S8, [S10, Last]).Select(h.Feed).ToList();
        AssertSkip(change7[1], "queue_first");
        Assert.False(change7[1].InstanceStarted);
        AssertSkip(change7[2], "not_last");
        Assert.True(change7[2].InstanceStarted);

        h.Time.Advance(TimeSpan.FromMinutes(3));
        var change9 = QueueFirstChange(S8, S10, [Last]).Select(h.Feed).ToList();
        AssertSkip(change9[1], "queue_first");
        var fire = Assert.IsType<TriggerDecision.Fire>(change9[2].Decision);
        Assert.Equal(FireKind.Start, fire.Kind);
        Assert.Equal(FireRule.NearEnd, fire.Rule);
        Assert.Equal(QueueModel.Upcoming, fire.Model);

        // More progress reports of the same song: no second fire.
        h.MarkFired(E1, E2);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Feed(Finamp(S10, [Last]));
        h.Feed(Finamp(S10, [Last, E1, E2]));

        Assert.Single(Fires(h));
        Assert.DoesNotContain(h.Outcomes, o => o.Decision is TriggerDecision.Skip { Reason: SkipReason.StaleQueue });
    }

    [Fact]
    public void QF02_Item_first_order_still_fires_once()
    {
        var h = AtInstance6();

        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertSkip(h.FeedAll(ItemFirstChange(S8, [S10, Last])), "not_last");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        var outcome = h.FeedAll(ItemFirstChange(S10, [Last]));

        var fire = Assert.IsType<TriggerDecision.Fire>(outcome.Decision);
        Assert.Equal(FireKind.Start, fire.Kind);
        Assert.Equal(FireRule.NearEnd, fire.Rule);
        h.MarkFired(E1, E2);
        h.Feed(Finamp(S10, [Last]));
        Assert.Single(Fires(h));
    }

    [Fact]
    public void QF03_Queue_first_then_last_song_fires_by_the_fallback()
    {
        // The near-end start was missed (first report since Encore started), so the last song's fallback must fire.
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Finamp(S10, [Last])), "no_history");

        h.Time.Advance(TimeSpan.FromMinutes(3));
        var change = QueueFirstChange(S10, Last, []).Select(h.Feed).ToList();

        AssertSkip(change[1], "queue_first");
        var fire = Assert.IsType<TriggerDecision.Fire>(change[2].Decision);
        Assert.Equal(FireKind.Start, fire.Kind);
        Assert.Equal(FireRule.LastSong, fire.Rule);
        Assert.Single(Fires(h));
    }

    [Fact]
    public void QF04_Queue_first_after_a_near_end_fire_does_not_fire_again_on_the_last_song()
    {
        // Dry run (or a lost send): the songs never show in the list. Guard 9b still sees the sent songs.
        var h = AtInstance6();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        h.FeedAll(QueueFirstChange(S6, S8, [S10, Last]));
        h.Time.Advance(TimeSpan.FromMinutes(3));
        h.FeedAll(QueueFirstChange(S8, S10, [Last]));
        h.MarkFired(E1, E2);
        h.Run = RunOf(E1, E2);
        h.LastSessionFireAt = h.Time.GetUtcNow();

        h.Time.Advance(TimeSpan.FromMinutes(3));
        var change = QueueFirstChange(S10, Last, []).Select(h.Feed).ToList();

        AssertSkip(change[1], "queue_first");
        AssertSkip(change[2], "already_queued");
        Assert.Single(Fires(h));
    }

    [Fact]
    public void QF05_Queue_first_with_the_sent_songs_then_top_up_on_the_first_radio_song()
    {
        var h = AtInstance6();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        h.FeedAll(QueueFirstChange(S6, S8, [S10, Last]));
        h.Time.Advance(TimeSpan.FromMinutes(3));
        h.FeedAll(QueueFirstChange(S8, S10, [Last]));
        h.MarkFired(E1, E2);
        h.Run = RunOf(E1, E2);

        // The queue-first list is longer than the ending song's (the radio songs arrived): it is still queue-first.
        h.Time.Advance(TimeSpan.FromMinutes(3));
        var change = QueueFirstChange(S10, Last, [E1, E2]).Select(h.Feed).ToList();
        AssertSkip(change[1], "queue_first");
        AssertSkip(change[2], "not_last");

        h.Time.Advance(TimeSpan.FromMinutes(3));
        var topUp = QueueFirstChange(Last, E1, [E2]).Select(h.Feed).ToList();
        AssertSkip(topUp[1], "queue_first");
        var fire = Assert.IsType<TriggerDecision.Fire>(topUp[2].Decision);
        Assert.Equal(FireKind.TopUp, fire.Kind);
        Assert.Equal(2, Fires(h).Count);
    }

    [Fact]
    public void QF06_A_genuinely_stale_list_still_skips()
    {
        var h = AtInstance6();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        h.FeedAll(QueueFirstChange(S6, S8, [S10, Last]));

        // The user stops and starts another album; the server still holds the old album's list.
        h.Time.Advance(TimeSpan.FromMinutes(10));
        h.Feed(Idle(Finamp(S8, [S10, Last])));
        var outcome = h.Feed(Finamp(B1, [Last]));

        Assert.True(outcome.InstanceStarted);
        AssertSkip(outcome, "stale_queue");
        Assert.Empty(Fires(h));
    }

    [Fact]
    public void QF07_A_replay_of_the_same_song_with_an_unrelated_list_is_a_new_instance()
    {
        var h = AtInstance6();

        // The same song again after a stop, but the list is not the one after the next song.
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Feed(Idle(Finamp(S6, [S8, S10, Last])));
        var replay = h.Feed(Finamp(S6, [S8, S10, Last]));

        Assert.True(replay.InstanceStarted);
        AssertSkip(replay, "not_last");
    }

    [Fact]
    public void QF08_Web_is_unchanged_by_the_queue_first_rule()
    {
        // A web list repeating a song: same item, new entry. It must stay a new instance and fire by the Full rule.
        var list = new[] { S6, S6, Last };
        var h = new TriggerHarness();
        AssertSkip(h.Feed(Web(list, 0)), "not_last");
        h.Feed(Idle(Web(list, 0)));
        var outcome = h.Feed(Web(list, 1));

        Assert.True(outcome.InstanceStarted);
        var fire = Assert.IsType<TriggerDecision.Fire>(outcome.Decision);
        Assert.Equal(FireRule.NearEnd, fire.Rule);
    }
}
