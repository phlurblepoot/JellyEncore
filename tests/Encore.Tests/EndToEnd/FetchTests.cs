using Encore.Config;
using Encore.Fetch;
using Encore.Jellyfin;
using Encore.Orchestration;
using Encore.Store;
using Encore.Tests.Fetch;
using Encore.Tests.Picks;
using Encore.Tests.Watch;
using Encore.Trigger;
using Orchestrator = Encore.Orchestration.Orchestrator;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// Fetching end to end (v0.2 design section 15, T51 FE01-FE09, plus PL18, PL19 and session_gone): the orchestrator with
/// the real pool, candidate source, owned index, coordinator, worker, Soulseek fetcher and Jellyfin ingest, over a fake
/// Jellyfin, slskd, Deezer and ListenBrainz and a fast fake clock on the fetch side.
/// </summary>
public class FetchTests
{
    private static readonly HashSet<Guid> CatalogSongs = [.. Catalog.Build().Picks.SongsByArtist.Values.SelectMany(s => s).Select(s => s.Id)];

    private static async Task<(OrchestratorHarness H, FetchWorld W, Orchestrator O, Listener L)> CreateAsync(
        FetchMode mode, Action<FetchWorld>? setup = null, double scale = 100)
    {
        var h = new OrchestratorHarness();
        var w = new FetchWorld(h, mode, scale);
        setup?.Invoke(w);
        await w.StartAsync();
        var o = h.CreateOrchestrator();
        await o.RefreshAllowListAsync();
        return (h, w, o, new Listener(h, o));
    }

    private static async Task DisposeAsync(OrchestratorHarness h, FetchWorld w)
    {
        await w.DisposeAsync();
        h.Dispose();
    }

    /// <summary>The v0.1 sends for the same catalog: a plain harness with no fetch side at all.</summary>
    private static async Task<(Guid[] Start, Guid[] TopUp)> BaselineAsync()
    {
        using var h = new OrchestratorHarness();
        var o = h.CreateOrchestrator();
        await o.RefreshAllowListAsync();
        var l = new Listener(h, o);
        var start = await l.StartAsync();
        return (start, await l.TopUpAsync());
    }

    private static TimeSpan FireBound => new OrchestratorTiming().PickTimeout + new JellyfinClientOptions().Timeout;

    [Fact]
    public async Task FE01_An_unowned_pick_is_fetched_ingested_and_goes_out_in_the_first_top_up()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.Radio(w.Track("Radio B", "New Song"));
            w.OfferGood("Radio B", "New Song");
        });
        try
        {
            var start = await l.StartAsync();
            Assert.All(start, id => Assert.Contains(id, CatalogSongs));
            var request = w.Fetch.For("New Song");
            Assert.Equal(Sessions.WebId, request.SessionId);

            await w.UntilReadyAsync("New Song");
            var fetched = Assert.IsType<FetchReadiness.Ready>(w.Readiness("New Song")).ItemId;
            Assert.Equal(w.FetchedId("Radio B", "New Song"), fetched);
            Assert.Contains(FetchWorld.JellyfinPathOf("Radio B", "New Song"), h.Jellyfin.MediaUpdatedPaths);

            var topUp = await l.TopUpAsync();

            Assert.Equal(fetched, topUp[0]);
            Assert.Equal(2, topUp.Length);
            Assert.Equal(["ready"], w.Query("SELECT state FROM fetched_files"));
            Assert.Equal([fetched.ToString("N")], w.Query("SELECT jellyfin_item_id FROM fetched_files"));
            Assert.Equal(["deezer_radio+soulseek"], w.Query($"SELECT source FROM added_items WHERE item_id = '{fetched:N}'"));
            Assert.StartsWith("deezer_radio+soulseek,", w.Query("SELECT sources FROM triggers WHERE outcome = 'sent' ORDER BY id").Last());
            Assert.Single(w.Slskd.Enqueued);
            Assert.Equal(0, o.Status.Failed);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE02_A_candidate_the_server_owns_is_queued_as_the_server_copy_with_no_slskd_call()
    {
        Guid owned = default;
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            owned = w.OwnSong(80_001, "Radio A", "Owned Hit");
            w.Radio(w.Track("Radio A", "Owned Hit"));
        });
        try
        {
            var start = await l.StartAsync();
            await l.TopUpAsync();

            Assert.Equal(owned, start[0]);
            Assert.Empty(w.Fetch.Requests);
            Assert.Empty(w.Slskd.Started);
            Assert.Empty(w.Slskd.Enqueued);
            Assert.Equal(0, w.Slskd.GetSearchCalls);
            Assert.Empty(w.Query("SELECT id FROM fetched_files"));
            Assert.StartsWith("deezer_radio+server,", w.Query("SELECT sources FROM triggers WHERE outcome = 'sent' ORDER BY id").First());
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE03_A_fetch_not_ready_by_its_second_hand_out_is_skipped_and_every_hand_out_is_full_without_a_stall()
    {
        // Jellyfin never lists the file, so the job waits in ingest (3 min, 18 s real at this scale) through every fire.
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.H.Jellyfin.IngestDelay = null;
            w.Radio(w.Track("Radio B", "Slow Song"));
            w.OfferGood("Radio B", "Slow Song");
        }, scale: 10);
        try
        {
            var start = await l.StartAsync();
            var pick = w.Fetch.For("Slow Song").PickId;

            var first = await l.TopUpAsync();
            Assert.Equal(1, o.Status.PassedOver);
            Assert.Equal(0, o.Status.Skipped);

            var second = await l.TopUpAsync();
            Assert.Equal(1, o.Status.Skipped);
            Assert.Equal((pick, "late"), Assert.Single(w.Fetch.Abandoned));

            Assert.All(new[] { start, first, second }, batch => Assert.Equal(2, batch.Length));
            Assert.All(new[] { start, first, second }.SelectMany(b => b), id => Assert.Contains(id, CatalogSongs));
            Assert.IsType<FetchReadiness.Pending>(w.Coordinator.GetReadiness(pick));
            Assert.All(l.Durations, d => Assert.True(d < TimeSpan.FromSeconds(5), $"a snapshot took {d}"));
            Assert.True(l.HeartbeatFresh);
            Assert.Equal(0, o.Status.Failed);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE04_No_acceptable_copy_is_skipped_and_the_negative_cache_keeps_it_out_of_the_next_plan()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.Radio(w.Track("Radio B", "Rare Song"));
            w.Offer("Radio B", "Rare Song", ("carol", 2_000_000, FetchWorld.M4a("Radio B", "Rare Song")));
        });
        try
        {
            await l.StartAsync();
            await w.UntilFailedAsync("Rare Song");
            Assert.Equal(FetchFailure.NoAcceptable, ((FetchReadiness.Failed)w.Readiness("Rare Song")).Reason);
            Assert.Empty(w.Slskd.Enqueued);
            var key = w.Fetch.For("Rare Song").Candidate.Key.Value;
            Assert.True(w.Store.IsNegative(key, w.Clock.GetUtcNow()));

            var topUp = await l.TopUpAsync();
            Assert.Equal(2, topUp.Length);
            Assert.Equal(1, o.Status.Skipped);
            Assert.Equal(0, o.Status.PassedOver);

            // The user starts another album: a new run plans again, and the song is not asked for again.
            var planned = h.Log.Named("pool.planned").Count();
            Guid[] other = [Catalog.G(600), Catalog.G(601), Catalog.G(602)];
            h.Time.Advance(TimeSpan.FromSeconds(200));
            await h.Send(o, Sessions.WebAt(other, 0));
            h.Time.Advance(TimeSpan.FromSeconds(200));
            await h.Send(o, Sessions.WebAt(other, 1));

            Assert.Equal(2, h.Log.Named("trigger.fired").Count(e => (string?)e.Fields["Kind"] == "start"));
            Assert.True(h.Log.Named("pool.planned").Count() > planned);
            Assert.Single(w.Fetch.Requests);
            Assert.DoesNotContain(h.Jellyfin.PlayLasts.SelectMany(p => p.ItemIds), id => !CatalogSongs.Contains(id));
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE05_Mode_off_makes_no_Deezer_ListenBrainz_or_slskd_call_and_sends_the_v01_songs()
    {
        var baseline = await BaselineAsync();
        var (h, w, o, l) = await CreateAsync(FetchMode.Off, w =>
        {
            w.OwnSong(80_001, "Radio A", "Owned Hit");
            w.Radio(w.Track("Radio B", "New Song"), w.Track("Radio A", "Owned Hit"));
            w.OfferGood("Radio B", "New Song");
        });
        try
        {
            var start = await l.StartAsync();
            var topUp = await l.TopUpAsync();

            Assert.Equal(baseline.Start, start);
            Assert.Equal(baseline.TopUp, topUp);
            Assert.Empty(w.Deezer.Calls);
            Assert.Empty(w.ListenBrainz.Calls);
            Assert.Empty(w.Slskd.Started);
            Assert.Empty(w.Slskd.Enqueued);
            Assert.Empty(w.Fetch.Requests);
            Assert.Empty(h.Log.Named("pool.planned"));
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE06_Dry_run_searches_slskd_but_never_downloads_and_sends_what_mode_off_sends()
    {
        var baseline = await BaselineAsync();
        var (h, w, o, l) = await CreateAsync(FetchMode.DryRun, w =>
        {
            w.Radio(w.Track("Radio B", "New Song"));
            w.OfferGood("Radio B", "New Song");
        });
        try
        {
            var start = await l.StartAsync();
            await Wait.Until(() => w.Coordinator.Status.WouldDownload == 1, "the dry-run search scored a copy", 15_000);
            var topUp = await l.TopUpAsync();

            Assert.Equal(baseline.Start, start);
            Assert.Equal(baseline.TopUp, topUp);
            Assert.True(Assert.Single(w.Fetch.Requests).DryRun);
            Assert.NotEmpty(w.Slskd.Started);
            Assert.Empty(w.Slskd.Enqueued);
            Assert.Empty(w.Query("SELECT id FROM fetched_files"));
            Assert.Empty(h.Jellyfin.MediaUpdatedPaths);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE07_A_stalled_first_peer_gives_way_to_the_backup_peer_and_the_song_is_sent()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.Radio(w.Track("Radio B", "New Song"));
            w.Offer("Radio B", "New Song",
                ("fastbutstuck", 5_000_000, FetchWorld.Mp3("Radio B", "New Song")),
                ("steady", 1_000_000, FetchWorld.Mp3("Radio B", "New Song")));
            w.Slskd.Peers["fastbutstuck"] = FakeSlskd.QueuedRemotely();
            w.Slskd.Peers["steady"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(6), 8_000_000);
        });
        try
        {
            await l.StartAsync();
            await w.UntilReadyAsync("New Song");
            var fetched = ((FetchReadiness.Ready)w.Readiness("New Song")).ItemId;

            var topUp = await l.TopUpAsync();

            Assert.Equal(fetched, topUp[0]);
            Assert.Equal(["fastbutstuck", "steady"], w.Slskd.Enqueued.Select(e => e.Username));
            Assert.Contains(w.Slskd.CancelledTransfers, t => t.User == "fastbutstuck");
            Assert.Equal(["steady"], w.Query("SELECT peer FROM fetched_files WHERE state = 'ready'"));
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE08_The_delivery_check_confirms_a_fetched_id()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.Radio(w.Track("Radio B", "New Song"));
            w.OfferGood("Radio B", "New Song");
        });
        try
        {
            await l.StartAsync();
            await w.UntilReadyAsync("New Song");
            var topUp = await l.TopUpAsync();
            Assert.Contains(((FetchReadiness.Ready)w.Readiness("New Song")).ItemId, topUp);

            // The fetched song starts and the client's queue holds it.
            await l.PlayAsync(l.Index + 1);

            Assert.Equal(2, h.Log.Named("queue.confirmed").Count());
            Assert.Empty(h.Log.Named("queue.unconfirmed"));
            Assert.Empty(h.Log.Named("queue.resent"));
            Assert.Equal(0, o.Status.Unconfirmed);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE08_PL19_A_lost_send_holding_a_fetched_id_is_re_sent_once_like_any_other()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.Radio(w.Track("Radio B", "New Song"));
            w.OfferGood("Radio B", "New Song");
        });
        try
        {
            await l.StartAsync();
            await w.UntilReadyAsync("New Song");
            var fetched = ((FetchReadiness.Ready)w.Readiness("New Song")).ItemId;
            var topUp = await l.TopUpAsync();
            Assert.Equal(fetched, topUp[0]);

            // The next song starts, but the client's queue does not hold the top-up.
            var withoutTopUp = l.Queue.Take(l.Queue.Count - topUp.Length).ToList();
            await l.PlayAsync(l.Index + 1, withoutTopUp);

            var verdict = Assert.Single(h.Log.Named("queue.unconfirmed"));
            Assert.Equal(ResendNames.Pending, verdict.Fields["Resend"]);
            Assert.Single(h.Log.Named("queue.resent"));
            Assert.Equal(topUp, h.Jellyfin.PlayLasts.Last().ItemIds);
            Assert.Equal(1, h.Store.CountTriggers(EncoreStore.SentOutcome, "resend"));
            Assert.StartsWith("deezer_radio+soulseek,",
                w.Query("SELECT sources FROM triggers WHERE kind = 'resend'").Single());

            // More snapshots of the same song never send it a third time.
            var attempts = h.Jellyfin.PlayLastAttempts.Count;
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await h.Send(o, Sessions.WebAt([.. withoutTopUp], l.Index));
            Assert.Equal(attempts, h.Jellyfin.PlayLastAttempts.Count);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE09_With_two_slow_fetches_running_every_fire_ends_in_time_and_the_heartbeat_stays_fresh()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.H.Jellyfin.IngestDelay = null;
            w.Radio(w.Track("Radio C", "Slow One"), w.Track("Radio D", "Slow Two"));
            w.OfferGood("Radio C", "Slow One", "carol");
            w.OfferGood("Radio D", "Slow Two", "dave");
        }, scale: 10);
        try
        {
            await l.StartAsync();
            Assert.Equal(2, w.Fetch.Requests.Count);
            await Wait.Until(() => w.Coordinator.Status.InFlight + w.Coordinator.Status.Ingesting == 2, "both fetches running");

            await l.TopUpAsync();
            await l.TopUpAsync();

            Assert.Equal(3, h.Jellyfin.PlayLasts.Count);
            Assert.All(w.Fetch.Requests, r => Assert.IsType<FetchReadiness.Pending>(w.Coordinator.GetReadiness(r.PickId)));
            Assert.All(l.Durations, d => Assert.True(d < FireBound, $"a snapshot took {d}, over {FireBound}"));
            Assert.All(l.Durations, d => Assert.True(d < TimeSpan.FromSeconds(5), $"a snapshot took {d}"));
            Assert.True(l.HeartbeatFresh);
            Assert.Equal(0, o.Status.Failed);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task PL18_A_seed_artist_Deezer_does_not_know_plans_from_the_owned_chain_without_an_error()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w => w.Deezer.Artists.Clear());
        try
        {
            var start = await l.StartAsync();
            var topUp = await l.TopUpAsync();

            Assert.Equal(2, start.Length);
            Assert.Equal(2, topUp.Length);
            Assert.All(start.Concat(topUp), id => Assert.Contains(id, CatalogSongs));
            Assert.Contains(w.Deezer.Calls, c => c.StartsWith("find:", StringComparison.Ordinal));
            Assert.Empty(w.Fetch.Requests);
            Assert.Empty(w.Slskd.Started);
            Assert.Empty(h.Log.Named("pool.external_failed"));
            Assert.Empty(h.Log.Named("pool.external_timeout"));
            Assert.Empty(h.Log.Named("queue.add_failed"));
            Assert.NotEmpty(h.Log.Named("pool.planned"));
            Assert.Equal(0, o.Status.Failed);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task PL20_The_run_cap_counts_fetched_and_server_copies_alike_and_ends_the_run_once()
    {
        // The pool fakes: every unowned pick is ready by the next hand-out, so a run of 60 songs plays quickly.
        using var h = new OrchestratorHarness();
        h.Options = h.Options with { FetchMode = FetchMode.Live };
        var pool = new PoolWorld();
        pool.Fetch.Accept = r =>
        {
            pool.Fetch.Readiness[r.PickId] = new FetchReadiness.Ready(Guid.NewGuid());
            return true;
        };
        pool.Candidates.List = [.. Enumerable.Range(0, 80).Select(i => i % 2 == 0
            ? pool.ExtOwned($"Owned {i}", $"Band {i}")
            : PoolWorld.ExtNew($"New {i}", $"Band {i}"))];
        h.Fetch = pool.Fetch;
        h.Candidates = pool.Candidates;
        h.Owned = pool.Owned;
        var o = h.CreateOrchestrator();
        await o.RefreshAllowListAsync();
        var l = new Listener(h, o);

        await l.StartAsync();
        var runId = (string)h.Log.Named("trigger.fired").First().Fields["RunId"]!;
        for (var i = 2; i < l.Queue.Count; i++)
        {
            await l.PlayAsync(i);
        }

        Assert.Equal(TriggerLimits.RunCap, h.Jellyfin.PlayLasts.Sum(p => p.ItemIds.Length));
        var sources = Rows(h, "SELECT source FROM added_items");
        Assert.Equal(TriggerLimits.RunCap, sources.Count);
        Assert.Contains(sources, s => s.EndsWith("+soulseek", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.EndsWith("+server", StringComparison.Ordinal));
        Assert.Contains(h.Log.Named("trigger.skipped"), e => (string?)e.Fields["Reason"] == "run_cap");
        Assert.Equal((runId, "run_cap"), Assert.Single(pool.Fetch.EndedRuns));
        Assert.Equal(0, o.Status.Failed);

        static List<string> Rows(OrchestratorHarness h, string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={h.DbPath};Pooling=False");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(reader.GetString(0));
            }

            return rows;
        }
    }

    [Fact]
    public async Task A_session_unseen_for_30_minutes_ends_its_run_on_the_fetch_side_once()
    {
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.H.Jellyfin.IngestDelay = null;
            w.Radio(w.Track("Radio B", "New Song"));
            w.OfferGood("Radio B", "New Song");
        }, scale: 10);
        try
        {
            await l.StartAsync();
            var runId = Assert.Single(w.Fetch.Requests).RunId;
            Assert.Empty(w.Fetch.EndedRuns);

            await o.StartAsync(CancellationToken.None);
            try
            {
                h.Time.Advance(new OrchestratorTiming().SessionForgetAfter + TimeSpan.FromMinutes(1));
                await Wait.Until(() => !w.Fetch.EndedRuns.IsEmpty, "the run ended");
                await Task.Delay(300);  // several more housekeeping ticks
            }
            finally
            {
                await o.StopAsync(CancellationToken.None);
            }

            Assert.Equal((runId, "session_gone"), Assert.Single(w.Fetch.EndedRuns));
            var ended = Assert.Single(w.CoordinatorLog.Named("run.ended"));
            Assert.Equal("session_gone", ended.Fields["Reason"]);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }
}
