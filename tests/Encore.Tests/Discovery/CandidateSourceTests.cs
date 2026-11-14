using System.Net;
using Encore.Discovery;
using Encore.Jellyfin;
using Encore.Tests.Watch;
using static Encore.Tests.Discovery.Songs;

namespace Encore.Tests.Discovery;

public sealed class CandidateSourceTests
{
    private static readonly Guid Mbid = Guid.Parse("a74b1b7f-71a5-4011-9441-d0b5e4122711");
    private const int SeedArtistId = 70;

    private sealed class Rig
    {
        public Rig(bool withMbid = true)
        {
            Library.Items[Id(SeedArtistId)] = new BaseItemDto
            {
                Id = Id(SeedArtistId),
                Name = "Radiohead",
                Type = "MusicArtist",
                ProviderIds = withMbid ? new Dictionary<string, string> { ["MusicBrainzArtist"] = Mbid.ToString("D") } : new Dictionary<string, string>(),
            };
            Runtime = new DiscoveryRuntime(null, Clock);
            Owned = new OwnedIndex(Library, RadioDir, new ListLogger<OwnedIndex>(), Clock);
            Deezer.AddArtist(399, "Radiohead");
        }

        public ManualClock Clock { get; } = new();

        public FakeDeezer Deezer { get; } = new();

        public FakeListenBrainz ListenBrainz { get; } = new();

        public FakeLibrary Library { get; } = new();

        public FakeNegative Negative { get; } = new();

        public DiscoveryRuntime Runtime { get; }

        public OwnedIndex Owned { get; }

        public ListLogger<CandidateSource> Log { get; } = new();

        public BaseItemDto Seed { get; set; } = Audio(1000, "Radiohead", "Reckoner", 290, artistId: SeedArtistId);

        public CandidateSource Build(IDeezerClient? deezer = null, IListenBrainzClient? lb = null) =>
            new(deezer ?? Deezer, lb ?? ListenBrainz, Library, Owned, Negative, Runtime, Log);

        public async Task<IReadOnlyList<ResolvedCandidate>> GetAsync(
            IReadOnlySet<Guid>? excludedIds = null, IReadOnlySet<TrackKey>? excludedKeys = null, CancellationToken ct = default)
        {
            await Owned.RefreshAsync(CancellationToken.None);
            return await Build().GetAsync(
                new CandidateQuery(Seed, excludedIds ?? new HashSet<Guid>(), excludedKeys ?? new HashSet<TrackKey>()), ct);
        }

        /// <summary>Radio: two songs (the second a remaster of related artist 2's first). Related: six artists with two songs each.
        /// ListenBrainz: six similar artists, the first the same as related artist 2 by ArtistKey.</summary>
        public void World()
        {
            Deezer.Radio[399] = [Track(1, "R Artist", "Radio One"), Track(2, "Dee 2", "D2 Song 1 (Remastered)", artistId: 602)];
            Deezer.Related[399] = Enumerable.Range(1, 6).Select(i => new DeezerArtist(600 + i, $"Dee {i}", 10, true)).ToList();
            for (var i = 1; i <= 6; i++)
            {
                Deezer.Top[600 + i] = [Track(6000 + (i * 10) + 1, $"Dee {i}", $"D{i} Song 1", artistId: 600 + i), Track(6000 + (i * 10) + 2, $"Dee {i}", $"D{i} Song 2", artistId: 600 + i)];
            }

            var similar = new List<LbSimilarArtist> { new(Id(800), "The Dee 2", 100) };
            for (var i = 2; i <= 7; i++)
            {
                similar.Add(new LbSimilarArtist(Id(800 + i), $"Lb {i}", 100 - i));
                Deezer.AddArtist(700 + i, $"Lb {i}");
                Deezer.Top[700 + i] = [Track(7000 + i, $"Lb {i}", $"L{i} Song", artistId: 700 + i)];
            }

            ListenBrainz.Similar[Mbid] = similar;
        }
    }

    [Fact]
    public async Task CS01_CS09_radio_related_and_listenbrainz_merge_round_robin_deduped_by_key()
    {
        var rig = new Rig();
        rig.World();

        var result = await rig.GetAsync();

        Assert.Equal(
            [
                "Radio One", "D1 Song 1", "L2 Song",
                "D2 Song 1 (Remastered)", "L3 Song",
                "D3 Song 1", "L4 Song",
                "D4 Song 1", "L5 Song",
                "D5 Song 1", "L6 Song",
                "D1 Song 2", "D2 Song 2", "D3 Song 2", "D4 Song 2", "D5 Song 2",
            ],
            result.Select(r => r.Candidate.Title));
        Assert.Equal(Enumerable.Range(1, result.Count), result.Select(r => r.Candidate.Rank));
        Assert.Equal(CandidateOrigin.DeezerRadio, result[0].Candidate.Origin);
        Assert.Equal(CandidateOrigin.DeezerRelated, result[1].Candidate.Origin);
        Assert.Equal(CandidateOrigin.ListenBrainzSimilar, result[2].Candidate.Origin);
        Assert.Equal(Id(802), result[2].Candidate.ArtistMbid);
        Assert.Null(result[0].Candidate.ArtistMbid);
        Assert.Equal(1, result[0].Candidate.DeezerTrackId);
        Assert.Equal("Album 1", result[0].Candidate.Album);
        Assert.Equal("deezer_related", result[1].Candidate.Origin.ToWire());

        // CS09: five related artists with five songs each; ListenBrainz artists not already in related.
        var calls = rig.Deezer.Calls.ToList();
        Assert.Contains("radio:399:25", calls);
        Assert.Contains("related:399:10", calls);
        Assert.Equal(["top:601:5", "top:602:5", "top:603:5", "top:604:5", "top:605:5"], calls.Where(c => c.StartsWith("top:60", StringComparison.Ordinal)).Order());
        Assert.Equal(["find:Lb 2", "find:Lb 3", "find:Lb 4", "find:Lb 5", "find:Lb 6"], calls.Where(c => c.StartsWith("find:Lb", StringComparison.Ordinal)).Order());
        Assert.DoesNotContain("find:The Dee 2", calls);
        Assert.Equal((Mbid, 10), Assert.Single(rig.ListenBrainz.Calls));
        Assert.InRange(rig.Deezer.MaxInFlight, 1, CandidateLimits.MaxInFlight);
        Assert.Single(rig.Log.Named("discovery.seed"));
        var summary = Assert.Single(rig.Log.Named("discovery.candidates"));
        Assert.Equal(2, summary.Fields["Radio"]);
        Assert.Equal(10, summary.Fields["Related"]);
        Assert.Equal(5, summary.Fields["Lb"]);
        Assert.Equal(16, summary.Fields["Total"]);
    }

    [Fact]
    public async Task CS02_a_seed_without_an_mbid_makes_no_listenbrainz_call()
    {
        var rig = new Rig(withMbid: false);
        rig.World();

        var result = await rig.GetAsync();

        Assert.Empty(rig.ListenBrainz.Calls);
        Assert.DoesNotContain(result, r => r.Candidate.Origin == CandidateOrigin.ListenBrainzSimilar);
        Assert.Equal(11, result.Count);
    }

    [Fact]
    public async Task CS03_owned_candidates_are_kept_with_their_server_copy()
    {
        var rig = new Rig();
        rig.World();
        rig.Library.Audio = [Audio(2000, "Dee 1", "D1 Song 1", 200, artistId: 601), Audio(2001, "Lb 3", "L3 Song", 200, path: "/music-radio/soulseek/l3.flac", artistId: 703)];

        var result = await rig.GetAsync();

        Assert.Equal(16, result.Count);
        Assert.Equal(Id(2000), result.Single(r => r.Candidate.Title == "D1 Song 1").Owned!.ItemId);
        var radioCopy = result.Single(r => r.Candidate.Title == "L3 Song").Owned!;
        Assert.Equal(Id(2001), radioCopy.ItemId);
        Assert.True(radioCopy.InRadioDir);
        Assert.Equal(14, result.Count(r => r.Owned is null));
        var summary = Assert.Single(rig.Log.Named("discovery.candidates"));
        Assert.Equal(2, summary.Fields["Owned"]);
        Assert.Equal(14, summary.Fields["Unowned"]);
    }

    [Fact]
    public async Task CS04_seed_excluded_negative_and_out_of_length_songs_are_dropped()
    {
        var rig = new Rig();
        rig.Deezer.Radio[399] =
        [
            Track(11, "Radiohead", "Reckoner (2007 Remaster)"),
            Track(12, "X", "Owned Excluded"),
            Track(13, "X", "Excluded Key"),
            Track(14, "X", "Negative Unowned"),
            Track(15, "X", "Negative Owned"),
            Track(16, "X", "Too Short", 59),
            Track(17, "X", "Min", 60),
            Track(18, "X", "Max", 720),
            Track(19, "X", "Too Long", 721),
            Track(20, "X", "?!"),
        ];
        rig.Library.Audio = [Audio(3001, "X", "Owned Excluded", artistId: 99), Audio(3002, "X", "Negative Owned", artistId: 99)];
        rig.Negative.Keys.Add(TrackKey.Create("X", "Negative Unowned")!.Value.Value);
        rig.Negative.Keys.Add(TrackKey.Create("X", "Negative Owned")!.Value.Value);

        var result = await rig.GetAsync(
            excludedIds: new HashSet<Guid> { Id(3001) },
            excludedKeys: new HashSet<TrackKey> { TrackKey.Create("X", "Excluded Key")!.Value });

        Assert.Equal(["Negative Owned", "Min", "Max"], result.Select(r => r.Candidate.Title));
        Assert.Equal(Id(3002), result[0].Owned!.ItemId);
        Assert.Equal(7, Assert.Single(rig.Log.Named("discovery.candidates")).Fields["Dropped"]);
    }

    [Fact]
    public async Task CS05_recently_played_songs_are_dropped_including_another_copy()
    {
        var rig = new Rig();
        rig.Deezer.Radio[399] = [Track(21, "X", "Played Song", 250), Track(22, "X", "Fresh Song", 250)];
        rig.Library.Audio =
        [
            Audio(4001, "X", "Played Song", 200, path: "/music-radio/soulseek/p.flac", artistId: 99),
            Audio(4002, "X", "Played Song", 250, artistId: 99),
        ];

        // Only the music-radio copy was played; the main-library copy (the one Resolve would pick) is dropped too.
        var result = await rig.GetAsync(excludedIds: new HashSet<Guid> { Id(4001) });

        Assert.Equal(["Fresh Song"], result.Select(r => r.Candidate.Title));
    }

    [Fact]
    public async Task CS06_a_failing_deezer_gives_an_empty_list_and_a_log_not_an_exception()
    {
        var rig = new Rig();
        rig.World();
        rig.Deezer.Failure = new DeezerException("/search/artist", DiscoveryFailure.Http, "HTTP 500", 500);

        var result = await rig.GetAsync();

        Assert.Empty(result);
        var failed = Assert.Single(rig.Log.Named("discovery.source_failed"));
        Assert.Equal("deezer", failed.Fields["Source"]);
        Assert.Equal("/search/artist", failed.Fields["Endpoint"]);
        Assert.Equal(500, failed.Fields["Status"]);
        Assert.Single(rig.ListenBrainz.Calls);
    }

    [Fact]
    public async Task CS07_cancellation_propagates()
    {
        var rig = new Rig();
        rig.World();
        rig.Deezer.Hang = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.GetAsync(ct: cts.Token));
        Assert.Empty(rig.Log.Named("discovery.source_failed"));
    }

    [Fact]
    public async Task CS08_a_second_seed_by_the_same_artist_makes_no_deezer_or_listenbrainz_call()
    {
        var rig = new Rig();
        var deezerHttp = new FuncHandler((request, _, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            var json = path switch
            {
                _ when path.StartsWith("/search/artist?q=Radiohead", StringComparison.Ordinal) => """{"data":[{"id":399,"name":"Radiohead","nb_fan":10,"radio":true}]}""",
                _ when path.StartsWith("/search/artist?q=Nirvana", StringComparison.Ordinal) => """{"data":[{"id":6,"name":"Nirvana","nb_fan":10,"radio":true}]}""",
                _ when path.StartsWith("/artist/399/radio", StringComparison.Ordinal) => """{"data":[{"id":1,"title":"Creep","title_short":"Creep","duration":238,"artist":{"id":399,"name":"Radiohead"},"album":{"id":1,"title":"Pablo Honey"}}]}""",
                _ when path.StartsWith("/artist/399/related", StringComparison.Ordinal) => """{"data":[{"id":5,"name":"Muse","nb_fan":10,"radio":true}]}""",
                _ when path.StartsWith("/artist/5/top", StringComparison.Ordinal) => """{"data":[{"id":2,"title":"Hysteria","duration":227,"artist":{"id":5,"name":"Muse"},"album":{"id":2,"title":"Absolution"}}]}""",
                _ when path.StartsWith("/artist/6/top", StringComparison.Ordinal) => """{"data":[{"id":3,"title":"Lithium","duration":257,"artist":{"id":6,"name":"Nirvana"},"album":{"id":3,"title":"Nevermind"}}]}""",
                _ => """{"error":{"type":"DataException","message":"no data","code":800}}""",
            };
            return Task.FromResult(FuncHandler.Json(HttpStatusCode.OK, json));
        });
        var lbHttp = FuncHandler.Always(_ => FuncHandler.Json(HttpStatusCode.OK, """[{"artist_mbid":"5b11f4ce-a62d-471e-81fc-a69a8278c7da","name":"Nirvana","score":11782}]"""));
        var deezer = new DeezerClient(new HttpClient(deezerHttp) { BaseAddress = new Uri("https://api.deezer.com/"), Timeout = Timeout.InfiniteTimeSpan }, rig.Runtime, new ListLogger<DeezerClient>());
        var lb = new ListenBrainzClient(new HttpClient(lbHttp) { BaseAddress = new Uri("https://labs.api.listenbrainz.org/"), Timeout = Timeout.InfiniteTimeSpan }, rig.Runtime, new ListLogger<ListenBrainzClient>());
        var source = rig.Build(deezer, lb);
        var none = new HashSet<Guid>();
        var noKeys = new HashSet<TrackKey>();

        var first = await source.GetAsync(new CandidateQuery(rig.Seed, none, noKeys), CancellationToken.None);
        var deezerCalls = deezerHttp.Calls;
        var lbCalls = lbHttp.Calls;
        var otherSeed = Audio(1001, "Radiohead", "Nude", 255, artistId: SeedArtistId);
        var second = await source.GetAsync(new CandidateQuery(otherSeed, none, noKeys), CancellationToken.None);

        Assert.Equal(["Creep", "Hysteria", "Lithium"], first.Select(r => r.Candidate.Title));
        Assert.Equal(first.Select(r => r.Candidate.Key), second.Select(r => r.Candidate.Key));
        Assert.Equal(6, deezerCalls);
        Assert.Equal(1, lbCalls);
        Assert.Equal(deezerCalls, deezerHttp.Calls);
        Assert.Equal(lbCalls, lbHttp.Calls);
        Assert.Single(rig.Library.ByIdCalls);
    }

    [Fact]
    public async Task CS10_the_artist_mbid_comes_from_provider_ids_and_is_cached_24_hours()
    {
        var rig = new Rig();
        rig.World();

        await rig.GetAsync();
        await rig.GetAsync();
        var (ids, fields) = Assert.Single(rig.Library.ByIdCalls);
        Assert.Equal([Id(SeedArtistId)], ids);
        Assert.Equal("ProviderIds", fields);
        Assert.All(rig.ListenBrainz.Calls, c => Assert.Equal(Mbid, c.Mbid));

        rig.Clock.Advance(TimeSpan.FromHours(25));
        await rig.GetAsync();
        Assert.Equal(2, rig.Library.ByIdCalls.Count);
    }

    [Fact]
    public async Task A_failing_mbid_lookup_skips_listenbrainz_and_is_not_cached()
    {
        var rig = new Rig();
        rig.World();
        await rig.Owned.RefreshAsync(CancellationToken.None);
        rig.Library.Failure = new JellyfinRequestException("GetItemsByIds", 503);
        var source = rig.Build();
        var query = new CandidateQuery(rig.Seed, new HashSet<Guid>(), new HashSet<TrackKey>());

        var result = await source.GetAsync(query, CancellationToken.None);

        Assert.Equal(11, result.Count);
        Assert.Empty(rig.ListenBrainz.Calls);
        Assert.Equal("jellyfin", Assert.Single(rig.Log.Named("discovery.source_failed")).Fields["Source"]);

        rig.Library.Failure = null;
        await source.GetAsync(query, CancellationToken.None);
        Assert.Single(rig.ListenBrainz.Calls);
    }

    [Fact]
    public async Task A_seed_without_an_artist_gives_nothing()
    {
        var rig = new Rig();
        rig.World();
        rig.Seed = rig.Seed with { ArtistItems = [], AlbumArtists = [] };

        Assert.Empty(await rig.GetAsync());
        Assert.Empty(rig.Deezer.Calls);
    }
}
