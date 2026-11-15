using Encore.Jellyfin;
using Encore.Picks;
using Encore.Store;
using Encore.Trigger;
using Orchestrator = Encore.Orchestration.Orchestrator;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// The one-song buffer end to end (v01-design.md section 9.3, T28/T29): the radio starts at the second-to-last song,
/// tops up when one Encore song is left, and a send the next queue report does not show is re-sent once, only to a
/// playing session, never on a timeout alone.
/// </summary>
public class BufferTests
{
    private static readonly Guid A = Catalog.G(500), B = Catalog.G(501), C = Catalog.G(502);
    private static readonly Guid U1 = Catalog.G(510), U2 = Catalog.G(511);
    private static readonly Guid[] Album = [A, B, C];

    private static JellyfinRequestException Timeout() => new("PlayLast", "timed out", outcomeUnknown: true);

    private static async Task<(OrchestratorHarness H, Orchestrator O)> CreateAsync(Action<OrchestratorHarness>? setup = null)
    {
        var h = new OrchestratorHarness();
        setup?.Invoke(h);
        var orchestrator = h.CreateOrchestrator();
        await orchestrator.RefreshAllowListAsync();
        return (h, orchestrator);
    }

    /// <summary>Plays the web album from A to B (the second-to-last song, which fires). Returns the songs sent.</summary>
    private static async Task<Guid[]> PlayToSecondToLastAsync(OrchestratorHarness h, Orchestrator o)
    {
        await h.Send(o, Sessions.WebAt(Album, 0));
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(Album, 1));
        return h.Jellyfin.PlayLastAttempts.LastOrDefault().ItemIds ?? [];
    }

    private static Guid[] With(params Guid[][] parts) => [.. parts.SelectMany(p => p)];

    private static int AddedLastHour(OrchestratorHarness h, string session = Sessions.WebId) =>
        h.Store.CountAddedSince(session, h.Time.GetUtcNow() - TimeSpan.FromHours(1));

    [Fact]
    public async Task E01_Web_second_to_last_sends_one_playlast_with_two_ids()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;

        var sent = await PlayToSecondToLastAsync(h, o);

        var call = Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Equal(2, call.ItemIds.Length);
        Assert.Equal(sent, call.ItemIds);
        Assert.DoesNotContain(call.ItemIds, Album.Contains);
        var fired = Assert.Single(h.Log.Named("trigger.fired"));
        Assert.Equal("start", fired.Fields["Kind"]);
        Assert.Equal("near_end", fired.Fields["Rule"]);
    }

    [Fact]
    public async Task E02_Delivered_then_top_up_on_the_first_Encore_song()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        var sent = await PlayToSecondToLastAsync(h, o);
        var full = With(Album, sent);

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(full, 2));
        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Single(h.Jellyfin.PlayLasts);

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(full, 3));

        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        var second = h.Jellyfin.PlayLasts.Last().ItemIds;
        Assert.Equal(2, second.Length);
        Assert.Empty(second.Intersect(full));
        Assert.Equal("topup", h.Log.Named("trigger.fired").Last().Fields["Kind"]);
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
    }

    [Fact]
    public async Task E03_Silent_204_is_re_sent_once_at_the_next_song()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        var sent = await PlayToSecondToLastAsync(h, o);

        // The next song starts and the report does not hold the songs.
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(o, Sessions.WebAt(Album, 2));

        var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal("next_song", verdict.Fields["JudgedAt"]);
        Assert.Equal(ResendNames.Pending, verdict.Fields["Resend"]);
        Assert.Equal(false, verdict.Fields["WasResend"]);
        Assert.Single(h.Log.Named("queue.resent"));
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        Assert.Equal(sent, h.Jellyfin.PlayLasts.Last().ItemIds);
        Assert.Equal(1, h.Store.CountTriggers(EncoreStore.SentOutcome, "resend"));
        Assert.Equal(4, AddedLastHour(h));
        Assert.Equal(2, o.Status.Sent);
        Assert.Equal(4, o.Status.ItemsSent);
        Assert.Equal(1, o.Status.Unconfirmed);
        var fired = h.Log.Named("trigger.fired").Last();
        Assert.Equal("resend", fired.Fields["Kind"]);
        Assert.EndsWith("|resend|1", (string)fired.Fields["DedupeKey"]!, StringComparison.Ordinal);

        // More snapshots of the same song never send again.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Send(o, Sessions.WebAt(Album, 2));
        Assert.Equal(2, h.Jellyfin.PlayLastAttempts.Count);
    }

    [Fact]
    public async Task E04_Timeout_then_the_next_song_without_the_songs_re_sends_once()
    {
        var (h, o) = await CreateAsync(h => h.Jellyfin.PlayLastFailure = Timeout());
        using var _ = h;
        var sent = await PlayToSecondToLastAsync(h, o);
        Assert.Single(h.Jellyfin.PlayLastAttempts);

        // However long the same song plays, nothing is re-sent before a report arrives.
        for (var i = 0; i < 10; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await h.Send(o, Sessions.WebAt(Album, 1));
        }

        Assert.Single(h.Jellyfin.PlayLastAttempts);

        h.Jellyfin.PlayLastFailure = null;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Send(o, Sessions.WebAt(Album, 2));

        Assert.Equal(2, h.Jellyfin.PlayLastAttempts.Count);
        Assert.Equal(sent, Assert.Single(h.Jellyfin.PlayLasts).ItemIds);
        Assert.Single(h.Log.Named("queue.resent"));
    }

    [Fact]
    public async Task E05_A_lost_re_send_is_not_sent_again()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(o, Sessions.WebAt(Album, 2));
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);

        // The re-sent songs never show up either; the user goes back to B (second-to-last again).
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(Album, 1));

        var verdicts = h.Log.Named("queue.unconfirmed").ToList();
        Assert.Equal(2, verdicts.Count);
        Assert.Equal(ResendNames.AlreadyResent, verdicts[1].Fields["Resend"]);
        Assert.Equal(true, verdicts[1].Fields["WasResend"]);
        Assert.Equal(2, h.Jellyfin.PlayLastAttempts.Count);
        Assert.Contains(h.Log.Named("trigger.skipped_quiet"), e => e.Fields["Reason"] as string == "already_queued");
        Assert.Equal(2, o.Status.Unconfirmed);
    }

    [Fact]
    public async Task E06_A_non_empty_stop_without_the_songs_arms_a_re_send_for_the_next_song()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        var sent = await PlayToSecondToLastAsync(h, o);

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebStopped(Album));
        var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal("stopped", verdict.Fields["JudgedAt"]);
        Assert.Equal(ResendNames.Armed, verdict.Fields["Resend"]);
        Assert.Single(h.Jellyfin.PlayLastAttempts);

        // A new song within the grace takes the re-send; the verdict is not logged twice.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Send(o, Sessions.WebAt(Album, 2));

        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        Assert.Equal(sent, h.Jellyfin.PlayLasts.Last().ItemIds);
        Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal(1, o.Status.Unconfirmed);
    }

    [Fact]
    public async Task E06b_A_non_empty_stop_with_no_new_song_in_the_grace_never_sends()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebStopped(Album));
        h.Time.Advance(TimeSpan.FromSeconds(11));
        await h.Send(o, Sessions.WebStopped(Album));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Send(o, Sessions.WebAt(Album, 2));

        Assert.Single(h.Jellyfin.PlayLastAttempts);
        Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Empty(h.Log.Named("queue.resent"));
    }

    [Fact]
    public async Task E07_An_empty_stop_report_past_the_grace_is_unconfirmed_and_never_sent()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebStopped([]));
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
        h.Time.Advance(TimeSpan.FromSeconds(11));
        await h.Send(o, Sessions.WebStopped([]));

        var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal("stopped", verdict.Fields["JudgedAt"]);
        Assert.Equal(ResendNames.SkippedNotPlaying, verdict.Fields["Resend"]);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Send(o, Sessions.WebAt(Album, 2));
        Assert.Single(h.Jellyfin.PlayLastAttempts);
    }

    [Fact]
    public async Task E08_A_report_one_second_after_the_send_defers_the_verdict()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        var sent = await PlayToSecondToLastAsync(h, o);

        // B was almost over: C starts 1 s after the send, before the client applied the songs.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Send(o, Sessions.WebAt(Album, 2));

        Assert.Single(h.Log.Named("queue.verdict_deferred"));
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
        Assert.Contains(h.Log.Named("trigger.skipped_quiet"), e => e.Fields["Reason"] as string == "already_queued");
        Assert.Single(h.Jellyfin.PlayLastAttempts);

        // At the following change the songs show up: confirmed, and the top-up goes out as normal.
        var full = With(Album, sent);
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(full, 3));

        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        Assert.Equal("topup", h.Log.Named("trigger.fired").Last().Fields["Kind"]);
    }

    [Fact]
    public async Task E09_Dry_run_logs_one_would_add_per_album_end()
    {
        var (h, o) = await CreateAsync(h => h.Options = h.Options with { DryRun = true });
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(Album, 2));

        Assert.Empty(h.Jellyfin.PlayLastAttempts);
        Assert.Single(h.Log.Named("queue.would_add"));
        Assert.Contains(h.Log.Named("trigger.skipped_quiet"), e => e.Fields["Reason"] as string == "already_queued");
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
    }

    [Fact]
    public async Task E10_No_re_send_while_the_user_has_two_songs_after_the_missing_ones()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);

        // The user queued U1 and U2; Encore's songs never arrived.
        var withUser = With(Album, [U1, U2]);
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(o, Sessions.WebAt(withUser, 2));

        var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
        Assert.Equal(ResendNames.SkippedUserSongs, verdict.Fields["Resend"]);
        Assert.Single(h.Jellyfin.PlayLastAttempts);

        // Down to one of the user's songs: a new start.
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(withUser, 3));

        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        Assert.Equal("start", h.Log.Named("trigger.fired").Last().Fields["Kind"]);
        Assert.Empty(h.Log.Named("queue.resent"));
    }

    [Fact]
    public async Task E11_Re_sent_songs_count_against_the_hourly_limit_and_the_run()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(o, Sessions.WebAt(Album, 2));

        Assert.Equal(4, AddedLastHour(h));
        var run = h.Store.LatestRun(Sessions.WebId, h.Time.GetUtcNow() - TimeSpan.FromHours(6));
        Assert.NotNull(run);
        Assert.Equal(4, run.AddedCount);
    }

    [Fact]
    public async Task E11b_A_re_send_that_would_pass_30_an_hour_is_rate_limited()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;

        // 26 songs already went to this session in the last hour; the start takes it to 28, a re-send would make 30+2.
        var earlier = Enumerable.Range(0, 26)
            .Select(i => new PickedItem(Catalog.G(9000 + i), $"Earlier {i}", null, null, PickSource.SimilarArtist))
            .ToList();
        h.Store.RecordFire(
            new TriggerRecord(h.Time.GetUtcNow() - TimeSpan.FromMinutes(30), Sessions.WebId, Catalog.User, "Jellyfin Web", "full", "start",
                Catalog.G(9999), "playlistItem0", "earlier", EncoreStore.SentOutcome, "earlier-run", 1),
            earlier);
        h.Store.RecordFire(
            new TriggerRecord(h.Time.GetUtcNow() - TimeSpan.FromMinutes(20), "other-session", Catalog.User, "Jellyfin Web", "full", "start",
                Catalog.G(9998), "playlistItem0", "other", EncoreStore.SentOutcome, "other-run", 1),
            [new PickedItem(Catalog.G(9100), "Other", null, null, PickSource.SimilarArtist)]);

        await PlayToSecondToLastAsync(h, o);
        Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Equal(28, AddedLastHour(h));

        // 28 + 2 = 30 is still allowed, so add one more song elsewhere on this session's count to reach 29.
        h.Store.RecordFire(
            new TriggerRecord(h.Time.GetUtcNow(), Sessions.WebId, Catalog.User, "Jellyfin Web", "full", "start",
                Catalog.G(9997), "playlistItem0", "extra", EncoreStore.SentOutcome, "extra-run", 1),
            [new PickedItem(Catalog.G(9101), "Extra", null, null, PickSource.SimilarArtist)]);

        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(o, Sessions.WebAt(Album, 2));

        Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Contains(h.Log.Named("trigger.skipped"), e => e.Fields["Reason"] as string == "rate_limited");
        Assert.Single(h.Log.Named("queue.unconfirmed"));
    }

    [Fact]
    public async Task E13_A_capped_run_restored_from_the_store_does_not_block_a_fresh_album()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;

        // An earlier run on this session reached the cap 3 hours ago (outside the hourly limits, inside the 6 hour restore window).
        var earlier = Enumerable.Range(0, TriggerLimits.RunCap)
            .Select(i => new PickedItem(Catalog.G(8000 + i), $"Earlier {i}", null, null, PickSource.SimilarArtist))
            .ToList();
        h.Store.RecordFire(
            new TriggerRecord(h.Time.GetUtcNow() - TimeSpan.FromHours(3), Sessions.WebId, Catalog.User, "Jellyfin Web", "full", "start",
                Catalog.G(9999), "playlistItem0", "earlier", EncoreStore.SentOutcome, "capped-run", 1),
            earlier);
        var stored = h.Store.LatestRun(Sessions.WebId, h.Time.GetUtcNow() - TimeSpan.FromHours(6));
        Assert.Equal(TriggerLimits.RunCap, stored!.AddedCount);

        var sent = await PlayToSecondToLastAsync(h, o);

        Assert.Equal(2, sent.Length);
        Assert.DoesNotContain(h.Log.Named("trigger.skipped"), e => e.Fields["Reason"] as string == "run_cap");
        var fired = Assert.Single(h.Log.Named("trigger.fired"));
        Assert.Equal("start", fired.Fields["Kind"]);
        Assert.NotEqual("capped-run", fired.Fields["RunId"]);
        Assert.Single(h.Jellyfin.PlayLasts);
    }

    [Fact]
    public async Task E12_Restart_between_the_send_and_the_next_song_uses_the_fallback()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);

        h.RestartStore();
        var restarted = h.CreateOrchestrator();
        await restarted.RefreshAllowListAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(restarted, Sessions.WebAt(Album, 2));

        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        var fired = h.Log.Named("trigger.fired").Last();
        Assert.Equal("start", fired.Fields["Kind"]);
        Assert.Equal("last_song", fired.Fields["Rule"]);
        Assert.Empty(h.Log.Named("queue.resent"));
    }

    [Fact]
    public async Task E12b_Restart_between_the_send_and_the_next_song_with_the_songs_sends_nothing()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        var sent = await PlayToSecondToLastAsync(h, o);

        h.RestartStore();
        var restarted = h.CreateOrchestrator();
        await restarted.RefreshAllowListAsync();
        h.Time.Advance(TimeSpan.FromSeconds(60));
        await h.Send(restarted, Sessions.WebAt(With(Album, sent), 2));

        Assert.Single(h.Jellyfin.PlayLasts);
    }

    [Fact]
    public async Task E13_Restart_in_the_middle_of_the_second_to_last_song_does_not_send_again()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        await PlayToSecondToLastAsync(h, o);

        h.RestartStore();
        h.Time.Advance(TimeSpan.FromMinutes(1));
        var restarted = h.CreateOrchestrator();
        await restarted.RefreshAllowListAsync();
        await h.Send(restarted, Sessions.WebAt(Album, 1));

        Assert.Single(h.Jellyfin.PlayLasts);
        Assert.Contains(h.Log.Named("trigger.skipped"), e => e.Fields["Reason"] as string == "duplicate");
    }

    [Fact]
    public async Task E14_Finamp_flow_end_to_end()
    {
        var (h, o) = await CreateAsync();
        using var _ = h;
        Guid N2 = Catalog.G(520), N1 = Catalog.G(521), N = Catalog.G(522);

        await h.Send(o, Sessions.Finamp(N2, N1, N));
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.FinampStopped(N));
        await h.Send(o, Sessions.Finamp(N1, N));
        var first = Assert.Single(h.Jellyfin.PlayLasts).ItemIds;
        Assert.Equal("start", h.Log.Named("trigger.fired").Last().Fields["Kind"]);

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.FinampStopped(first));
        await h.Send(o, Sessions.Finamp(N, first));
        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Single(h.Jellyfin.PlayLasts);

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.FinampStopped(first[1]));
        await h.Send(o, Sessions.Finamp(first[0], first[1]));
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        var second = h.Jellyfin.PlayLasts.Last().ItemIds;
        Assert.Equal("topup", h.Log.Named("trigger.fired").Last().Fields["Kind"]);
        Assert.Empty(second.Intersect(first));

        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.FinampStopped(second));
        await h.Send(o, Sessions.Finamp(first[1], second));

        Assert.Equal(2, h.Log.Named("queue.confirmed").Count());
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
    }

    [Fact]
    public async Task E15_Finamp_queue_first_reports_end_to_end()
    {
        // T75: Finamp sends each change as the stop, the OLD song with the NEW list, then the new song (live 2026-11-15).
        var (h, o) = await CreateAsync();
        using var _ = h;
        Guid N3 = Catalog.G(530), N2 = Catalog.G(531), N1 = Catalog.G(532), N = Catalog.G(533);

        async Task Change(Guid old, Guid next, params Guid[] upcoming)
        {
            h.Time.Advance(TimeSpan.FromSeconds(200));
            await h.Send(o, Sessions.FinampStopped(upcoming));
            await h.Send(o, Sessions.Finamp(old, upcoming));
            await h.Send(o, Sessions.Finamp(next, upcoming));
        }

        await h.Send(o, Sessions.Finamp(N3, N2, N1, N));
        await Change(N3, N2, N1, N);
        Assert.Empty(h.Jellyfin.PlayLasts);
        await Change(N2, N1, N);
        var first = Assert.Single(h.Jellyfin.PlayLasts).ItemIds;
        var fired = Assert.Single(h.Log.Named("trigger.fired"));
        Assert.Equal("start", fired.Fields["Kind"]);
        Assert.Equal("near_end", fired.Fields["Rule"]);

        await Change(N1, N, first);
        Assert.Single(h.Log.Named("queue.confirmed"));
        Assert.Single(h.Jellyfin.PlayLasts);

        await Change(N, first[0], first[1]);
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        Assert.Equal("topup", h.Log.Named("trigger.fired").Last().Fields["Kind"]);

        Assert.DoesNotContain(h.Log.Named("trigger.skipped"), e => e.Fields["Reason"] as string == "stale_queue");
        Assert.Contains(h.Log.Named("trigger.skipped_quiet"), e => e.Fields["Reason"] as string == "queue_first");
        Assert.Empty(h.Log.Named("queue.unconfirmed"));
    }
}
