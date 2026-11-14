using Encore.Config;
using Encore.Fetch;
using Encore.Tests.Picks;
using Microsoft.Data.Sqlite;
using Orchestrator = Encore.Orchestration.Orchestrator;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// The pick pool through the orchestrator (v0.2 design section 4, T50): live mode sends server copies and fetched
/// songs and stores which is which; dry_run queues exactly what mode off queues; a new start ends the old run.
/// </summary>
public class PoolTests
{
    private static readonly Guid[] Album = [Catalog.G(500), Catalog.G(501), Catalog.G(502)];
    private static readonly Guid Fetched = Catalog.G(9001);

    private static async Task<(OrchestratorHarness H, Orchestrator O, PoolWorld W)> CreateAsync(FetchMode mode)
    {
        var h = new OrchestratorHarness();
        h.Options = h.Options with { FetchMode = mode };
        var w = new PoolWorld();
        if (mode != FetchMode.Off)
        {
            h.Fetch = w.Fetch;
            h.Candidates = w.Candidates;
            h.Owned = w.Owned;
        }

        var o = h.CreateOrchestrator();
        await o.RefreshAllowListAsync();
        return (h, o, w);
    }

    /// <summary>Album to its second-to-last song (start fire), then the first Encore song (top-up). Returns both sends.</summary>
    private static async Task<(Guid[] Start, Guid[] TopUp)> PlayStartAndTopUpAsync(OrchestratorHarness h, Orchestrator o, Action? beforeTopUp = null)
    {
        await h.Send(o, Sessions.WebAt(Album, 0));
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(Album, 1));
        var start = h.Jellyfin.PlayLasts.Last().ItemIds;
        Guid[] full = [.. Album, .. start];

        beforeTopUp?.Invoke();
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(full, 2));
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(full, 3));
        Assert.Equal(2, h.Jellyfin.PlayLasts.Count);
        return (start, h.Jellyfin.PlayLasts.Last().ItemIds);
    }

    private static List<string> Query(OrchestratorHarness h, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={h.DbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task PL03_PL10_Live_sends_the_server_copy_at_once_and_the_fetched_song_at_the_top_up_and_stores_which()
    {
        var (h, o, w) = await CreateAsync(FetchMode.Live);
        using var _ = h;
        w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B"), w.ExtOwned("Owned Hit", "Radio A")];

        var (start, topUp) = await PlayStartAndTopUpAsync(h, o, () => w.Fetch.Set("New Song", new FetchReadiness.Ready(Fetched)));

        Assert.Equal(Lib.IdOf("song:Owned Hit"), start[0]);
        Assert.Equal(Fetched, topUp[0]);
        var request = Assert.Single(w.Fetch.Requests);
        Assert.Equal(Sessions.WebId, request.SessionId);
        Assert.Equal(Catalog.User, request.UserId);

        var sources = Query(h, "SELECT sources FROM triggers WHERE outcome = 'sent' ORDER BY id");
        Assert.Equal(2, sources.Count);
        Assert.StartsWith("deezer_radio+server,", sources[0]);
        Assert.StartsWith("deezer_radio+soulseek,", sources[1]);
        Assert.Equal(["deezer_radio+soulseek"], Query(h, $"SELECT source FROM added_items WHERE item_id = '{Fetched:N}'"));
        Assert.Contains("[deezer_radio+soulseek]", h.Log.Named("queue.added").Last().Message);
        Assert.NotEmpty(h.Log.Named("pool.planned"));
        Assert.Equal(2, h.Log.Named("pool.handout").Count());
    }

    [Fact]
    public async Task PL04_PL05_Live_never_waits_a_pending_fetch_is_passed_over_and_the_batch_is_still_full()
    {
        var (h, o, w) = await CreateAsync(FetchMode.Live);
        using var _ = h;
        w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B")];

        var (start, topUp) = await PlayStartAndTopUpAsync(h, o);

        Assert.Equal(2, start.Length);
        Assert.Equal(2, topUp.Length);
        Assert.Equal(1, o.Status.PassedOver);
        Assert.Equal(0, o.Status.Skipped);
        Assert.DoesNotContain("soulseek", string.Join(",", Query(h, "SELECT sources FROM triggers")));
    }

    [Fact]
    public async Task PL14_Dry_run_queues_exactly_what_mode_off_queues_and_makes_dry_run_requests()
    {
        var (off, offO, _) = await CreateAsync(FetchMode.Off);
        using var offH = off;
        var offSends = await PlayStartAndTopUpAsync(off, offO);

        var (dry, dryO, w) = await CreateAsync(FetchMode.DryRun);
        using var dryH = dry;
        w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B"), w.ExtOwned("Owned Hit", "Radio A")];
        var drySends = await PlayStartAndTopUpAsync(dry, dryO);

        Assert.Equal(offSends.Start, drySends.Start);
        Assert.Equal(offSends.TopUp, drySends.TopUp);
        var request = Assert.Single(w.Fetch.Requests);
        Assert.True(request.DryRun);
        Assert.All(dry.Log.Named("pool.planned"), e => Assert.Equal("dry_run", e.Fields["Mode"]));
        Assert.NotEmpty(dry.Log.Named("pool.planned"));
        Assert.Empty(dry.Log.Named("pool.handout"));
        Assert.Empty(off.Log.Named("pool.planned"));
    }

    [Fact]
    public async Task PL01_Mode_off_never_asks_for_outside_suggestions()
    {
        var (h, o, w) = await CreateAsync(FetchMode.Off);
        using var _ = h;
        w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B")];

        await PlayStartAndTopUpAsync(h, o);

        Assert.Empty(w.Candidates.Queries);
        Assert.Empty(w.Fetch.Requests);
    }

    [Fact]
    public async Task PL15_A_new_start_ends_the_old_run_on_the_fetch_side()
    {
        var (h, o, w) = await CreateAsync(FetchMode.Live);
        using var _ = h;
        w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B")];
        await h.Send(o, Sessions.WebAt(Album, 0));
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(Album, 1));
        var firstRun = (string)h.Log.Named("trigger.fired").Single().Fields["RunId"]!;
        Assert.Empty(w.Fetch.EndedRuns);

        Guid[] other = [Catalog.G(600), Catalog.G(601), Catalog.G(602)];
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(other, 0));
        h.Time.Advance(TimeSpan.FromSeconds(200));
        await h.Send(o, Sessions.WebAt(other, 1));

        Assert.Equal(2, h.Log.Named("trigger.fired").Count());
        Assert.Equal((firstRun, "replaced"), Assert.Single(w.Fetch.EndedRuns));
    }
}
