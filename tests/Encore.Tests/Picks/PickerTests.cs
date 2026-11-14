using Encore.Jellyfin;
using Encore.Picks;
using Microsoft.Extensions.Logging.Abstractions;
using static Encore.Tests.Picks.Lib;

namespace Encore.Tests.Picks;

public class PickerTests
{
    private static readonly Guid User = IdOf("user");

    private readonly FakePickClient _client = new();

    private Picker NewPicker() => new(_client, NullLogger<Picker>.Instance);

    private static PickRequest Request(BaseItemDto seed, int need, IEnumerable<Guid>? exclusions = null,
        IReadOnlyList<HistorySong>? history = null, IReadOnlyList<PickedItem>? planned = null) => new()
    {
        UserId = User,
        Seed = seed,
        Need = need,
        Exclusions = new HashSet<Guid>(exclusions ?? []),
        SessionHistory = history ?? [],
        Planned = planned ?? [],
    };

    private static void AssertPlanRules(IReadOnlyList<PickedItem> items, IReadOnlyList<PickedItem>? planned = null)
    {
        var all = (planned ?? []).Concat(items).ToList();
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        Assert.All(all.Where(i => i.ArtistId is not null).GroupBy(i => i.ArtistId), g => Assert.True(g.Count() <= PickLimits.MaxPerArtist));
        for (var i = 1; i < all.Count; i++)
        {
            Assert.False(all[i].ArtistId is not null && all[i].ArtistId == all[i - 1].ArtistId, $"back-to-back artist at {i}");
        }
    }

    [Fact]
    public async Task Primary_similar_artists_return_enough_in_two_calls()
    {
        var seed = Song("Seed", "A");
        _client.SimilarArtists[IdOf("artist:A")] = [Artist("B"), Artist("C"), Artist("D")];
        foreach (var a in new[] { "B", "C", "D" })
        {
            _client.SongsByArtist[IdOf("artist:" + a)] = Songs(a, 3);
        }

        var result = await NewPicker().PickAsync(Request(seed, 5));

        Assert.Equal(5, result.Items.Count);
        Assert.Equal(0, result.Shortfall);
        Assert.Equal(2, result.CallsMade);
        Assert.Equal(["similar_artists", "songs_by_artists"], _client.Calls);
        Assert.All(result.Items, i => Assert.Equal(PickSource.SimilarArtist, i.Source));
        Assert.Equal(15, _client.SongsRequests.Single().Limit);
        AssertPlanRules(result.Items);
    }

    [Fact]
    public async Task Empty_sources_fall_back_in_order_to_the_seed_artist()
    {
        var seed = Song("Seed", "A");
        _client.SongsByArtist[IdOf("artist:A")] = [seed, .. Songs("A", 3)];

        var result = await NewPicker().PickAsync(Request(seed, 2));

        Assert.Equal(["similar_artists", "similar_songs", "instant_mix", "songs_by_artists"], _client.Calls);
        Assert.Equal([PickSource.SimilarArtist, PickSource.SimilarSong, PickSource.InstantMix, PickSource.SeedArtist], result.SourcesTried);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal(PickSource.SeedArtist, i.Source));
        Assert.DoesNotContain(result.Items, i => i.Id == seed.Id);
        Assert.Equal([IdOf("artist:A")], _client.SongsRequests.Single().Artists);
    }

    [Fact]
    public async Task Similar_song_fallback_excludes_the_seed_artist_and_stops_when_enough()
    {
        var seed = Song("Seed", "A");
        _client.SimilarSongs[seed.Id] = [Song("A other", "A"), Song("X1", "X"), Song("Y1", "Y")];

        var result = await NewPicker().PickAsync(Request(seed, 2));

        Assert.Equal(["similar_artists", "similar_songs"], _client.Calls);
        Assert.Equal([IdOf("artist:A")], _client.SimilarSongExcludes.Single());
        Assert.Equal(["X1", "Y1"], result.Items.Select(i => i.Name));
        Assert.All(result.Items, i => Assert.Equal(PickSource.SimilarSong, i.Source));
    }

    [Fact]
    public async Task Instant_mix_drops_the_seed_it_returns_first()
    {
        var seed = Song("Seed", "A");
        _client.InstantMix[seed.Id] = [seed, Song("M1", "M"), Song("N1", "N")];

        var result = await NewPicker().PickAsync(Request(seed, 2));

        Assert.Equal(["M1", "N1"], result.Items.Select(i => i.Name));
        Assert.All(result.Items, i => Assert.Equal(PickSource.InstantMix, i.Source));
        Assert.Equal(3, result.CallsMade);
    }

    [Fact]
    public async Task Seed_and_queue_items_are_excluded()
    {
        var seed = Song("Seed", "A");
        var queued = Song("Q1", "Q");
        _client.InstantMix[seed.Id] = [seed, queued, Song("R1", "R")];

        var result = await NewPicker().PickAsync(Request(seed, 5, exclusions: [queued.Id]));

        Assert.Equal(["R1"], result.Items.Select(i => i.Name));
        Assert.Equal(4, result.Shortfall);
    }

    [Fact]
    public async Task Length_and_type_filters_apply_and_unknown_length_is_allowed()
    {
        var seed = Song("Seed", null);
        _client.InstantMix[seed.Id] =
        [
            Song("Interlude", "I", seconds: 30),
            Song("Long mix", "L", seconds: 13 * 60),
            Song("Video", "V", type: "MusicVideo"),
            Song("Edge short", "E", seconds: 60),
            Song("Edge long", "F", seconds: 12 * 60),
            Song("Unknown", "U", seconds: -1),
        ];

        var result = await NewPicker().PickAsync(Request(seed, 5));

        Assert.Equal(["Edge short", "Edge long", "Unknown"], result.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Seed_without_artist_starts_at_similar_songs()
    {
        var seed = Song("Seed", null);
        _client.SimilarSongs[seed.Id] = [Song("S1", "S")];

        var result = await NewPicker().PickAsync(Request(seed, 1));

        Assert.Equal(["similar_songs"], _client.Calls);
        Assert.Null(_client.SimilarSongExcludes.Single());
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task At_most_two_per_artist_and_no_back_to_back_artist()
    {
        var seed = Song("Seed", "Z");
        _client.InstantMix[seed.Id] = [.. Songs("A", 4), .. Songs("B", 1)];

        var result = await NewPicker().PickAsync(Request(seed, 5));

        Assert.Equal(["A 1", "B 1", "A 2"], result.Items.Select(i => i.Name));
        AssertPlanRules(result.Items);
    }

    [Fact]
    public void Select_avoids_the_seed_artist_first_and_repeats_only_when_nothing_else_is_left()
    {
        PickedItem P(string name, string artist) => new(IdOf(name), name, IdOf("artist:" + artist), artist, PickSource.InstantMix);
        var candidates = new[] { P("A1", "A"), P("B1", "B") };

        var picked = Picker.Select(candidates, [], previousArtist: IdOf("artist:A"), need: 2);
        Assert.Equal(["B1", "A1"], picked.Select(p => p.Name));

        var onlyA = Picker.Select([P("A1", "A"), P("A2", "A")], [], IdOf("artist:A"), 2);
        Assert.Equal(["A1", "A2"], onlyA.Select(p => p.Name));
    }

    [Fact]
    public async Task Planned_items_count_towards_the_artist_cap_and_are_not_returned()
    {
        var seed = Song("Seed", "Z");
        var a1 = Song("A 1", "A");
        var planned = new[]
        {
            new PickedItem(a1.Id, a1.Name, IdOf("artist:A"), "A", PickSource.InstantMix),
            new PickedItem(IdOf("song:C 1"), "C 1", IdOf("artist:C"), "C", PickSource.InstantMix),
            new PickedItem(IdOf("song:A 2"), "A 2", IdOf("artist:A"), "A", PickSource.InstantMix),
        };
        _client.InstantMix[seed.Id] = [.. Songs("A", 4), Song("B 1", "B")];

        var result = await NewPicker().PickAsync(Request(seed, 3, planned: planned));

        Assert.Equal(["B 1"], result.Items.Select(i => i.Name));
        AssertPlanRules(result.Items, planned);
    }

    [Fact]
    public async Task History_artists_are_secondary_seeds_when_still_short()
    {
        var seed = Song("Seed", "A");
        var h = IdOf("artist:H");
        _client.SimilarArtists[h] = [Artist("HS")];
        _client.SongsByArtist[IdOf("artist:HS")] = Songs("HS", 3);
        var history = new[] { new HistorySong(IdOf("song:old1"), IdOf("artist:A")), new HistorySong(IdOf("song:old2"), h) };

        var result = await NewPicker().PickAsync(Request(seed, 1, history: history));

        Assert.Equal("HS 1", Assert.Single(result.Items).Name);
        Assert.Equal(PickSource.SimilarArtist, result.Items[0].Source);
        Assert.Equal(["similar_artists", "similar_songs", "instant_mix", "songs_by_artists", "similar_artists", "songs_by_artists"], _client.Calls);
    }

    [Fact]
    public async Task Never_more_than_eight_calls()
    {
        var seed = Song("Seed", "A");
        _client.SimilarArtists[IdOf("artist:A")] = [Artist("B")];
        _client.SimilarArtists[IdOf("artist:H1")] = [Artist("C")];
        _client.SimilarArtists[IdOf("artist:H2")] = [Artist("D")];
        var history = new[] { new HistorySong(IdOf("s1"), IdOf("artist:H1")), new HistorySong(IdOf("s2"), IdOf("artist:H2")) };

        var result = await NewPicker().PickAsync(Request(seed, 5, history: history));

        Assert.True(result.IsEmpty);
        Assert.True(_client.Calls.Count <= PickLimits.MaxCalls);
        Assert.Equal(_client.Calls.Count, result.CallsMade);
        // 2 (primary) + 1 + 1 + 1 + 2 (first history artist) = 7; the second history artist would need 2 more.
        Assert.Equal(7, result.CallsMade);
    }

    [Fact]
    public async Task All_sources_empty_is_no_picks()
    {
        var seed = Song("Seed", "A");

        var result = await NewPicker().PickAsync(Request(seed, 2));

        Assert.True(result.IsEmpty);
        Assert.Equal(2, result.Shortfall);
        Assert.Equal(4, result.CallsMade);
    }

    [Fact]
    public async Task A_failing_source_is_skipped()
    {
        var seed = Song("Seed", "A");
        _client.SimilarArtists[IdOf("artist:A")] = [Artist("B")];
        _client.Failing.Add("songs_by_artists");
        _client.Failing.Add("similar_songs");
        _client.InstantMix[seed.Id] = [Song("M1", "M")];

        var result = await NewPicker().PickAsync(Request(seed, 1));

        Assert.Equal("M1", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task No_duplicates_when_sources_overlap()
    {
        var seed = Song("Seed", "A");
        var shared = Song("Shared", "S");
        _client.SimilarSongs[seed.Id] = [shared];
        _client.InstantMix[seed.Id] = [shared, Song("T1", "T")];

        var result = await NewPicker().PickAsync(Request(seed, 3));

        Assert.Equal(["Shared", "T1"], result.Items.Select(i => i.Name));
        AssertPlanRules(result.Items);
    }
}
