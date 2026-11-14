using Encore.Config;
using Encore.Fetch;
using Encore.Fetch.Scoring;
using Encore.Slskd;
using Encore.Store;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;

namespace Encore.Tests.Fetch;

/// <summary>SF01-SF19 (v0.2 design section 15, T46).</summary>
public sealed class SoulseekFetcherTests : IDisposable
{
    private const string Key = "radiohead|reckoner";
    private const string Q1 = "Radiohead Reckoner";
    private const string Dir = @"@@a\Music\Radiohead\In Rainbows\";
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "encore-t46-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly string _dbPath;
    private readonly AutoClock _clock = new(Start);
    private readonly FakeSlskd _slskd;
    private readonly SearchBudget _budget;
    private readonly FetchStore _store;
    private readonly ListLogger<SoulseekFetcher> _log = new();
    private readonly SoulseekFetcher _fetcher;

    public SoulseekFetcherTests()
    {
        _downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_downloads);
        _dbPath = Path.Combine(_root, "encore.db");
        EncoreStore.Open(_dbPath, _clock).Dispose();
        _store = FetchStore.Open(_dbPath);
        _slskd = new FakeSlskd(_clock, _downloads);
        _budget = new SearchBudget(new SearchLimit(34, 220), _clock);
        _fetcher = new SoulseekFetcher(_slskd, _budget, new Scorer(), _store, _downloads, _log, _clock);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------ helpers

    private SoulseekJob Job(string title = "Reckoner", bool dryRun = false, DateTimeOffset? deadline = null) =>
        new("job0000000000000000000000000001", Key, "Radiohead", title, "In Rainbows", 290, dryRun,
            deadline ?? _clock.GetUtcNow() + FetchLimits.JobDeadline);

    private static SlskdFile Flac(string name = "07 Reckoner.flac") => new(Dir + name, 32_000_000, null, null, 290, 44100, 16, "flac");

    private static SlskdFile Bad() => new(Dir + "07 Reckoner.m4a", 9_000_000, null, null, 290, null, null, "m4a");

    private static SlskdSearchResponse Peer(string user, long speed, bool free = true, params SlskdFile[] files) =>
        new(user, free, speed, 0, files.Length == 0 ? [Flac()] : files);

    private void Search(string query, TimeSpan? completeAfter = null, params SlskdSearchResponse[] responses) =>
        _slskd.Searches[query] = new SearchScript(responses, completeAfter ?? TimeSpan.FromSeconds(3));

    private Task<SoulseekFetchResult> Run(SoulseekJob job, SoulseekFetchHooks? hooks = null, CancellationToken soft = default,
        CancellationToken stopping = default) => _fetcher.FetchAsync(job, hooks, soft, stopping);

    private sealed record Row(string Outcome, string? Reason, string? Peer, string? Query, long? FileId);

    private List<Row> Attempts()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ConnectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT outcome, reason, peer, query, fetched_file_id FROM fetch_attempts ORDER BY id";
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
        {
            rows.Add(new Row(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
        }

        return rows;
    }

    /// <summary>Peer attempts only (search rows have reason "search" or no peer).</summary>
    private List<Row> PeerAttempts() => Attempts().Where(a => a.Peer is not null && a.Reason != "search").ToList();

    private (string Until, string Reason, long Misses)? NegativeRow()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ConnectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT until_utc, reason, misses FROM fetch_negative WHERE track_key = $k";
        cmd.Parameters.AddWithValue("$k", Key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetInt64(2)) : null;
    }

    private bool IsNegative => _store.IsNegative(Key, _clock.GetUtcNow());

    // ------------------------------------------------------------ SF01-SF19

    [Fact]
    public async Task SF01_q1_acceptable_file_is_downloaded_and_q2_never_runs()
    {
        Search(Q1, null, Peer("alice", 2_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(6));
        long? rowId = null;
        var hooks = new SoulseekFetchHooks(OnEnqueued: e =>
        {
            rowId = _store.InsertFetched(new NewFetchedFile(e.JobId, Key, "Radiohead", "Reckoner", "deezer_radio", "soulseek",
                "run1", "sess1", Guid.NewGuid(), _clock.GetUtcNow(), Peer: e.File.Username));
            return rowId;
        });

        var result = await Run(Job(), hooks);

        var done = Assert.IsType<SoulseekFetchResult.Downloaded>(result).File;
        Assert.Equal("alice", done.File.Username);
        Assert.Equal(Path.Combine(_downloads, "encore", Job().JobId, "07 Reckoner.flac"), done.LocalPath);
        Assert.Equal(1, done.PeerNo);
        Assert.Equal(rowId, done.FetchedFileId);
        Assert.Equal([Q1], _slskd.Started);
        var enq = Assert.Single(_slskd.Enqueued);
        Assert.Equal($"encore/{Job().JobId}", enq.Destination);
        Assert.Contains(_slskd.CancelledTransfers, c => c.Remove);   // the finished transfer entry is cleared
        var ok = Assert.Single(PeerAttempts());
        Assert.Equal("ok", ok.Outcome);
        Assert.Equal(rowId, ok.FileId);
        Assert.False(IsNegative);
        Assert.Single(_log.Named("slskd.downloaded"));
    }

    [Fact]
    public async Task SF02_q1_without_acceptable_file_runs_q2_and_takes_two_budget_slots()
    {
        const string q1 = "Radiohead Reckoner (2007 Remaster)";
        Search(q1, null, Peer("bob", 1_000_000, true, Bad()));
        Search(Q1, null, Peer("carol", 1_000_000));
        _slskd.Peers["carol"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        var result = await Run(Job("Reckoner (2007 Remaster)"));

        var done = Assert.IsType<SoulseekFetchResult.Downloaded>(result).File;
        Assert.Equal("carol", done.File.Username);
        Assert.Equal(Q1, done.Query);
        Assert.Equal([q1, Q1], _slskd.Started);
        Assert.Equal(2, _budget.SearchesInWindow);
        Assert.Equal(["rejected", "ok", "ok"], Attempts().Select(a => a.Outcome));
    }

    [Fact]
    public async Task SF03_no_results_in_both_queries_fails_with_attempt_rows_and_24h_negative_cache()
    {
        var result = await Run(Job("Reckoner (2007 Remaster)"));

        Assert.Equal(FetchFailure.NoResults, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        Assert.Equal(2, _slskd.Started.Count);
        Assert.Equal(["no_results", "no_results"], Attempts().Select(a => a.Outcome));
        var neg = NegativeRow();
        Assert.NotNull(neg);
        Assert.Equal(FetchFailure.NoResults, neg.Value.Reason);
        Assert.Equal((_clock.GetUtcNow() + TimeSpan.FromHours(24)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), neg.Value.Until);
        Assert.Single(_log.Named("score.none"));
        Assert.Empty(_slskd.Enqueued);
    }

    [Fact]
    public async Task SF03b_files_but_none_acceptable_is_no_acceptable()
    {
        Search(Q1, null, Peer("bob", 1_000_000, true, Bad()));

        var result = await Run(Job());

        Assert.Equal(FetchFailure.NoAcceptable, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        Assert.Equal(FetchFailure.NoAcceptable, NegativeRow()!.Value.Reason);
    }

    [Fact]
    public async Task SF04_free_slot_acceptable_file_at_6s_cancels_the_search_early()
    {
        Search(Q1, TimeSpan.FromSeconds(60), Peer("alice", 2_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        await Run(Job());

        var id = Assert.Single(_slskd.StartedIds);
        Assert.Contains(id, _slskd.CancelledSearches);
        var done = Assert.Single(_log.Named("slskd.search_done"));
        Assert.Equal("early", done.Fields["EndedBy"]);
        Assert.Equal(6000L, done.Fields["ElapsedMs"]);
    }

    [Fact]
    public async Task SF04b_busy_peer_does_not_stop_early_and_the_search_is_cut_at_20s()
    {
        Search(Q1, TimeSpan.FromSeconds(60), Peer("alice", 2_000_000, free: false));
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        await Run(Job());

        var done = Assert.Single(_log.Named("slskd.search_done"));
        Assert.Equal("max_wait", done.Fields["EndedBy"]);
        Assert.Equal(20000L, done.Fields["ElapsedMs"]);
        Assert.Contains(Assert.Single(_slskd.StartedIds), _slskd.CancelledSearches);
    }

    [Fact]
    public async Task SF05_every_search_is_deleted_also_on_failure()
    {
        _slskd.Searches[Q1] = new SearchScript([Peer("alice", 1)], TimeSpan.FromSeconds(2),
            ResponsesError: new SlskdRequestException("search.responses", 500));

        var result = await Run(Job());

        Assert.Equal(FetchFailure.SlskdUnavailable, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        Assert.Equal(_slskd.StartedIds, _slskd.DeletedSearches);
        Assert.False(IsNegative);

        // and after success and after "nothing found"
        _slskd.Searches.Clear();
        await Run(Job("Reckoner (2007 Remaster)"));
        Assert.Equal(3, _slskd.StartedIds.Count);
        Assert.Equal(_slskd.StartedIds, _slskd.DeletedSearches);
    }

    [Fact]
    public async Task SF06_peer_queued_for_20s_is_cancelled_and_removed_then_peer_2_succeeds()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.QueuedRemotely();
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        var result = await Run(Job());

        Assert.Equal("bob", Assert.IsType<SoulseekFetchResult.Downloaded>(result).File.File.Username);
        Assert.Contains(_slskd.CancelledTransfers, c => c.User == "alice" && c.Remove);
        Assert.Equal([("stalled", "queued", "alice"), ("ok", null, "bob")],
            PeerAttempts().Select(a => (a.Outcome, a.Reason, a.Peer!)));
        var stalled = Assert.Single(_log.Named("slskd.transfer_stalled"));
        Assert.Equal("queued", stalled.Fields["Why"]);
    }

    [Fact]
    public async Task SF07_in_progress_without_new_bytes_for_30s_moves_to_peer_2()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.IdleAt(5000);
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        var result = await Run(Job());

        Assert.IsType<SoulseekFetchResult.Downloaded>(result);
        Assert.Equal(("stalled", "idle"), PeerAttempts().Select(a => (a.Outcome, a.Reason)).First());
    }

    [Fact]
    public async Task SF08_eta_past_the_deadline_after_10s_is_too_slow()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Slow(1000);
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));
        var before = _clock.GetUtcNow();

        var result = await Run(Job());

        Assert.IsType<SoulseekFetchResult.Downloaded>(result);
        Assert.Equal(("stalled", "too_slow"), PeerAttempts().Select(a => (a.Outcome, a.Reason)).First());
        Assert.True(_clock.GetUtcNow() - before < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task SF09_errored_transfers_move_on_at_once_and_are_removed()
    {
        Search(Q1, null, Peer("alice", 3_000_000), Peer("bob", 2_000_000), Peer("carol", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Ends("Completed, Errored");
        _slskd.Peers["bob"] = FakeSlskd.Ends("Errored", Start.AddMinutes(5));
        _slskd.Peers["carol"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        var result = await Run(Job());

        Assert.Equal("carol", Assert.IsType<SoulseekFetchResult.Downloaded>(result).File.File.Username);
        Assert.Equal([("failed", "errored"), ("failed", "errored"), ("ok", null)],
            PeerAttempts().Select(a => (a.Outcome, a.Reason)));
        Assert.Contains(_slskd.CancelledTransfers, c => c.User == "alice" && c.Remove);
        Assert.Contains(_slskd.CancelledTransfers, c => c.User == "bob" && c.Remove);
    }

    [Fact]
    public async Task SF09b_rejected_transfer_is_reason_rejected()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Ends("Completed, Rejected");
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        await Run(Job());

        Assert.Equal(("failed", "rejected"), PeerAttempts().Select(a => (a.Outcome, a.Reason)).First());
    }

    [Fact]
    public async Task SF10_three_failed_peers_fail_the_job_with_negative_cache()
    {
        Search(Q1, null, Peer("a1", 4_000_000), Peer("a2", 3_000_000), Peer("a3", 2_000_000), Peer("a4", 1_000_000));
        foreach (var p in new[] { "a1", "a2", "a3", "a4" })
        {
            _slskd.Peers[p] = FakeSlskd.Ends("Completed, Errored");
        }

        var result = await Run(Job());

        Assert.Equal(FetchFailure.AllPeersFailed, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        Assert.Equal(["a1", "a2", "a3"], _slskd.Enqueued.Select(e => e.Username));
        Assert.Equal(FetchFailure.AllPeersFailed, NegativeRow()!.Value.Reason);
    }

    [Fact]
    public async Task SF11_enqueue_404_moves_to_the_next_peer()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = new TransferScript(_ => new TransferStep("Requested", 0, 0), EnqueueError: new SlskdPeerOfflineException("enqueue"));
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        var result = await Run(Job());

        Assert.Equal("bob", Assert.IsType<SoulseekFetchResult.Downloaded>(result).File.File.Username);
        Assert.Equal(("failed", "offline"), PeerAttempts().Select(a => (a.Outcome, a.Reason)).First());
    }

    [Fact]
    public async Task SF11b_enqueue_failures_list_is_offline()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = new TransferScript(_ => new TransferStep("Requested", 0, 0), Refuse: true);
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        Assert.IsType<SoulseekFetchResult.Downloaded>(await Run(Job()));
        Assert.Equal(("failed", "offline"), PeerAttempts().Select(a => (a.Outcome, a.Reason)).First());
    }

    [Fact]
    public async Task SF12_local_file_exact_or_renamed_or_missing()
    {
        // renamed by slskd
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4), writeFile: false);
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4), saveAs: "07 Reckoner_639000000000000000.flac");

        var result = await Run(Job());

        var done = Assert.IsType<SoulseekFetchResult.Downloaded>(result).File;
        Assert.Equal("bob", done.File.Username);
        Assert.Equal(Path.Combine(_downloads, "encore", Job().JobId, "07 Reckoner_639000000000000000.flac"), done.LocalPath);
        Assert.Equal(("failed", "missing_file"), PeerAttempts().Select(a => (a.Outcome, a.Reason)).First());
        Assert.Contains(_log.Named("slskd.transfer_stalled"), e => (string?)e.Fields["Why"] == "missing_file");
    }

    [Fact]
    public void SF12b_find_local_file_rules()
    {
        var dir = Path.Combine(_downloads, "encore", "j1");
        Directory.CreateDirectory(dir);
        Assert.Null(_fetcher.FindLocalFile("j1", @"@@a\x\01 Song.flac"));

        File.WriteAllBytes(Path.Combine(dir, "other.mp3"), [1]);
        Assert.Equal(Path.Combine(dir, "other.mp3"), _fetcher.FindLocalFile("j1", @"@@a\x\01 Song.flac"));   // the only audio file

        File.WriteAllBytes(Path.Combine(dir, "01 Song_123.flac"), [1]);
        Assert.Equal(Path.Combine(dir, "01 Song_123.flac"), _fetcher.FindLocalFile("j1", @"@@a\x\01 Song.flac"));

        File.WriteAllBytes(Path.Combine(dir, "01 Song.flac"), [1]);
        Assert.Equal(Path.Combine(dir, "01 Song.flac"), _fetcher.FindLocalFile("j1", @"@@a\x\01 Song.flac"));
    }

    [Fact]
    public async Task SF13_dry_run_never_enqueues_and_logs_would_download()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));

        var result = await Run(Job(dryRun: true));

        var would = Assert.IsType<SoulseekFetchResult.WouldDownload>(result);
        Assert.Equal("alice", would.Best.Username);
        Assert.Equal(1, would.Backups);
        Assert.Empty(_slskd.Enqueued);
        var log = Assert.Single(_log.Named("score.would_download"));
        Assert.Equal("alice", log.Fields["Peer"]);
        Assert.Equal(Dir + "07 Reckoner.flac", log.Fields["File"]);
        Assert.Equal("flac", log.Fields["Format"]);
        Assert.NotNull(log.Fields["Kbps"]);
        Assert.Equal(32_000_000L, log.Fields["Size"]);
        Assert.True((double)log.Fields["EtaSeconds"]! > 0);
        Assert.Equal(1, log.Fields["Backups"]);
        Assert.Equal("dry_run", Attempts().Last().Outcome);
        Assert.False(IsNegative);
    }

    [Fact]
    public async Task SF14_soft_cancel_before_the_enqueue_cancels_and_deletes_the_search()
    {
        Search(Q1, TimeSpan.FromSeconds(60), Peer("alice", 2_000_000, free: false));
        using var soft = new CancellationTokenSource();
        _slskd.OnCall = op =>
        {
            if (op == "get_search" && _slskd.GetSearchCalls == 3)
            {
                soft.Cancel();
            }
        };

        var result = await Run(Job(), soft: soft.Token);

        Assert.Equal(FetchFailure.Cancelled, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        var id = Assert.Single(_slskd.StartedIds);
        Assert.Contains(id, _slskd.CancelledSearches);
        Assert.Contains(id, _slskd.DeletedSearches);
        Assert.Empty(_slskd.Enqueued);
        Assert.Equal("cancelled", Attempts().Last().Outcome);
        Assert.False(IsNegative);
    }

    [Fact]
    public async Task SF15_soft_cancel_after_the_enqueue_is_ignored()
    {
        Search(Q1, null, Peer("alice", 2_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(8));
        using var soft = new CancellationTokenSource();
        var hooks = new SoulseekFetchHooks(OnEnqueued: _ =>
        {
            soft.Cancel();
            return null;
        });

        var result = await Run(Job(), hooks, soft.Token);

        Assert.IsType<SoulseekFetchResult.Downloaded>(result);
    }

    [Fact]
    public async Task SF15b_stopping_token_cancels_and_removes_the_running_download()
    {
        Search(Q1, null, Peer("alice", 2_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(60));
        using var stopping = new CancellationTokenSource();
        _slskd.OnCall = op =>
        {
            if (op == "get_transfer" && _slskd.GetTransferCalls == 2)
            {
                stopping.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Job(), stopping: stopping.Token));

        Assert.Contains(_slskd.CancelledTransfers, c => c.User == "alice" && c.Remove);
        Assert.Equal(("cancelled", "shutdown"), PeerAttempts().Select(a => (a.Outcome, a.Reason)).Last());
        Assert.False(IsNegative);
    }

    [Fact]
    public async Task SF16_budget_slot_after_the_deadline_fails_with_budget_and_no_negative_cache()
    {
        Assert.True(await _budget.TryAcquireAsync(_clock.GetUtcNow(), CancellationToken.None));   // the next slot is 5 s away

        var result = await Run(Job(deadline: _clock.GetUtcNow() + TimeSpan.FromSeconds(1)));

        Assert.Equal(FetchFailure.Budget, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        Assert.Empty(_slskd.Started);
        Assert.False(IsNegative);
    }

    [Fact]
    public async Task SF17_search_409_is_slskd_unavailable_without_negative_cache()
    {
        _slskd.Searches[Q1] = new SearchScript([], TimeSpan.FromSeconds(1), StartError: new SlskdUnavailableException("search.start"));

        var result = await Run(Job());

        Assert.Equal(FetchFailure.SlskdUnavailable, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        Assert.False(IsNegative);
        Assert.Empty(_slskd.Enqueued);
    }

    [Fact]
    public async Task SF17b_search_429_waits_and_retries()
    {
        _slskd.Searches[Q1] = new SearchScript([Peer("alice", 2_000_000)], TimeSpan.FromSeconds(3), BusyTimes: 2);
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        Assert.IsType<SoulseekFetchResult.Downloaded>(await Run(Job()));
        Assert.Equal(3, _slskd.Started.Count);
        Assert.Single(_slskd.StartedIds);
    }

    [Fact]
    public void SF18_local_name_is_the_last_backslash_segment()
    {
        Assert.Equal("07 Reckoner.flac", SoulseekFetcher.LocalName(@"@@abc\Music\Radiohead\In Rainbows\07 Reckoner.flac"));
        Assert.Equal("a.mp3", SoulseekFetcher.LocalName("x/y/a.mp3"));
        Assert.Equal("a.mp3", SoulseekFetcher.LocalName("a.mp3"));
    }

    [Fact]
    public void SF19_q2_text_is_simplified()
    {
        var q = SoulseekQueries.For("Beyoncé feat. Jay-Z", "Crazy in Love (feat. Jay-Z) - Remastered \"2003\" -explicit");

        Assert.Equal(2, q.Count);
        Assert.Equal("Beyoncé feat. Jay-Z Crazy in Love (feat. Jay-Z) Remastered 2003", q[0]);
        Assert.Equal("Beyonce Crazy in Love", q[1]);
        Assert.Equal(["Radiohead Reckoner"], SoulseekQueries.For("Radiohead", "Reckoner"));
        Assert.Equal("Sigur Ros Hoppipolla", SoulseekQueries.Simplified("Sigur Rós", "Hoppípolla [Live]"));
    }

    [Fact]
    public async Task Mismatch_from_verify_counts_as_a_peer_and_moves_on()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));
        var hooks = new SoulseekFetchHooks(VerifyAsync: (f, _) => Task.FromResult(f.File.Username == "bob"));

        var result = await Run(Job(), hooks);

        Assert.Equal(2, Assert.IsType<SoulseekFetchResult.Downloaded>(result).File.PeerNo);
        Assert.Equal([("rejected", "mismatch"), ("ok", null)], PeerAttempts().Select(a => (a.Outcome, a.Reason)));
    }

    [Fact]
    public async Task Deadline_passed_before_an_enqueue_fails_with_deadline()
    {
        Search(Q1, null, Peer("alice", 2_000_000), Peer("bob", 1_000_000));
        _slskd.Peers["alice"] = FakeSlskd.QueuedRemotely();
        _slskd.Peers["bob"] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(4));

        var result = await Run(Job(deadline: _clock.GetUtcNow() + TimeSpan.FromSeconds(20)));

        Assert.Equal(FetchFailure.Deadline, Assert.IsType<SoulseekFetchResult.Failed>(result).Reason);
        Assert.Equal(["alice"], _slskd.Enqueued.Select(e => e.Username));
        Assert.False(IsNegative);
    }
}
