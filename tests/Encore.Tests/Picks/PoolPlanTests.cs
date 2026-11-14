using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Jellyfin;
using Encore.Picks;
using Encore.Tests.Watch;

namespace Encore.Tests.Picks;

/// <summary>The one pick pool of fetch mode live (v0.2 design section 4, PL cases of T50).</summary>
public class PoolPlanTests
{
    private static readonly Guid User = Lib.IdOf("user");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    private readonly PoolWorld _w = new();
    private readonly ListLogger<PoolPlan> _log = new();

    private PoolPlan NewPlan(int lookahead = 5, int batch = 2, TimeSpan? externalTimeout = null) =>
        new(lookahead, batch, _w.Fetch, _w.Candidates, _w.Owned, _log, null, externalTimeout);

    private static PlanContext Ctx(IPicker picker, IReadOnlySet<Guid>? exclusions = null, BaseItemDto? seed = null) =>
        new(picker, User, seed ?? PoolWorld.Seed(), [], exclusions ?? new HashSet<Guid>(), "run1", "sess1", Now);

    /// <summary>Owned-chain songs, each by its own artist.</summary>
    private List<PickedItem> Chain(int count, string prefix = "c") =>
        [.. Enumerable.Range(1, count).Select(i => _w.Own($"{prefix}{i}", $"Chain Artist {prefix}{i}"))];

    private static Guid Id(string title) => Lib.IdOf("song:" + title);

    [Fact]
    public async Task PL03_An_owned_outside_pick_goes_out_at_once_as_the_server_copy()
    {
        _w.Candidates.List = [_w.ExtOwned("Owned Hit", "Radio A")];
        var chain = Chain(8);
        var picker = new ScriptPicker(chain);
        var plan = NewPlan();

        var first = await plan.NextBatchAsync(Ctx(picker));

        Assert.Equal([Id("Owned Hit"), chain[2].Id], first.Batch.Select(b => b.Id));
        Assert.Equal("deezer_radio+server", first.Batch[0].WireSource());
        Assert.Equal(PickCopy.Server, first.Batch[0].Copy);
        Assert.Equal("similar_artist", first.Batch[1].WireSource());
        Assert.Equal([chain[0].Id, chain[1].Id], plan.Reserve.Select(r => r.Id));
        Assert.Equal([7], picker.Needs);
        Assert.Empty(_w.Fetch.Requests);
    }

    [Fact]
    public async Task PL04_An_unowned_pick_is_requested_at_planning_and_not_sent_at_that_fire()
    {
        _w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B"), _w.ExtOwned("Owned Hit", "Radio A")];
        var chain = Chain(8);
        var plan = NewPlan();

        var first = await plan.NextBatchAsync(Ctx(new ScriptPicker(chain)));

        var request = Assert.Single(_w.Fetch.Requests);
        Assert.Equal("run1:1", request.PickId);
        Assert.Equal("run1", request.RunId);
        Assert.Equal("sess1", request.SessionId);
        Assert.Equal(User, request.UserId);
        Assert.False(request.DryRun);
        Assert.Equal(Now + TimeSpan.FromMinutes(15), request.Deadline);
        Assert.Equal("New Song", request.Candidate.Title);

        Assert.Equal([Id("Owned Hit"), chain[2].Id], first.Batch.Select(b => b.Id));
        Assert.Equal(0, first.PassedOver);
        var waiting = plan.Items[0];
        Assert.True(waiting.IsFetch);
        Assert.Equal(0, waiting.PassOvers);
        Assert.Equal(Guid.Empty, first.Remaining[0].Id);
        Assert.Equal("deezer_radio+soulseek", first.Remaining[0].WireSource());
    }

    [Fact]
    public async Task PL05_PL06_A_pending_pick_is_passed_over_once_then_skipped_late()
    {
        _w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B")];
        var chain = Chain(12);
        var picker = new ScriptPicker(chain);
        var plan = NewPlan();
        var first = await plan.NextBatchAsync(Ctx(picker));
        Assert.DoesNotContain(first.Batch, b => b.Copy == PickCopy.Soulseek);

        var second = await plan.NextBatchAsync(Ctx(picker));
        Assert.Equal(1, second.PassedOver);
        Assert.Equal(2, second.Batch.Count);
        Assert.True(plan.Items[0].IsFetch);
        Assert.Equal(1, plan.Items[0].PassOvers);
        Assert.Empty(_w.Fetch.Abandoned);

        var third = await plan.NextBatchAsync(Ctx(picker));
        Assert.Equal(1, third.Skipped);
        Assert.Equal(2, third.Batch.Count);
        Assert.DoesNotContain(plan.Items, i => i.IsFetch);
        Assert.Equal(("run1:1", "late"), Assert.Single(_w.Fetch.Abandoned));
        var handout = _log.Named("pool.handout").Last();
        Assert.Contains("skipped_late", handout.Fields["Skipped"]?.ToString());
    }

    [Fact]
    public async Task PL10_A_ready_fetched_pick_goes_out_first_as_the_soulseek_copy()
    {
        _w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B")];
        var picker = new ScriptPicker(Chain(12));
        var plan = NewPlan();
        await plan.NextBatchAsync(Ctx(picker));
        var fetched = Lib.IdOf("fetched");
        _w.Fetch.Set("New Song", new FetchReadiness.Ready(fetched));

        var second = await plan.NextBatchAsync(Ctx(picker));

        Assert.Equal(fetched, second.Batch[0].Id);
        Assert.Equal("deezer_radio+soulseek", second.Batch[0].WireSource());
        Assert.Contains("[deezer_radio+soulseek]", second.Batch[0].ToString());
        Assert.Contains(fetched, plan.HandedOut);
        Assert.Contains($"{fetched:N}:deezer_radio+soulseek", _log.Named("pool.handout").Last().Fields["Sent"]?.ToString());
    }

    [Fact]
    public async Task PL07_A_failed_fetch_is_skipped_at_once()
    {
        _w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B")];
        var picker = new ScriptPicker(Chain(12));
        var plan = NewPlan();
        await plan.NextBatchAsync(Ctx(picker));
        _w.Fetch.Set("New Song", new FetchReadiness.Failed("no_results"));

        var second = await plan.NextBatchAsync(Ctx(picker));

        Assert.Equal(0, second.PassedOver);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(2, second.Batch.Count);
        Assert.DoesNotContain(plan.Items, i => i.IsFetch);
        Assert.Empty(_w.Fetch.Abandoned);
        Assert.Contains("skipped_failed(no_results)", _log.Named("pool.handout").Last().Fields["Skipped"]?.ToString());
    }

    [Fact]
    public async Task PL08_Nothing_ready_the_batch_comes_from_the_owned_reserve_and_the_reserve_refills()
    {
        _w.Candidates.List = [.. Enumerable.Range(1, 5).Select(i => PoolWorld.ExtNew($"New {i}", $"Radio {i}"))];
        var chain = Chain(2);
        var picker = new ScriptPicker(chain);
        var plan = NewPlan();

        var first = await plan.NextBatchAsync(Ctx(picker));
        Assert.Equal(chain.Select(c => c.Id), first.Batch.Select(b => b.Id));
        Assert.Equal(5, plan.Items.Count(i => i.IsFetch));
        Assert.Equal(2, _log.Named("pool.handout").Last().Fields["Filler"]);

        picker.Chain.AddRange(Chain(2, "d"));
        var second = await plan.NextBatchAsync(Ctx(picker));

        Assert.Equal([Id("d1"), Id("d2")], second.Batch.Select(b => b.Id));
        Assert.Equal(5, second.PassedOver);
        Assert.Equal(2, picker.Needs[^1]);
        Assert.Equal(2, _log.Named("pool.handout").Last().Fields["Filler"]);
    }

    [Fact]
    public async Task PL09_A_song_the_user_queued_and_an_excluded_ready_fetch_are_dropped()
    {
        _w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B"), _w.ExtOwned("Owned Hit", "Radio A")];
        var chain = Chain(12);
        var picker = new ScriptPicker(chain);
        var plan = NewPlan();
        var first = await plan.NextBatchAsync(Ctx(picker));
        Assert.Equal([Id("Owned Hit"), chain[2].Id], first.Batch.Select(b => b.Id));
        var userQueued = plan.Items.First(i => !i.IsFetch).Item!.Id;
        var fetched = Lib.IdOf("fetched");
        _w.Fetch.Set("New Song", new FetchReadiness.Ready(fetched));

        var second = await plan.NextBatchAsync(Ctx(picker, new HashSet<Guid> { userQueued, fetched }));

        Assert.DoesNotContain(userQueued, second.Batch.Select(b => b.Id));
        Assert.DoesNotContain(fetched, second.Batch.Select(b => b.Id));
        Assert.Equal(2, second.Batch.Count);
        Assert.DoesNotContain(plan.Items, i => i.Item?.Id == userQueued);
        Assert.Contains(("run1:1", "excluded"), _w.Fetch.Abandoned);
    }

    [Fact]
    public async Task PL11_Fetch_unavailable_plans_no_unowned_pick_and_says_why()
    {
        _w.Fetch.Availability = new FetchAvailability(false, AvailabilityReasons.NotLoggedIn);
        _w.Candidates.List = [PoolWorld.ExtNew("New 1", "Radio B"), PoolWorld.ExtNew("New 2", "Radio C")];
        var plan = NewPlan();

        var first = await plan.NextBatchAsync(Ctx(new ScriptPicker(Chain(10))));

        Assert.Empty(_w.Fetch.Requests);
        Assert.Equal(2, first.Batch.Count);
        Assert.DoesNotContain(plan.Items, i => i.IsFetch);
        var planned = Assert.Single(_log.Named("pool.planned"));
        Assert.Equal(2, planned.Fields["UnownedDropped"]);
        Assert.Equal("not_logged_in", planned.Fields["DropReason"]);
        Assert.Equal("live", planned.Fields["Mode"]);
    }

    [Fact]
    public async Task A_refused_request_drops_the_pick_and_the_next_candidate_is_tried()
    {
        _w.Fetch.Accept = r => r.Candidate.Title != "New 1";
        _w.Candidates.List = [PoolWorld.ExtNew("New 1", "Radio B"), PoolWorld.ExtNew("New 2", "Radio C")];
        var plan = NewPlan();

        await plan.NextBatchAsync(Ctx(new ScriptPicker(Chain(10))));

        Assert.Equal("New 2", Assert.Single(_w.Fetch.Requests).Candidate.Title);
        Assert.Equal(1, _log.Named("pool.planned").Single().Fields["UnownedDropped"]);
    }

    [Fact]
    public async Task PL12_An_outside_lookup_over_the_cut_off_plans_from_the_owned_chain()
    {
        _w.Candidates.Hang = true;
        var chain = Chain(10);
        var plan = NewPlan(externalTimeout: TimeSpan.FromMilliseconds(50));

        var first = await plan.NextBatchAsync(Ctx(new ScriptPicker(chain)));

        Assert.Equal([chain[2].Id, chain[3].Id], first.Batch.Select(b => b.Id));
        Assert.Single(_log.Named("pool.external_timeout"));
    }

    [Fact]
    public async Task An_outside_lookup_that_throws_plans_from_the_owned_chain()
    {
        _w.Candidates.Failure = new InvalidOperationException("boom");
        var plan = NewPlan();

        var first = await plan.NextBatchAsync(Ctx(new ScriptPicker(Chain(10))));

        Assert.Equal(2, first.Batch.Count);
        Assert.Single(_log.Named("pool.external_failed"));
    }

    [Fact]
    public async Task PL13_Owned_index_not_loaded_plans_from_the_owned_chain_only()
    {
        _w.Owned.IsLoaded = false;
        _w.Candidates.List = [PoolWorld.ExtNew("New 1", "Radio B")];
        var plan = NewPlan();

        var first = await plan.NextBatchAsync(Ctx(new ScriptPicker(Chain(10))));

        Assert.Empty(_w.Candidates.Queries);
        Assert.Empty(_w.Fetch.Requests);
        Assert.Equal(2, first.Batch.Count);
        Assert.Single(_log.Named("pool.index_not_ready"));
    }

    [Fact]
    public async Task PL02_Outside_suggestions_come_first_then_the_owned_chain_deduped_by_key_with_the_artist_rules()
    {
        _w.Candidates.List =
        [
            _w.ExtOwned("Hit One", "Radio A"),
            _w.ExtOwned("Rel One", "Related B", CandidateOrigin.DeezerRelated),
            _w.ExtOwned("Hit Two", "Radio A"),
            _w.ExtOwned("Hit Three", "Radio A"),
            _w.ExtOwned("Lb One", "Similar C", CandidateOrigin.ListenBrainzSimilar),
        ];

        // The owned chain holds another copy of "Hit One" (other album): same key, other id.
        var otherCopy = new PickedItem(Lib.IdOf("other copy"), "Hit One", Lib.IdOf("artist:Radio A"), "Radio A", PickSource.SimilarSong);
        _w.Owned.Add(new OwnedEntry(otherCopy.Id, TrackKey.Create("Radio A", "Hit One")!.Value, "Hit One", otherCopy.ArtistId, "Radio A", 200, "/x", null, false));
        var chain = Chain(2);
        chain.Add(otherCopy);
        chain.AddRange(Chain(4, "d"));
        var plan = NewPlan(lookahead: 6, batch: 1);

        await plan.NextBatchAsync(Ctx(new ScriptPicker(chain)));

        // Planned: Hit One, Rel One, Hit Two, Lb One (Hit Three: Radio A already twice), then the chain (c1 is the reserve).
        var planned = plan.Items.Select(i => i.Item!.Name).Prepend("Hit One").ToList();
        Assert.Equal(["Hit One", "Rel One", "Hit Two", "Lb One", "c2", "d1"], planned);
        Assert.DoesNotContain(plan.Items, i => i.Item!.Id == otherCopy.Id);
        Assert.Equal(["c1"], plan.Reserve.Select(r => r.Name));
        Assert.Equal("deezer_related+server", plan.Items[0].Item!.WireSource());
        Assert.Equal("lb_similar+server", plan.Items[2].Item!.WireSource());
    }

    [Fact]
    public async Task PL16_A_put_back_batch_is_sent_again_with_the_same_ids()
    {
        _w.Candidates.List = [PoolWorld.ExtNew("New Song", "Radio B")];
        var picker = new ScriptPicker(Chain(12));
        var plan = NewPlan();
        await plan.NextBatchAsync(Ctx(picker));
        _w.Fetch.Set("New Song", new FetchReadiness.Ready(Lib.IdOf("fetched")));
        var second = await plan.NextBatchAsync(Ctx(picker));

        plan.PutBack(second.Batch);
        var retry = await plan.NextBatchAsync(Ctx(picker));

        Assert.Equal(second.Batch.Select(b => b.Id), retry.Batch.Select(b => b.Id));
        Assert.Equal("deezer_radio+soulseek", retry.Batch[0].WireSource());
    }

    [Fact]
    public async Task PL17_The_plan_never_holds_more_than_lookahead_and_the_reserve_at_most_batch()
    {
        _w.Candidates.List = [.. Enumerable.Range(1, 8).Select(i => PoolWorld.ExtNew($"New {i}", $"Radio {i}"))];
        var picker = new ScriptPicker(Chain(40));
        var plan = NewPlan();
        var sent = new List<Guid>();
        for (var fire = 0; fire < 8; fire++)
        {
            if (fire % 3 == 1)
            {
                foreach (var r in _w.Fetch.Requests.Where(r => !_w.Fetch.Readiness.ContainsKey(r.PickId)).Take(1))
                {
                    _w.Fetch.Readiness[r.PickId] = new FetchReadiness.Ready(Lib.IdOf("fetched " + r.PickId));
                }
            }

            var handout = await plan.NextBatchAsync(Ctx(picker));
            Assert.InRange(plan.Items.Count, 0, 5);
            Assert.InRange(plan.Reserve.Count, 0, 2);
            Assert.Equal(2, handout.Batch.Count);
            sent.AddRange(handout.Batch.Select(b => b.Id));
        }

        Assert.Equal(sent.Count, sent.Distinct().Count());
    }

    [Fact]
    public async Task A_full_plan_makes_no_outside_lookup()
    {
        _w.Candidates.List = [.. Enumerable.Range(1, 5).Select(i => PoolWorld.ExtNew($"New {i}", $"Radio {i}"))];
        var picker = new ScriptPicker(Chain(2));
        var plan = NewPlan(lookahead: 5, batch: 2);
        await plan.NextBatchAsync(Ctx(picker));

        // The plan is still full of pending fetches (all passed over); only the empty reserve needs the owned chain.
        await plan.NextBatchAsync(Ctx(picker));

        Assert.Single(_w.Candidates.Queries);
    }

    [Fact]
    public async Task Dry_run_shadow_requests_dry_run_fetches_from_outside_suggestions_only()
    {
        _w.Candidates.List = [PoolWorld.ExtNew("New 1", "Radio B"), _w.ExtOwned("Owned Hit", "Radio A")];
        var picker = new ScriptPicker(Chain(5));
        var shadow = new PoolPlan(5, 2, _w.Fetch, _w.Candidates, _w.Owned, _log, pickIdPrefix: "run1:dry1");

        var keys = await shadow.PlanShadowAsync(Ctx(picker), new HashSet<Guid>(), new HashSet<TrackKey>());

        Assert.Empty(picker.Needs);
        var request = Assert.Single(_w.Fetch.Requests);
        Assert.True(request.DryRun);
        Assert.Equal("run1:dry1:1", request.PickId);
        Assert.Equal([TrackKey.Create("Radio B", "New 1")!.Value], keys);
        Assert.Equal("dry_run", _log.Named("pool.planned").Single().Fields["Mode"]);

        // A key already asked for in this run is not asked again.
        var again = new PoolPlan(5, 2, _w.Fetch, _w.Candidates, _w.Owned, _log, pickIdPrefix: "run1:dry2");
        await again.PlanShadowAsync(Ctx(picker), new HashSet<Guid>(), new HashSet<TrackKey>(keys));
        Assert.Single(_w.Fetch.Requests);
    }

    [Fact]
    public void No_setting_or_parameter_sets_an_owned_to_new_ratio()
    {
        static bool Ratio(string name) =>
            name.Contains("ratio", StringComparison.OrdinalIgnoreCase) || name.Contains("percent", StringComparison.OrdinalIgnoreCase)
            || name.Contains("share", StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(typeof(EncoreOptions).GetProperties(), p => Ratio(p.Name));
        Assert.DoesNotContain(EncoreOptionsLoader.Variables, v => Ratio(v));
        Assert.DoesNotContain(typeof(PoolPlan).GetConstructors().SelectMany(c => c.GetParameters()), p => Ratio(p.Name!));
    }
}
