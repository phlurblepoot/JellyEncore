using Encore.Trigger;
using static Encore.Tests.Trigger.TriggerHarness;

namespace Encore.Tests.Trigger;

/// <summary>
/// T77: Finamp plays a PlayLast batch in reverse order (live 2026-11-15 14:26-14:35Z: sent [A,B], plays B then A).
/// The fire kind and the last-song fallback must not depend on the order the run's songs show in the queue.
/// </summary>
public sealed class TriggerReverseOrderTests
{
    private static readonly Guid S8 = Item("S8"), S10 = Item("S10"), Last = Item("last");
    private static readonly Guid A = Item("A"), B = Item("B"), C = Item("C"), D = Item("D"), U = Item("U");

    /// <summary>A Finamp song change in the live order: the stop, the old song with the new list, then the new song.</summary>
    private static TriggerSession[] Change(Guid old, Guid next, params Guid[] upcoming) =>
        [Idle(Finamp(old, upcoming)), Finamp(old, upcoming), Finamp(next, upcoming)];

    private static TriggerDecision.Fire AssertFire(TriggerOutcome outcome, FireKind kind, FireRule rule)
    {
        var fire = Assert.IsType<TriggerDecision.Fire>(outcome.Decision);
        Assert.Equal(kind, fire.Kind);
        Assert.Equal(rule, fire.Rule);
        return fire;
    }

    private static void AssertSkip(TriggerOutcome outcome, string wire)
    {
        var skip = Assert.IsType<TriggerDecision.Skip>(outcome.Decision);
        Assert.Equal(wire, skip.Reason.ToWire());
    }

    /// <summary>Plays the album to its second-to-last song, which starts the run with [A,B].</summary>
    private static TriggerHarness Started()
    {
        var h = new TriggerHarness();
        h.Feed(Finamp(S8, [S10, Last]));
        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertFire(h.FeedAll(Change(S8, S10, Last)), FireKind.Start, FireRule.NearEnd);
        h.MarkFired(A, B);
        h.Run = RunOf(A, B);
        h.LastSessionFireAt = h.Time.GetUtcNow();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertSkip(h.FeedAll(Change(S10, Last, B, A)), "not_last");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        return h;
    }

    [Fact]
    public void RO01_Reverse_batch_near_end_is_a_top_up()
    {
        var h = Started();

        AssertFire(h.FeedAll(Change(Last, B, A)), FireKind.TopUp, FireRule.NearEnd);
    }

    [Fact]
    public void RO02_Second_reverse_batch_is_a_top_up_too()
    {
        var h = Started();
        AssertFire(h.FeedAll(Change(Last, B, A)), FireKind.TopUp, FireRule.NearEnd);
        h.MarkFired(C, D);
        h.Run = RunOf(A, B, C, D);
        h.LastSessionFireAt = h.Time.GetUtcNow();

        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertSkip(h.FeedAll(Change(B, A, D, C)), "not_last");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        AssertFire(h.FeedAll(Change(A, D, C)), FireKind.TopUp, FireRule.NearEnd);
    }

    [Fact]
    public void RO03_Run_cap_still_stops_a_reverse_order_run()
    {
        var h = Started();
        h.Run = new RunInfo(B, TriggerLimits.RunCap, [A, B]);

        AssertSkip(h.FeedAll(Change(Last, B, A)), "run_cap");
    }

    [Fact]
    public void RO04_A_user_song_after_the_radio_songs_makes_a_start()
    {
        var h = Started();
        // The user queued U after the reversed batch: at B two songs are left, at A only U.
        AssertSkip(h.FeedAll(Change(Last, B, A, U)), "not_last");
        h.Time.Advance(TimeSpan.FromMinutes(3));

        AssertFire(h.FeedAll(Change(B, A, U)), FireKind.Start, FireRule.NearEnd);
    }

    [Fact]
    public void RO05_A_user_song_ahead_of_a_radio_song_still_makes_a_start_when_it_is_last()
    {
        // The user's own song is the one still to come after a radio song: a start, even though the current song is the run's.
        var h = Started();
        AssertFire(h.FeedAll(Change(Last, B, U)), FireKind.Start, FireRule.NearEnd);
    }

    [Fact]
    public void RO06_Last_song_fallback_fires_a_top_up_on_the_first_song_sent()
    {
        // Fallback (b): Encore restarted at B (no history) and Finamp reported an empty list there, so neither the
        // near-end nor fallback (a) can fire at A, the first song sent, which plays last.
        var h = new TriggerHarness { Run = RunOf(A, B) };
        AssertSkip(h.Feed(Finamp(B, [])), "no_history");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        h.Feed(Idle(Finamp(A, [])));

        AssertFire(h.Feed(Finamp(A, [])), FireKind.TopUp, FireRule.LastSong);
    }

    [Fact]
    public void RO07_Web_queue_with_the_batch_reversed_is_a_top_up()
    {
        var h = new TriggerHarness { Run = RunOf(A, B) };

        var fire = AssertFire(h.Feed(Web([S10, Last, B, A], 2)), FireKind.TopUp, FireRule.NearEnd);
        Assert.Equal(QueueModel.Full, fire.Model);
    }

    [Fact]
    public void RO08_Web_user_song_after_the_radio_songs_makes_a_start()
    {
        var h = new TriggerHarness { Run = RunOf(A, B) };

        AssertFire(h.Feed(Web([S10, Last, A, B, U], 3)), FireKind.Start, FireRule.NearEnd);
    }
}
