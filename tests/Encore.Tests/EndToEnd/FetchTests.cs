using Encore.Config;
using Encore.Discovery;
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
        FetchMode mode, Action<FetchWorld>? setup = null, double scale = 100, bool waitAvailable = true)
    {
        var h = new OrchestratorHarness();
        var w = new FetchWorld(h, mode, scale);
        setup?.Invoke(w);
        await w.StartAsync(waitAvailable);
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
            // Past searching, as the comment above says (a skip during the search would cancel the job, not leave it pending).
            await Wait.Until(() => w.Slskd.Enqueued.Count == 1, "the download started");

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
            // Both past searching (a search still running when the second top-up skips the pick would be cancelled, not run on).
            await Wait.Until(() => w.Slskd.Enqueued.Count == 2 && w.Coordinator.Status.InFlight + w.Coordinator.Status.Ingesting == 2, "both fetches downloading or later");

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

    /// <summary>A peer whose download makes steady progress until <paramref name="release"/> completes, then succeeds.</summary>
    private static TransferScript Held(Task release) => new(e => release.IsCompleted
        ? new TransferStep("Completed, Succeeded", 8_000_000, 1_000_000)
        : new TransferStep("InProgress", Math.Min(7_999_999, 1 + (long)(e.TotalSeconds * 1000)), 1_000_000));

    /// <summary>Plays entries 0 and 1 of a new three-song album (the second-to-last song: a start fire). Returns the start batch.</summary>
    private static async Task<Guid[]> StartAlbumAsync(OrchestratorHarness h, Orchestrator o, Guid[] album)
    {
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(album, 0));
        h.Time.Advance(TimeSpan.FromSeconds(200));
        var before = h.Jellyfin.PlayLasts.Count;
        await h.Send(o, Sessions.WebAt(album, 1));
        Assert.Equal(before + 1, h.Jellyfin.PlayLasts.Count);
        return h.Jellyfin.PlayLasts.Last().ItemIds;
    }

    [Fact]
    public async Task FE10_Another_album_mid_fetch_cancels_the_old_runs_queued_jobs_and_its_running_downloads_finish_and_are_owned()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DeezerTrack[] songs = [];
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            songs = [w.Track("Radio C", "Held One"), w.Track("Radio D", "Held Two"), w.Track("Radio E", "Queued One"), w.Track("Radio F", "Queued Two")];
            w.Radio(songs);
            w.Offer("Radio C", "Held One", ("carol", 2_000_000, FetchWorld.Mp3("Radio C", "Held One")));
            w.Offer("Radio D", "Held Two", ("dave", 2_000_000, FetchWorld.Mp3("Radio D", "Held Two")));
            w.Slskd.Peers["carol"] = Held(release.Task);
            w.Slskd.Peers["dave"] = Held(release.Task);
            w.OfferGood("Radio E", "Queued One", "erin");
            w.OfferGood("Radio F", "Queued Two", "frank");
        }, scale: 50);
        try
        {
            // Run 1 asks for all four; two slots: the two held songs download, the other two wait in the queue.
            await l.StartAsync();
            var oldRun = w.Fetch.For("Held One").RunId;
            Assert.Equal(4, w.Fetch.Requests.Count);
            await Wait.Until(() => w.Slskd.Enqueued.Count == 2 && w.Coordinator.Status is { InFlight: 2, Queued: 2 }, "two downloading, two queued");

            // The user starts another album (Deezer has nothing new for it): run 1 is replaced.
            w.Radio();
            var second = await StartAlbumAsync(h, o, [Catalog.G(600), Catalog.G(601), Catalog.G(602)]);

            Assert.All(second, id => Assert.Contains(id, CatalogSongs));
            Assert.Equal((oldRun, "replaced"), Assert.Single(w.Fetch.EndedRuns));
            Assert.Equal(2, Assert.Single(w.CoordinatorLog.Named("run.ended")).Fields["CancelledJobs"]);
            foreach (var title in new[] { "Queued One", "Queued Two" })
            {
                Assert.Equal(FetchFailure.Cancelled, Assert.IsType<FetchReadiness.Failed>(w.Readiness(title)).Reason);
            }

            foreach (var title in new[] { "Held One", "Held Two" })
            {
                Assert.Equal(FetchStage.Downloading, Assert.IsType<FetchReadiness.Pending>(w.Readiness(title)).Stage);
            }

            Assert.Equal(2, w.Coordinator.Status.Cancelled);
            Assert.Equal(0, w.Coordinator.Status.Queued);

            // The running downloads finish, are imported and ingested, and the songs are owned.
            release.SetResult();
            await w.UntilReadyAsync("Held One");
            await w.UntilReadyAsync("Held Two");
            var heldOne = ((FetchReadiness.Ready)w.Readiness("Held One")).ItemId;
            var heldTwo = ((FetchReadiness.Ready)w.Readiness("Held Two")).ItemId;
            Assert.Equal(heldOne, w.Owned.Resolve(w.Fetch.For("Held One").Candidate.Key, 200)?.ItemId);
            Assert.Equal(heldTwo, w.Owned.Resolve(w.Fetch.For("Held Two").Candidate.Key, 200)?.ItemId);
            Assert.Equal(["ready", "ready"], w.Query("SELECT state FROM fetched_files ORDER BY id"));

            // The cancelled songs were never searched for, let alone downloaded.
            var cancelledQueries = SoulseekQueries.For("Radio E", "Queued One").Concat(SoulseekQueries.For("Radio F", "Queued Two")).ToHashSet();
            Assert.DoesNotContain(w.Slskd.Started, q => cancelledQueries.Contains(q));
            Assert.Equal(["carol", "dave"], w.Slskd.Enqueued.Select(e => e.Username).Order());

            // A later run whose suggestions hold the two songs queues the server copies: no new fetch.
            w.Radio(songs[0], songs[1]);
            var third = await StartAlbumAsync(h, o, [Catalog.G(650), Catalog.G(651), Catalog.G(652)]);

            Assert.Equal(new[] { heldOne, heldTwo }.Order(), third.Order());
            Assert.Equal(4, w.Fetch.Requests.Count);
            Assert.Equal(["deezer_radio+server"], w.Query($"SELECT source FROM added_items WHERE item_id = '{heldOne:N}'"));
            Assert.Equal(2, w.Slskd.Enqueued.Count);
            Assert.Equal(0, o.Status.Failed);
        }
        finally
        {
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE11_A_restart_mid_fetch_settles_the_interrupted_rows_and_fetches_nothing_twice()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            // Jellyfin does not list the imported song before the restart, so its job waits in ingest.
            w.H.Jellyfin.IngestDelay = null;
            w.Radio(w.Track("Radio C", "Held Song"), w.Track("Radio D", "Imported Song"));
            w.Offer("Radio C", "Held Song", ("carol", 2_000_000, FetchWorld.Mp3("Radio C", "Held Song")));
            w.Slskd.Peers["carol"] = Held(hold.Task);
            w.OfferGood("Radio D", "Imported Song", "dave");
            var radioDir = w.RadioDir;
            w.Importer.Before = r =>
            {
                // The scripted importer puts no file in music-radio; the real one does, and reconciliation looks for it.
                var path = Path.Combine(radioDir, "soulseek", r.Artist, r.Title + ".mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [1, 2, 3]);
                return Task.CompletedTask;
            };
        }, scale: 50);
        try
        {
            await l.StartAsync();
            var heldPick = w.Fetch.For("Held Song");
            var importedKey = w.Fetch.For("Imported Song").Candidate.Key;
            await Wait.Until(() =>
                w.Query("SELECT state FROM fetched_files WHERE title = 'Held Song'") is ["downloading"]
                && w.Query("SELECT state FROM fetched_files WHERE title = 'Imported Song'") is ["imported"]
                && w.Coordinator.Status.Ingesting == 1, "one song downloading, one imported and waiting for Jellyfin");
            var heldJob = w.Query("SELECT job_id FROM fetched_files WHERE title = 'Held Song'").Single();
            var heldTransfer = Guid.Parse(w.Query("SELECT transfer_id FROM fetched_files WHERE title = 'Held Song'").Single());
            var partial = Path.Combine(w.DownloadsDir, "encore", heldJob);
            Directory.CreateDirectory(partial);
            File.WriteAllBytes(Path.Combine(partial, "01 Held Song.mp3"), [1]);

            // Restart: the host stops; a new process starts on the same database and folders. Jellyfin now lists what it is told about.
            h.Jellyfin.IngestDelay = TimeSpan.FromSeconds(60);
            o.Dispose();
            w = await w.RestartAsync();
            h.RestartStore();
            o = h.CreateOrchestrator();
            await o.RefreshAllowListAsync();
            l.Restarted(o);

            // Reconciliation: the interrupted download fails as restart, its slskd transfer and folder are removed; the imported song is reported again.
            var cleanup = Assert.Single(w.WorkerLog.Named("import.cleanup"));
            Assert.Equal(1, cleanup.Fields["RowsFailed"]);
            Assert.Equal(1, cleanup.Fields["RowsReingested"]);
            Assert.Equal(["failed|restart"], w.Query("SELECT state || '|' || fail_reason FROM fetched_files WHERE title = 'Held Song'"));
            Assert.Contains(w.Slskd.CancelledTransfers, t => t.User == "carol" && t.Id == heldTransfer && t.Remove);
            Assert.False(Directory.Exists(partial));
            Assert.Equal(FetchReasons.Unknown, Assert.IsType<FetchReadiness.Failed>(w.Coordinator.GetReadiness(heldPick.PickId)).Reason);

            await Wait.Until(() => w.Query("SELECT state FROM fetched_files WHERE title = 'Imported Song'") is ["ready"], "the imported song ready", 15_000);
            var importedId = w.FetchedId("Radio D", "Imported Song")!.Value;
            Assert.Equal([importedId.ToString("N")], w.Query("SELECT jellyfin_item_id FROM fetched_files WHERE title = 'Imported Song'"));
            Assert.Equal(importedId, w.Owned.Resolve(importedKey, 200)?.ItemId);

            // The radio carries on: the imported song goes out as the server copy; it is never downloaded again.
            var topUp = await l.TopUpAsync();

            Assert.Contains(importedId, topUp);
            Assert.Equal(2, topUp.Length);
            Assert.Equal(["deezer_radio+server"], w.Query($"SELECT source FROM added_items WHERE item_id = '{importedId:N}'"));
            Assert.DoesNotContain(w.Fetch.Requests, r => r.Candidate.Title == "Imported Song");
            Assert.Single(w.Slskd.Enqueued, e => e.Username == "dave");
            Assert.Single(w.Query("SELECT id FROM fetched_files WHERE title = 'Imported Song'"));
            Assert.Equal(0, o.Status.Failed);
        }
        finally
        {
            hold.TrySetResult();
            o.Dispose();
            await DisposeAsync(h, w);
        }
    }

    [Fact]
    public async Task FE12_With_slskd_down_the_radio_goes_on_with_owned_songs_and_health_is_ok_with_a_warning()
    {
        Guid owned = default;
        var (h, w, o, l) = await CreateAsync(FetchMode.Live, w =>
        {
            w.Slskd.Down = new HttpRequestException("Connection refused (gluetun-slskd:5030)");
            owned = w.OwnSong(80_001, "Radio A", "Owned Hit");
            w.Radio(w.Track("Radio B", "New Song"), w.Track("Radio A", "Owned Hit"));
            w.OfferGood("Radio B", "New Song");
        }, waitAvailable: false);
        try
        {
            // The whole first round of checks, not just the slskd one: the free-space reading comes after it, and health
            // read before it would also warn "free space unknown" (a slow CI runner hit that).
            await Wait.Until(() => w.Coordinator.Status is { SlskdReachable: false, RadioDirInLibrary: not null, FreeBytes: not null },
                "the slskd check failed and the library and free-space checks ran");

            var start = await l.StartAsync();
            var first = await l.TopUpAsync();
            var second = await l.TopUpAsync();

            Assert.Equal(owned, start[0]);
            Assert.All(new[] { start, first, second }, batch => Assert.Equal(2, batch.Length));
            Assert.All(start.Concat(first).Concat(second), id => Assert.True(id == owned || CatalogSongs.Contains(id), $"{id} is not an owned song"));
            Assert.Empty(w.Fetch.Requests);
            Assert.Empty(w.Slskd.Started);
            Assert.Empty(w.Slskd.Enqueued);
            Assert.Contains(h.Log.Named("pool.planned"), e => (string?)e.Fields["DropReason"] == AvailabilityReasons.SlskdUnreachable);
            Assert.True(l.HeartbeatFresh);
            Assert.Equal(0, o.Status.Failed);

            var health = Health(h, w, o);
            Assert.Equal(200, health.Code);
            Assert.Equal("ok", health.Report.Status);
            Assert.Equal(["slskd: unreachable"], health.Report.Warnings);
            Assert.False(health.Report.Slskd.Reachable);
            Assert.Equal("live", health.Report.Fetch.Mode);
            Assert.False(health.Report.Fetch.Available);
            Assert.Equal(AvailabilityReasons.SlskdUnreachable, health.Report.Fetch.UnavailableReason);

            // slskd comes back: the next state check clears the warning.
            w.Slskd.Down = null;
            await Wait.Until(() => w.Coordinator.Availability.Available, "fetching available again", 15_000);
            Assert.Empty(Health(h, w, o).Report.Warnings);
        }
        finally
        {
            await DisposeAsync(h, w);
        }

        static (int Code, Encore.Health.HealthReport Report) Health(OrchestratorHarness h, FetchWorld w, Orchestrator o)
        {
            var now = h.Time.GetUtcNow();
            var options = h.Options with { SlskdUrl = new Uri("http://gluetun-slskd:5030"), SlskdApiKey = "slskd-key-for-fe12-only" };
            var watcher = new Encore.Watch.WatcherStatus { State = Encore.Watch.SocketState.Subscribed, Since = now - TimeSpan.FromMinutes(10), LastSessionsAt = now };
            return Encore.Health.HealthEvaluator.Evaluate(new Encore.Health.HealthInput(options, "0.2.0", now, watcher, o.Status, h.DbPath, 2,
                new Encore.Health.FetchHealthInput(w.Coordinator.Status, 0, w.Owned.IsLoaded, w.Owned.Count, w.Owned.RefreshedUtc, null)));
        }
    }
}
