using System.Collections.Concurrent;
using Encore.Config;
using Encore.Fetch;
using Encore.Fetch.Import;
using Encore.Jellyfin;
using Encore.Store;
using Encore.Tests.Watch;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Fetch;

/// <summary>FetchWorker end to end over fakes and a real fetch store (v0.2 design section 15, T49: FW02-FW16).</summary>
public class FetchWorkerTests
{
    private static bool IsReady(FetchReadiness r) => r is FetchReadiness.Ready;

    private static bool IsFailed(FetchReadiness r) => r is FetchReadiness.Failed;

    [Fact]
    public async Task FW02_happy_path_goes_through_every_stage_and_row_state_to_ready()
    {
        await using var h = new WorkerHarness();
        var seen = new ConcurrentQueue<string>();
        var itemId = Guid.NewGuid();
        h.Importer.Before = r =>
        {
            seen.Enqueue("import:" + h.RowState(r.JobId));
            return Task.CompletedTask;
        };
        h.Ingest.Behavior = (job, path, _) =>
        {
            seen.Enqueue("ingest:" + h.RowState(job));
            return Task.FromResult(new IngestResult(IngestOutcome.Ready, itemId, TimeSpan.FromSeconds(5)));
        };
        h.Fetcher.Behavior = async c =>
        {
            seen.Enqueue("search:" + h.Readiness("p1"));
            var hooks = c.Hooks with
            {
                OnEnqueued = e =>
                {
                    var id = c.Hooks.OnEnqueued!(e);
                    seen.Enqueue("enqueued:" + h.RowState(c.Job.JobId));
                    return id;
                },
            };
            return await FakeFetcher.DownloadAsync(new FetchCall(c.Job, hooks, c.Soft, c.Stopping));
        };
        await h.StartAsync();

        Assert.True(h.Request("p1", "Radiohead", "Reckoner"));
        await h.Until("p1", IsReady, "p1 ready");

        Assert.Equal(new FetchReadiness.Ready(itemId), h.Readiness("p1"));
        Assert.Equal(["search:" + new FetchReadiness.Pending(FetchStage.Searching), "enqueued:downloading", "import:importing", "ingest:imported"], seen);

        var states = h.CoordinatorLog.Named("fetch.state").Select(e => $"{e.Fields["From"]}>{e.Fields["To"]}").ToList();
        Assert.Equal(["queued>searching", "searching>downloading", "downloading>importing", "importing>ingesting"], states);

        var row = Assert.Single(h.Rows());
        Assert.Equal("ready", row.State);
        Assert.Equal("peer1", row.Peer);
        Assert.Equal(itemId.ToString("N"), row.JellyfinItemId);
        Assert.Equal("run1", row.RunId);
        Assert.Equal("deezer_radio", row.Origin);
        Assert.NotNull(row.FetchedUtc);
        Assert.NotNull(row.ImportedUtc);
        Assert.NotNull(row.ReadyUtc);
        Assert.NotNull(row.Path);

        var owned = Assert.Single(h.Owned.Added);
        Assert.Equal(itemId, owned.ItemId);
        Assert.Equal(TrackKeyOf("Radiohead", "Reckoner"), owned.Key);
        Assert.Equal("/music-radio/soulseek/Radiohead/Reckoner.mp3", Assert.Single(h.Ingest.Ingested).Path);

        var request = Assert.Single(h.Importer.Requests);
        Assert.Equal(("Radiohead", "Reckoner", "Album", 200, 42L), (request.Artist, request.Title, request.Album, request.DurationS, request.DeezerTrackId));
        Assert.NotNull(request.ArtistMbid);

        Assert.Single(h.CoordinatorLog.Named("fetch.ready"));
        var status = h.Coordinator.Status;
        Assert.Equal((1, 0, 1, 0, 0, 0), (status.Ready, status.Failed, status.LastHour, status.Queued, status.InFlight, status.Ingesting));
        Assert.NotNull(status.LastReadyUtc);
    }

    [Fact]
    public async Task FW02_the_owned_entry_comes_from_the_jellyfin_item_when_jellyfin_has_it()
    {
        await using var h = new WorkerHarness();
        var itemId = Guid.NewGuid();
        h.Jellyfin.ItemsById[itemId] = new BaseItemDto
        {
            Id = itemId, Name = "Reckoner", Type = "Audio", Path = "/music-radio/soulseek/Radiohead/Reckoner.mp3",
            ArtistItems = [new NameGuidPair { Id = Guid.NewGuid(), Name = "Radiohead" }], RunTimeTicks = TimeSpan.FromSeconds(290).Ticks,
        };
        h.Ingest.Behavior = (_, _, _) => Task.FromResult(new IngestResult(IngestOutcome.Ready, itemId, TimeSpan.Zero));
        await h.StartAsync();

        Assert.True(h.Request("p1", "Radiohead", "Reckoner"));
        await h.Until("p1", IsReady, "p1 ready");

        var owned = Assert.Single(h.Owned.Added);
        Assert.Equal(290, owned.DurationS);
        Assert.True(owned.InRadioDir);
    }

    [Fact]
    public async Task FW03_never_more_than_the_limit_in_searching_plus_downloading_and_ingest_waits_hold_no_slot()
    {
        await using var h = new WorkerHarness(concurrency: 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maxInFlight = 0;
        h.Fetcher.Behavior = async c =>
        {
            maxInFlight = Math.Max(maxInFlight, h.Coordinator.Status.InFlight);
            await gate.Task.WaitAsync(c.Stopping);
            return await FakeFetcher.DownloadAsync(c, owner: h.Fetcher);
        };
        var ingestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Ingest.Behavior = async (_, _, ct) =>
        {
            await ingestGate.Task.WaitAsync(ct);
            return new IngestResult(IngestOutcome.Ready, Guid.NewGuid(), TimeSpan.Zero);
        };
        await h.StartAsync();

        for (var i = 1; i <= 5; i++)
        {
            Assert.True(h.Request($"p{i}", "Artist", $"Song {i}"));
        }

        await Wait.Until(() => h.Fetcher.Calls.Count == 2, "two jobs started");
        await Task.Delay(200);
        Assert.Equal(2, h.Fetcher.Calls.Count);
        Assert.Equal(3, h.Coordinator.Status.Queued);
        Assert.Equal(2, h.Coordinator.Status.InFlight);

        gate.SetResult();   // all five download; ingest holds them, but takes no slot
        await Wait.Until(() => h.Ingest.Ingested.Count == 5, "all five reach ingest while two wait in it");
        Assert.True(h.Fetcher.MaxActive <= 2, $"max active {h.Fetcher.MaxActive}");
        Assert.True(maxInFlight <= 2, $"max in flight {maxInFlight}");
        Assert.Equal(5, h.Coordinator.Status.Ingesting);
        Assert.Equal(0, h.Coordinator.Status.InFlight);

        ingestGate.SetResult();
        for (var i = 1; i <= 5; i++)
        {
            await h.Until($"p{i}", IsReady, $"p{i} ready");
        }
    }

    [Fact]
    public async Task FW04_the_hourly_window_is_seeded_from_the_store_at_start()
    {
        await using var h = new WorkerHarness();
        for (var i = 0; i < FetchLimits.FetchesPerHour; i++)
        {
            h.Store.InsertFetched(new NewFetchedFile($"old{i}", $"a|t{i}", "A", $"T{i}", "deezer_radio", "soulseek", "r", "s", Guid.Empty,
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)));
        }

        await h.StartAsync(waitAvailable: false);
        await Wait.Until(() => h.Coordinator.Status.SlskdReachable == true, "slskd checked");
        Assert.Equal(new FetchAvailability(false, "hourly_cap"), h.Coordinator.Availability);
        Assert.False(h.Request("p1", "Radiohead", "Reckoner"));
        Assert.Equal(30, h.Coordinator.Status.LastHour);
    }

    [Fact]
    public async Task FW05_the_same_song_from_two_runs_is_one_job_and_both_picks_turn_ready()
    {
        await using var h = new WorkerHarness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fetcher.Behavior = async c =>
        {
            await gate.Task;
            return await FakeFetcher.DownloadAsync(c);
        };
        await h.StartAsync();

        Assert.True(h.Request("a1", "Radiohead", "Reckoner", runId: "runA"));
        Assert.True(h.Request("b1", "radiohead", "RECKONER", runId: "runB"));
        gate.SetResult();
        await h.Until("a1", IsReady, "a1 ready");
        await h.Until("b1", IsReady, "b1 ready");

        Assert.Single(h.Fetcher.Calls);
        Assert.Equal(h.Readiness("a1"), h.Readiness("b1"));
        Assert.Single(h.Rows());
        Assert.Single(h.CoordinatorLog.Named("fetch.ready"));
    }

    [Fact]
    public async Task FW06_end_run_cancels_queued_and_searching_jobs_and_a_downloading_job_finishes_and_is_owned()
    {
        await using var h = new WorkerHarness(concurrency: 2);
        var download = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource();
        h.Fetcher.Behavior = async c =>
        {
            if (c.Job.Title == "Downloading")
            {
                return await FakeFetcher.DownloadAsync(c, downloading: download.Task);
            }

            return await FakeFetcher.SearchAsync(c, never.Task) ?? new SoulseekFetchResult.Failed("no_results");
        };
        await h.StartAsync();

        Assert.True(h.Request("d", "Artist", "Downloading"));
        await h.Until("d", r => r == new FetchReadiness.Pending(FetchStage.Downloading), "d downloading");
        Assert.True(h.Request("s", "Artist", "Searching"));
        await h.Until("s", r => r == new FetchReadiness.Pending(FetchStage.Searching), "s searching");
        Assert.True(h.Request("q", "Artist", "Queued"));
        Assert.Equal(new FetchReadiness.Pending(FetchStage.Queued), h.Readiness("q"));

        h.Coordinator.EndRun("run1", "replaced");

        await h.Until("s", IsFailed, "s cancelled");
        Assert.Equal(new FetchReadiness.Failed("cancelled"), h.Readiness("s"));
        Assert.Equal(new FetchReadiness.Failed("cancelled"), h.Readiness("q"));
        Assert.Equal(new FetchReadiness.Pending(FetchStage.Downloading), h.Readiness("d"));
        Assert.Equal(2, h.CoordinatorLog.Named("fetch.cancelled").Count(e => Equals(e.Fields["Reason"], "run_end")));

        download.SetResult();
        await h.Until("d", IsReady, "d ready");
        Assert.Single(h.Owned.Added);
        Assert.Equal("ready", Assert.Single(h.Rows()).State);
        Assert.Equal(2, h.Coordinator.Status.Cancelled);
        Assert.DoesNotContain(h.Fetcher.Calls, c => c.Job.Title == "Queued");
    }

    [Fact]
    public async Task FW07_abandon_of_a_downloading_job_lets_it_finish()
    {
        await using var h = new WorkerHarness();
        var download = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fetcher.Behavior = c => FakeFetcher.DownloadAsync(c, downloading: download.Task);
        await h.StartAsync();

        Assert.True(h.Request("d", "Artist", "Song"));
        await h.Until("d", r => r == new FetchReadiness.Pending(FetchStage.Downloading), "downloading");
        h.Coordinator.Abandon("d", "skipped");
        download.SetResult();

        await h.Until("d", IsReady, "ready");
        Assert.Empty(h.CoordinatorLog.Named("fetch.cancelled"));
        Assert.Single(h.Owned.Added);
    }

    [Fact]
    public async Task FW08_a_failed_job_reads_failed_logs_it_and_fails_its_row()
    {
        await using var h = new WorkerHarness();
        h.Fetcher.Behavior = c => c.Job.Title switch
        {
            "No copy" => Task.FromResult<SoulseekFetchResult>(new SoulseekFetchResult.Failed("no_acceptable")),
            _ => FakeFetcher.DownloadAsync(c, peers: 2),   // both peers mismatch
        };
        h.Importer.Statuses.Enqueue((ImportStatus.Mismatch, ImportReason.Mismatch));
        h.Importer.Statuses.Enqueue((ImportStatus.Mismatch, ImportReason.Mismatch));
        await h.StartAsync();

        Assert.True(h.Request("n", "Artist", "No copy"));
        await h.Until("n", IsFailed, "n failed");
        Assert.Equal(new FetchReadiness.Failed("no_acceptable"), h.Readiness("n"));
        Assert.Empty(h.Rows());

        Assert.True(h.Request("m", "Artist", "Mismatch"));
        await h.Until("m", IsFailed, "m failed");
        Assert.Equal(new FetchReadiness.Failed("all_peers_failed"), h.Readiness("m"));
        var row = Assert.Single(h.Rows());
        Assert.Equal(("failed", "all_peers_failed", "peer2"), (row.State, row.FailReason, row.Peer));
        Assert.Equal(2, h.Importer.Requests.Count);

        var failed = h.CoordinatorLog.Named("fetch.failed").Select(e => (string)e.Fields["Reason"]!).ToList();
        Assert.Equal(["no_acceptable", "all_peers_failed"], failed);
        Assert.Equal(2, h.Coordinator.Status.Failed);
    }

    [Fact]
    public async Task FW08_an_import_failure_ends_the_job_as_import_failed()
    {
        await using var h = new WorkerHarness();
        h.Importer.Statuses.Enqueue((ImportStatus.Failed, ImportReason.DiskLow));
        await h.StartAsync();

        Assert.True(h.Request("p", "Artist", "Song"));
        await h.Until("p", IsFailed, "failed");
        Assert.Equal(new FetchReadiness.Failed("import_failed"), h.Readiness("p"));
        var row = Assert.Single(h.Rows());
        Assert.Equal(("failed", "import_failed"), (row.State, row.FailReason));
        Assert.Empty(h.Ingest.Ingested);
    }

    [Fact]
    public async Task FW08_an_ingest_timeout_or_failed_report_leaves_the_row_imported()
    {
        await using var h = new WorkerHarness();
        h.Ingest.Behavior = (job, _, _) => h.Fetcher.Calls.Count == 1
            ? Task.FromResult(new IngestResult(IngestOutcome.Timeout, null, FetchLimits.IngestWait))
            : Task.FromException<IngestResult>(new JellyfinRequestException("ReportMediaUpdated", 500));
        await h.StartAsync();

        Assert.True(h.Request("t", "Artist", "Timeout"));
        await h.Until("t", IsFailed, "t failed");
        Assert.True(h.Request("e", "Artist", "Error"));
        await h.Until("e", IsFailed, "e failed");

        Assert.Equal(new FetchReadiness.Failed("ingest_timeout"), h.Readiness("t"));
        Assert.Equal(new FetchReadiness.Failed("ingest_timeout"), h.Readiness("e"));
        Assert.All(h.Rows(), r => Assert.Equal("imported", r.State));
        Assert.Single(h.CoordinatorLog.Named("fetch.failed"), e => Equals(e.Fields["JobId"], h.Fetcher.CallFor("Error")!.Job.JobId));
        Assert.Single(h.WorkerLog.Named("fetch.ingest_failed"));
        Assert.Empty(h.Owned.Added);
    }

    [Fact]
    public async Task FW11_restart_settles_downloading_and_imported_rows_and_cleans_up()
    {
        await using var h = new WorkerHarness();
        var now = DateTimeOffset.UtcNow;
        var transfer = Guid.NewGuid();

        var downloading = h.Store.InsertFetched(new NewFetchedFile("jobdl", "a|t1", "A", "T1", "deezer_radio", "soulseek", "r", "s", Guid.Empty,
            now - TimeSpan.FromHours(3), Peer: "peerX", TransferId: transfer.ToString()));
        var jobFolder = Path.Combine(h.DownloadsDir, "encore", "jobdl");
        Directory.CreateDirectory(jobFolder);
        File.WriteAllText(Path.Combine(jobFolder, "part.mp3"), "x");

        var present = Path.Combine(h.RadioDir, "soulseek", "A", "T2.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(present)!);
        File.WriteAllText(present, "audio");
        var imported = h.Store.InsertFetched(new NewFetchedFile("jobok", "a|t2", "A", "T2", "deezer_radio", "soulseek", "r", "s", Guid.Empty,
            now - TimeSpan.FromHours(3)));
        h.Store.UpdateFetched(imported, FetchState.Imported, new FetchedUpdate(Path: present));

        var missing = h.Store.InsertFetched(new NewFetchedFile("jobgone", "a|t3", "A", "T3", "deezer_radio", "soulseek", "r", "s", Guid.Empty,
            now - TimeSpan.FromHours(3)));
        h.Store.UpdateFetched(missing, FetchState.Imported, new FetchedUpdate(Path: Path.Combine(h.RadioDir, "soulseek", "A", "T3.mp3")));

        var orphan = Path.Combine(h.DownloadsDir, "encore", "orphan");
        Directory.CreateDirectory(orphan);
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - TimeSpan.FromHours(2));
        var young = Path.Combine(h.DownloadsDir, "encore", "young");
        Directory.CreateDirectory(young);

        h.Importer.StagedFiles = 2;
        var itemId = Guid.NewGuid();
        h.Ingest.Behavior = (_, _, _) => Task.FromResult(new IngestResult(IngestOutcome.Ready, itemId, TimeSpan.Zero));
        h.Jellyfin.ItemsById[itemId] = new BaseItemDto
        {
            Id = itemId, Name = "T2", Type = "Audio", Path = "/music-radio/soulseek/A/T2.mp3",
            ArtistItems = [new NameGuidPair { Id = Guid.NewGuid(), Name = "A" }],
        };
        await h.StartAsync();
        await Wait.Until(() => h.Rows().Single(r => r.Id == imported).State == "ready", "imported row re-ingested");

        Assert.Equal(1, h.Importer.CleanCalls);
        Assert.Equal(("peerX", transfer, true), Assert.Single(h.Slskd.CancelledTransfers));
        Assert.False(Directory.Exists(jobFolder));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(young));

        var rows = h.Rows().ToDictionary(r => r.Id);
        Assert.Equal(("failed", "restart"), (rows[downloading].State, rows[downloading].FailReason));
        Assert.Equal(("failed", "file_missing"), (rows[missing].State, rows[missing].FailReason));
        Assert.Equal(itemId.ToString("N"), rows[imported].JellyfinItemId);
        Assert.Equal("/music-radio/soulseek/A/T2.mp3", Assert.Single(h.Ingest.Ingested).Path);
        Assert.Equal(itemId, Assert.Single(h.Owned.Added).ItemId);

        var cleanup = Assert.Single(h.WorkerLog.Named("import.cleanup"));
        Assert.Equal((2, 2, 1, 1), ((int)cleanup.Fields["StagedDeleted"]!, (int)cleanup.Fields["RowsFailed"]!,
            (int)cleanup.Fields["RowsReingested"]!, (int)cleanup.Fields["OrphanDirsDeleted"]!));
    }

    [Fact]
    public async Task FW12_shutdown_stops_running_downloads_within_5_seconds()
    {
        var h = new WorkerHarness(concurrency: 1);
        try
        {
            h.Fetcher.Behavior = async c =>
            {
                c.Hooks.OnEnqueued?.Invoke(new EnqueuedDownload(c.Job.JobId, 1, FakeFetcher.Scored("peer1", c.Job), Guid.NewGuid(), "q1"));
                await Task.Delay(Timeout.Infinite, c.Stopping);   // the real fetcher removes the transfer, then throws
                throw new InvalidOperationException("unreachable");
            };
            await h.StartAsync();
            Assert.True(h.Request("p", "Artist", "Song"));
            Assert.True(h.Request("q", "Artist", "Queued one"));
            await h.Until("p", r => r == new FetchReadiness.Pending(FetchStage.Downloading), "downloading");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await h.StopAsync();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stop took {watch.Elapsed}");

            Assert.Equal(new FetchReadiness.Failed("cancelled"), h.Readiness("p"));
            Assert.Equal(new FetchReadiness.Failed("cancelled"), h.Readiness("q"));
            Assert.Contains(h.CoordinatorLog.Named("fetch.cancelled"), e => Equals(e.Fields["Reason"], "shutdown"));
            Assert.Equal("downloading", Assert.Single(h.Rows(), r => r.State == "downloading").State);   // settled at the next start
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [Fact]
    public async Task FW13_readiness_moves_forward_only_while_read_from_many_threads()
    {
        await using var h = new WorkerHarness(concurrency: 3);
        h.Fetcher.Behavior = async c =>
        {
            await Task.Delay(Random.Shared.Next(1, 15));
            return await FakeFetcher.DownloadAsync(c);
        };
        h.Ingest.Behavior = async (_, _, _) =>
        {
            await Task.Delay(Random.Shared.Next(1, 15));
            return new IngestResult(IngestOutcome.Ready, Guid.NewGuid(), TimeSpan.Zero);
        };
        await h.StartAsync();

        const int picks = 12;
        using var stop = new CancellationTokenSource();
        var problems = new ConcurrentQueue<string>();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var last = new int[picks];
            while (!stop.IsCancellationRequested)
            {
                for (var i = 0; i < picks; i++)
                {
                    var r = h.Readiness($"p{i}");
                    var rank = r switch
                    {
                        FetchReadiness.Pending p => 1 + (int)p.Stage,
                        FetchReadiness.Ready => 10,
                        FetchReadiness.Failed { Reason: "unknown" } => 0,
                        _ => 10,
                    };
                    if (rank < last[i])
                    {
                        problems.Enqueue($"p{i} went back from {last[i]} to {rank}");
                    }

                    last[i] = rank;
                }
            }
        })).ToArray();

        for (var i = 0; i < picks; i++)
        {
            Assert.True(h.Request($"p{i}", "Artist", $"Song {i}"));
        }

        for (var i = 0; i < picks; i++)
        {
            await h.Until($"p{i}", IsReady, $"p{i} ready");
        }

        stop.Cancel();
        await Task.WhenAll(readers);
        Assert.Empty(problems);
    }

    [Fact]
    public async Task FW14_dry_run_searches_and_scores_only()
    {
        await using var h = new WorkerHarness(FetchMode.DryRun);
        h.Fetcher.Behavior = c =>
        {
            Assert.True(c.Job.DryRun);
            return Task.FromResult<SoulseekFetchResult>(new SoulseekFetchResult.WouldDownload(FakeFetcher.Scored("peer1", c.Job), 2, "q1"));
        };
        await h.StartAsync();

        Assert.True(h.Request("p", "Artist", "Song"));
        await h.Until("p", IsFailed, "done");

        Assert.Equal(new FetchReadiness.Failed("dry_run"), h.Readiness("p"));
        var status = h.Coordinator.Status;
        Assert.Equal((1, 0, 1, FetchMode.DryRun), (status.WouldDownload, status.Failed, status.LastHour, status.Mode));
        Assert.Empty(h.Rows());
        Assert.Empty(h.Importer.Requests);
        Assert.Empty(h.CoordinatorLog.Named("fetch.failed"));
    }

    [Fact]
    public async Task FW15_status_matches_the_jobs()
    {
        await using var h = new WorkerHarness(concurrency: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Importer.LeaveSource = true;
        h.Fetcher.Behavior = async c => c.Job.Title switch
        {
            "Fail" => new SoulseekFetchResult.Failed("no_results"),
            "Hold" => await FakeFetcher.SearchAsync(c, gate.Task) ?? await FakeFetcher.DownloadAsync(c),
            _ => await FakeFetcher.DownloadAsync(c),
        };
        await h.StartAsync();

        Assert.True(h.Request("ok", "Artist", "Ok"));
        await h.Until("ok", IsReady, "ok");
        Assert.True(h.Request("fail", "Artist", "Fail"));
        await h.Until("fail", IsFailed, "fail");
        Assert.True(h.Request("hold", "Artist", "Hold"));
        Assert.True(h.Request("queued", "Artist", "Queued"));
        Assert.True(h.Request("cancel", "Artist", "Cancel"));
        h.Coordinator.Abandon("cancel", "skipped");
        await h.Until("hold", r => r == new FetchReadiness.Pending(FetchStage.Searching), "hold searching");

        var s = h.Coordinator.Status;
        Assert.Equal((1, 1, 0, 1, 1, 1, 1), (s.Queued, s.InFlight, s.Ingesting, s.Ready, s.Failed, s.Cancelled, s.LastHour));
        Assert.Equal((true, true, true, "Music"), (s.SlskdReachable, s.SlskdLoggedIn, s.RadioDirInLibrary, s.RadioLibrary));
        Assert.Equal(100_000_000_000, s.FreeBytes);
        Assert.Equal(1, s.SourceLeftBehind);
        Assert.Equal(FetchLimits.FetchesPerHour, s.HourlyCap);
        Assert.True(s.Available);
        gate.SetResult();
    }

    [Fact]
    public async Task FW16_a_job_that_throws_fails_as_internal_and_the_worker_goes_on()
    {
        await using var h = new WorkerHarness(concurrency: 1);
        h.Fetcher.Behavior = c => c.Job.Title == "Boom"
            ? throw new InvalidOperationException("boom")
            : FakeFetcher.DownloadAsync(c);
        await h.StartAsync();

        Assert.True(h.Request("boom", "Artist", "Boom"));
        Assert.True(h.Request("fine", "Artist", "Fine"));
        await h.Until("boom", IsFailed, "boom failed");
        await h.Until("fine", IsReady, "fine ready");

        Assert.Equal(new FetchReadiness.Failed("internal"), h.Readiness("boom"));
        var error = Assert.Single(h.WorkerLog.Named("fetch.job_error"));
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains("boom", (string)error.Fields["Error"]!, StringComparison.Ordinal);
        Assert.NotNull(h.Coordinator.Status.LastError);
    }

    [Fact]
    public async Task FW10_the_worker_checks_slskd_and_the_library_and_reports_them()
    {
        await using var h = new WorkerHarness();
        h.Slskd.State = new Encore.Slskd.SlskdServerState(true, false, "Connected");
        h.Ingest.Library = new LibraryCheck(false, null, null, ["/music"]);
        await h.StartAsync(waitAvailable: false);

        await Wait.Until(() => h.Coordinator.Status.SlskdReachable == true, "slskd checked");
        Assert.Equal("not_logged_in", h.Coordinator.Availability.Reason);
        Assert.False(h.Request("p", "Artist", "Song"));
        Assert.Single(h.WorkerLog.Named("slskd.state"));
        await Wait.Until(() => h.Coordinator.Status.RadioDirInLibrary == false, "library checked");
    }

    [Fact]
    public async Task Unexpected_check_errors_do_not_stop_the_periodic_checks()
    {
        await using var h = new WorkerHarness();
        h.Slskd.Failure = new InvalidOperationException("odd");
        h.Ingest.LibraryFailure = new InvalidOperationException("odd");
        await h.StartAsync(waitAvailable: false);

        // The free-space check and the sweep come after both failing checks in the same round.
        await Wait.Until(() => h.Coordinator.Status.FreeBytes is not null, "free space checked");
        Assert.False(h.Coordinator.Status.SlskdReachable);
        Assert.Equal("slskd_unreachable", h.Coordinator.Availability.Reason);
        Assert.StartsWith("library check:", h.Coordinator.Status.LastError, StringComparison.Ordinal);
    }

    private static Encore.Discovery.TrackKey TrackKeyOf(string artist, string title) => Encore.Discovery.TrackKey.Create(artist, title)!.Value;
}
