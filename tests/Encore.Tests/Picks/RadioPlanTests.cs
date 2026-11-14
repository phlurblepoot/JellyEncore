using Encore.Config;
using Encore.Picks;
using Microsoft.Extensions.Logging.Abstractions;
using static Encore.Tests.Picks.Lib;

namespace Encore.Tests.Picks;

public class RadioPlanTests
{
    private static readonly Guid User = IdOf("user");

    private readonly FakePickClient _client = new();

    private Picker NewPicker() => new(_client, NullLogger<Picker>.Instance);

    private void LibraryOf(string seedArtist, params string[] similar)
    {
        _client.SimilarArtists[IdOf("artist:" + seedArtist)] = [.. similar.Select(Artist)];
        foreach (var a in similar)
        {
            _client.SongsByArtist[IdOf("artist:" + a)] = Songs(a, 4);
        }
    }

    [Fact]
    public async Task Plans_lookahead_and_hands_out_batch()
    {
        LibraryOf("A", "B", "C", "D");
        var plan = new RadioPlan(lookahead: 5, batch: 2);

        var first = await plan.NextBatchAsync(NewPicker(), User, Song("Seed", "A"), [], new HashSet<Guid>());

        Assert.Equal(2, first.Batch.Count);
        Assert.Equal(3, first.Remaining.Count);
        Assert.Equal(5, first.Pick!.Items.Count);
        Assert.Equal(first.Pick.Items.Select(i => i.Id), first.Batch.Concat(first.Remaining).Select(i => i.Id));
    }

    [Fact]
    public async Task Top_up_refills_only_the_shortfall_and_never_repeats_a_song()
    {
        LibraryOf("A", "B", "C", "D", "E", "F", "G");
        var picker = new CountingPicker(NewPicker());
        var plan = new RadioPlan(5, 2);
        var seed = Song("Seed", "A");
        var handed = new List<PickedItem>();

        var first = await plan.NextBatchAsync(picker, User, seed, [], new HashSet<Guid>());
        handed.AddRange(first.Batch);
        for (var i = 0; i < 3; i++)
        {
            // Top-up: the current song is the last one Encore added.
            var current = handed[^1];
            var currentDto = Song(current.Name!, current.ArtistName);
            Assert.Equal(current.Id, currentDto.Id);
            var next = await plan.NextBatchAsync(picker, User, currentDto, [new HistorySong(seed.Id, IdOf("artist:A"))], new HashSet<Guid>());
            Assert.Equal(2, next.Batch.Count);
            handed.AddRange(next.Batch);
        }

        Assert.Equal([5, 2, 2, 2], picker.Needs);
        Assert.Equal(handed.Count, handed.Select(h => h.Id).Distinct().Count());
        Assert.Empty(plan.Items.Select(i => i.Id).Intersect(handed.Select(h => h.Id)));
        Assert.DoesNotContain(handed, h => h.Id == seed.Id);
    }

    [Fact]
    public async Task Planned_song_the_user_queued_is_dropped_at_hand_out()
    {
        LibraryOf("A", "B", "C", "D", "E");
        foreach (var a in new[] { "B", "C", "D", "E" })
        {
            // Every artist is similar to every other one, so the top-up seed has its own primary source.
            _client.SimilarArtists[IdOf("artist:" + a)] = [.. new[] { "B", "C", "D", "E" }.Where(o => o != a).Select(Artist)];
        }

        var plan = new RadioPlan(5, 2);
        var seed = Song("Seed", "A");
        var first = await plan.NextBatchAsync(NewPicker(), User, seed, [], new HashSet<Guid>());
        var userQueued = first.Remaining[0];

        var current = first.Batch[^1];
        var next = await plan.NextBatchAsync(NewPicker(), User, Song(current.Name!, current.ArtistName), [new HistorySong(seed.Id, IdOf("artist:A"))],
            new HashSet<Guid> { userQueued.Id });

        Assert.DoesNotContain(next.Batch.Concat(next.Remaining), i => i.Id == userQueued.Id);
        Assert.Equal(2, next.Batch.Count);
        Assert.Equal(3, next.Remaining.Count);
    }

    [Fact]
    public async Task Empty_sources_give_an_empty_batch()
    {
        var plan = new RadioPlan(5, 2);

        var handout = await plan.NextBatchAsync(NewPicker(), User, Song("Seed", "A"), [], new HashSet<Guid>());

        Assert.True(handout.IsEmpty);
        Assert.True(handout.Pick!.IsEmpty);
        Assert.Equal(["similar_artists", "similar_songs", "instant_mix", "songs_by_artists"], _client.Calls);
    }

    [Fact]
    public async Task Short_supply_hands_out_what_there_is()
    {
        var seed = Song("Seed", "A");
        _client.InstantMix[seed.Id] = [seed, Song("Only", "O")];
        var plan = new RadioPlan(5, 2);

        var handout = await plan.NextBatchAsync(NewPicker(), User, seed, [], new HashSet<Guid>());

        Assert.Equal("Only", Assert.Single(handout.Batch).Name);
        Assert.Empty(handout.Remaining);
    }

    [Fact]
    public async Task A_picker_returning_duplicates_or_excluded_ids_cannot_pollute_the_plan()
    {
        var seed = Song("Seed", "A");
        var x = new PickedItem(IdOf("x"), "x", IdOf("artist:X"), "X", PickSource.InstantMix);
        var banned = new PickedItem(IdOf("banned"), "banned", IdOf("artist:Y"), "Y", PickSource.InstantMix);
        var seedItem = new PickedItem(seed.Id, "Seed", IdOf("artist:A"), "A", PickSource.InstantMix);
        var bad = new StubPicker([x, x, banned, seedItem]);
        var plan = new RadioPlan(5, 2);

        var handout = await plan.NextBatchAsync(bad, User, seed, [], new HashSet<Guid> { banned.Id });

        Assert.Equal([x.Id], handout.Batch.Select(b => b.Id));
    }

    [Fact]
    public void Batch_must_fit_the_lookahead()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadioPlan(2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadioPlan(0, 0));
        _ = new RadioPlan(1, 1);
    }

    [Fact]
    public void Exclusions_session_window_uses_this_session_only()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var plays = Plays(now);

        var set = RecentExclusions.Build([IdOf("q1")], IdOf("seed"), plays, RecentWindow.Session, "s1", User, now);

        Assert.Equal(
            new HashSet<Guid> { IdOf("q1"), IdOf("seed"), IdOf("old-same-session"), IdOf("recent-same-session") },
            set);
    }

    [Fact]
    public void Exclusions_duration_window_uses_this_user_across_sessions()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        Assert.True(RecentWindow.TryParse("6h", out var sixHours));

        var set = RecentExclusions.Build([IdOf("q1")], IdOf("seed"), Plays(now), sixHours!, "s1", User, now);

        Assert.Equal(
            new HashSet<Guid> { IdOf("q1"), IdOf("seed"), IdOf("recent-same-session"), IdOf("recent-other-session") },
            set);
    }

    private static List<RecentPlay> Plays(DateTimeOffset now) =>
    [
        new("s1", User, IdOf("old-same-session"), now.AddHours(-10)),
        new("s1", User, IdOf("recent-same-session"), now.AddHours(-1)),
        new("s2", User, IdOf("recent-other-session"), now.AddHours(-2)),
        new("s2", User, IdOf("old-other-session"), now.AddHours(-7)),
        new("s3", IdOf("someone-else"), IdOf("other-user"), now.AddMinutes(-5)),
    ];

    private sealed class CountingPicker(IPicker inner) : IPicker
    {
        public List<int> Needs { get; } = [];

        public Task<PickResult> PickAsync(PickRequest request, CancellationToken ct = default)
        {
            Needs.Add(request.Need);
            return inner.PickAsync(request, ct);
        }
    }

    private sealed class StubPicker(IReadOnlyList<PickedItem> items) : IPicker
    {
        public Task<PickResult> PickAsync(PickRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PickResult(items, 1, 0, [PickSource.InstantMix]));
    }
}
