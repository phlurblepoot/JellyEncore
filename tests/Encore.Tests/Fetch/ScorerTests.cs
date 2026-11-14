using Encore.Fetch.Scoring;
using Encore.Slskd;
using Xunit;

namespace Encore.Tests.Fetch;

public class ScorerTests
{
    private const string Dir = @"@@a\Music\Radiohead\In Rainbows\";
    private static readonly ScoreReference Reckoner = new("Radiohead", "Reckoner", "In Rainbows", 290);
    private static readonly ScoreReference NoDuration = new("Radiohead", "Reckoner", null, null);
    private static readonly Scorer Subject = new();

    private static SlskdFile File(string path, long size, int? bitRate = null, bool? vbr = null, int? length = null, string ext = "")
        => new(path, size, bitRate, vbr, length, null, null, ext);

    private static SlskdSearchResponse Peer(string user, SlskdFile file, bool free = true, long queue = 0, long speed = 1_000_000)
        => new(user, free, speed, queue, [file]);

    private static ScoreResult One(ScoreReference r, SlskdFile f, bool free = true, long queue = 0)
        => Subject.Score(r, [Peer("bob", f, free, queue)]);

    private static void Expect(ScoreResult result, RejectReason? reason)
    {
        if (reason is null)
        {
            Assert.Single(result.Ranked);
            Assert.Equal(0, result.RejectedTotal);
        }
        else
        {
            Assert.Empty(result.Ranked);
            Assert.Equal(1, result.Rejected.GetValueOrDefault(reason.Value));
        }
        Assert.Equal(result.FilesSeen, result.Ranked.Count + result.RejectedTotal);
    }

    public static IEnumerable<object?[]> Cases()
    {
        // path, size, bitRate, vbr, length, ext, expected reject (null = accept)
        yield return [Dir + "07 Reckoner.flac", 32_000_000L, null, null, 290, "flac", null];                    // SC01
        yield return [Dir + "07 Reckoner.mp3", 11_600_000L, 320, false, 291, "mp3", null];                      // SC02
        yield return [Dir + "07 Reckoner.mp3", 9_300_000L, 256, false, 290, "mp3", RejectReason.Bitrate];       // SC03
        yield return [Dir + "07 Reckoner.mp3", 11_600_000L, null, null, null, "mp3", null];                     // SC04
        yield return [Dir + "07 Reckoner.mp3", 8_900_000L, 320, true, 290, "mp3", RejectReason.Bitrate];        // SC05
        foreach (var e in new[] { "m4a", "ogg", "wav", "wma", "aac" })                                          // SC06
            yield return [Dir + "07 Reckoner." + e, 32_000_000L, null, null, 290, e, RejectReason.Format];
        yield return [Dir + "07 Reckoner.flac", 32_000_000L, null, null, 294, "flac", RejectReason.Length];     // SC07
        yield return [Dir + "07 Reckoner.flac", 32_000_000L, null, null, 287, "flac", null];                    // SC08
        yield return [Dir + "07 Reckoner.flac", 1_000_000L, null, null, null, "flac", RejectReason.Size];       // SC09
        yield return [Dir + "07 Reckoner.flac", 30_000_000L, null, null, null, "flac", null];                   // SC10
        yield return [@"@@a\Music\In Rainbows\07 Reckoner.flac", 32_000_000L, null, null, 290, "flac", RejectReason.Tokens]; // SC11
        yield return [Dir + "07 Track07.flac", 32_000_000L, null, null, 290, "flac", RejectReason.Tokens];      // SC12
        yield return [Dir + "07 Reckoner (Live).flac", 32_000_000L, null, null, 290, "flac", RejectReason.Variant]; // SC13
        yield return [Dir + "Reckoner (Remix).flac", 32_000_000L, null, null, 290, "flac", RejectReason.Variant];  // SC15
        yield return [Dir + "Reckoner - Karaoke Version.mp3", 11_600_000L, 320, false, 290, "mp3", RejectReason.Variant]; // SC15
        yield return [@"@@a\Radiohead - Live at the Basement\07 Reckoner.flac", 32_000_000L, null, null, 290, "flac", RejectReason.Variant]; // SC16
        yield return [@"@@a\Live Collection\Radiohead - In Rainbows\07 Reckoner.flac", 32_000_000L, null, null, 290, "flac", null]; // SC17
        yield return [Dir + "07 Reckoner.mp3", 21_000_000L, 320, false, 290, "mp3", RejectReason.Size];        // SC26
        yield return [Dir + "07 Reckoner.FLAC", 32_000_000L, null, null, 290, "", null];                        // SC27
        yield return [Dir + "07 Reckoner.flac", 1_000_000L, null, null, 290, "flac", RejectReason.Size];        // FLAC far below 350 kbps
        yield return [Dir + "07 Reckoner.mp3", 11_600_000L, null, true, null, "mp3", null];                      // VBR, estimate 320 from the reference duration
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Scorer_table(string path, long size, int? bitRate, bool? vbr, int? length, string ext, RejectReason? expected)
        => Expect(One(Reckoner, File(path, size, bitRate, vbr, length, ext)), expected);

    [Fact]
    public void Scorer_SC14_live_allowed_when_the_reference_is_live()
    {
        var r = new ScoreReference("Radiohead", "Reckoner (Live)", null, 290);
        Expect(One(r, File(Dir + "Reckoner (Live).flac", 32_000_000, length: 290, ext: "flac")), null);
    }

    [Fact]
    public void Scorer_SC18_long_queue_rejected()
        => Expect(One(Reckoner, File(Dir + "07 Reckoner.flac", 32_000_000, length: 290), free: false, queue: 80), RejectReason.Queue);

    [Fact]
    public void Scorer_queue_of_exactly_50_is_ok()
        => Expect(One(Reckoner, File(Dir + "07 Reckoner.flac", 32_000_000, length: 290), free: false, queue: 50), null);

    [Theory]
    [InlineData("Queen", "Don't Stop Me Now", @"Queen\Dont Stop Me Now.mp3")]            // SC25
    [InlineData("Beyoncé", "Halo", @"Beyonce\Halo.mp3")]                                   // SC25
    [InlineData("The Beatles", "Help! (Remastered 2009)", @"Beatles\Help.mp3")]
    [InlineData("Daft Punk", "Get Lucky (feat. Pharrell Williams)", @"Daft Punk\Get Lucky.mp3")]
    [InlineData("Simon & Garfunkel", "Mrs. Robinson - Remastered", @"Simon and Garfunkel\Mrs Robinson.mp3")]
    public void Scorer_SC25_names_are_folded(string artist, string title, string path)
        => Expect(One(new ScoreReference(artist, title, null, 290), File(path, 11_600_000, 320, false, 290)), null);

    [Fact]
    public void Scorer_SC28_unknown_reference_duration_skips_the_length_rule()
    {
        var r = new ScoreReference("Radiohead", "Reckoner", null, null);
        Expect(One(r, File(Dir + "07 Reckoner.flac", 32_000_000, length: 300, ext: "flac")), null);
    }

    [Fact]
    public void Scorer_SC19_free_slot_beats_a_shorter_wait_with_a_bigger_file()
    {
        var a = Peer("a", File(Dir + "Reckoner.flac", 30_000_000, length: 290), free: true);
        var b = Peer("b", File(Dir + "Reckoner.mp3", 12_000_000, 320, false, 290), free: false, queue: 0);
        var r = Subject.Score(Reckoner, [b, a]);
        Assert.Equal(["a", "b"], r.Ranked.Select(x => x.Username));
        Assert.Equal(30, r.Ranked[0].EtaSeconds, 3);
        Assert.Equal(72, r.Ranked[1].EtaSeconds, 3);
    }

    [Fact]
    public void Scorer_SC20_format_is_neutral()
    {
        var mp3 = Peer("m", File(Dir + "Reckoner.mp3", 12_000_000, 320, false, 290), speed: 500_000);
        var flac = Peer("f", File(Dir + "Reckoner.flac", 32_000_000, length: 290), speed: 500_000);
        var r = Subject.Score(Reckoner, [flac, mp3]);
        Assert.Equal(["m", "f"], r.Ranked.Select(x => x.Username));
        Assert.Equal(24, r.Ranked[0].EtaSeconds, 3);
        Assert.Equal(64, r.Ranked[1].EtaSeconds, 3);
    }

    [Fact]
    public void Scorer_SC21_shorter_queue_first()
    {
        var f = File(Dir + "Reckoner.flac", 30_000_000, length: 290);
        var r = Subject.Score(Reckoner, [Peer("a", f, false, 10), Peer("b", f, false, 2)]);
        Assert.Equal(["b", "a"], r.Ranked.Select(x => x.Username));
    }

    [Fact]
    public void Scorer_SC22_ties_smaller_size_then_username()
    {
        // equal ETA (10 s) with different sizes
        var small = Peer("zed", File(Dir + "Reckoner.flac", 10_000_000), speed: 1_000_000);
        var big = Peer("amy", File(Dir + "Reckoner.flac", 12_000_000), speed: 1_200_000);
        Assert.Equal(["zed", "amy"], Subject.Score(NoDuration, [big, small]).Ranked.Select(x => x.Username));

        var f = File(Dir + "Reckoner.flac", 30_000_000, length: 290);
        var r = Subject.Score(Reckoner, [Peer("bob", f), Peer("alice", f)]);
        Assert.Equal(["alice", "bob"], r.Ranked.Select(x => x.Username));
    }

    [Fact]
    public void Scorer_SC23_one_entry_per_peer_with_the_lower_eta()
    {
        var peer = new SlskdSearchResponse("bob", true, 1_000_000, 0,
            [File(Dir + "Reckoner.flac", 32_000_000, length: 290), File(Dir + "Reckoner.mp3", 12_000_000, 320, false, 290)]);
        var r = Subject.Score(Reckoner, [peer]);
        var only = Assert.Single(r.Ranked);
        Assert.Equal(AudioFormat.Mp3, only.Format);
        Assert.Equal(2, r.FilesSeen);
    }

    [Fact]
    public void Scorer_SC24_zero_speed_uses_the_floor()
    {
        var r = Subject.Score(NoDuration, [Peer("bob", File(Dir + "Reckoner.flac", 10_000_000), speed: 0)]);
        Assert.Equal(10_000_000.0 / 51_200, r.Ranked[0].EtaSeconds, 6);
    }

    [Fact]
    public void Scorer_keeps_at_most_the_top_peers()
    {
        var f = File(Dir + "Reckoner.flac", 30_000_000, length: 290);
        var peers = Enumerable.Range(0, 5).Select(i => Peer($"u{i}", f, false, i)).ToList();
        Assert.Equal(3, Subject.Score(Reckoner, peers).Ranked.Count);
        Assert.Equal(5, Subject.Score(Reckoner, peers, 10).Ranked.Count);
    }

    [Fact]
    public void Scorer_reject_counts_sum_to_the_files_seen_and_order_is_deterministic()
    {
        var files = new[]
        {
            File(Dir + "07 Reckoner.flac", 32_000_000, length: 290),
            File(Dir + "07 Reckoner.ogg", 5_000_000, length: 290),
            File(Dir + "07 Reckoner (Live).flac", 32_000_000, length: 290),
            File(Dir + "07 Other.flac", 32_000_000, length: 290),
        };
        var peers = new List<SlskdSearchResponse>();
        for (var i = 0; i < 6; i++)
            peers.Add(new SlskdSearchResponse($"p{i}", i % 2 == 0, 100_000 * (i + 1), i, files));
        var baseline = Subject.Score(Reckoner, peers, 10);
        Assert.Equal(24, baseline.FilesSeen);
        Assert.Equal(6, baseline.Ranked.Count);
        Assert.Equal(18, baseline.RejectedTotal);
        Assert.Equal(6, baseline.Rejected[RejectReason.Format]);
        Assert.Equal(6, baseline.Rejected[RejectReason.Variant]);
        Assert.Equal(6, baseline.Rejected[RejectReason.Tokens]);

        var rng = new Random(7);
        for (var round = 0; round < 10; round++)
        {
            var shuffled = peers.OrderBy(_ => rng.Next()).ToList();
            var again = Subject.Score(Reckoner, shuffled, 10);
            Assert.Equal(baseline.Ranked.Select(x => x.Username), again.Ranked.Select(x => x.Username));
        }
    }

    [Fact]
    public void Scorer_empty_input()
    {
        var r = Subject.Score(Reckoner, []);
        Assert.Empty(r.Ranked);
        Assert.Equal(0, r.FilesSeen);
    }
}
