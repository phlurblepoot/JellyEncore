using Encore.Slskd;

namespace Encore.Fetch.Scoring;

/// <summary>
/// Pure scorer: accepts FLAC or MP3 320 files that match the song and ranks peers by estimated time to finish (v0.2 design
/// section 7). With <see cref="FetchPreference.FlacFirst"/> (v0.3 design section 6.2), FLAC files rank before MP3s, each
/// group by that same order, except a FLAC estimated to finish in more than 20 min or after the deadline, which ranks after the MP3s.
/// </summary>
public sealed class Scorer : IScorer
{
    private static readonly string[][] VariantPhrases =
    [
        ["live"], ["remix"], ["rmx"], ["karaoke"], ["instrumental"], ["cover"], ["edit"], ["acapella"],
        ["a", "cappella"], ["nightcore"], ["sped", "up"], ["slowed"], ["tribute"], ["8", "bit"],
    ];

    public ScoreResult Score(ScoreReference reference, IReadOnlyList<SlskdSearchResponse> responses, int maxPeers = ScorerLimits.PeersPerSong)
    {
        var ctx = new Context(reference);
        var rejected = new Dictionary<RejectReason, int>();
        var accepted = new List<ScoredFile>();
        var seen = 0;
        foreach (var peer in responses)
        {
            foreach (var file in peer.Files)
            {
                seen++;
                var (scored, reason) = Evaluate(ctx, peer, file);
                if (scored is not null) accepted.Add(scored);
                else rejected[reason!.Value] = rejected.GetValueOrDefault(reason.Value) + 1;
            }
        }

        if (reference.Preference == FetchPreference.FlacFirst)
        {
            var secondsLeft = reference.SecondsLeft;
            accepted.Sort((a, b) =>
            {
                var g = FlacFirstGroup(a, secondsLeft).CompareTo(FlacFirstGroup(b, secondsLeft));
                return g != 0 ? g : Compare(a, b);
            });
        }
        else
        {
            accepted.Sort(Compare);
        }
        var ranked = new List<ScoredFile>();
        var peers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in accepted)
        {
            if (ranked.Count >= maxPeers) break;
            if (peers.Add(f.Username)) ranked.Add(f);
        }
        return new ScoreResult(ranked, rejected, seen);
    }

    /// <summary>FlacFirst groups: 0 a FLAC that finishes in time, 1 an MP3, 2 a FLAC that is too slow or past the deadline.</summary>
    private static int FlacFirstGroup(ScoredFile f, double? secondsLeft)
    {
        if (f.Format != AudioFormat.Flac)
        {
            return 1;
        }

        var tooSlow = f.EtaSeconds > ScorerLimits.FlacFirstMaxEtaSeconds || (secondsLeft is { } left && f.EtaSeconds > left);
        return tooSlow ? 2 : 0;
    }

    private static int Compare(ScoredFile a, ScoredFile b)
    {
        var c = a.EtaSeconds.CompareTo(b.EtaSeconds);
        if (c != 0) return c;
        c = a.File.Size.CompareTo(b.File.Size);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Username, b.Username);
        return c != 0 ? c : string.CompareOrdinal(a.File.Filename, b.File.Filename);
    }

    private sealed class Context
    {
        public Context(ScoreReference r)
        {
            Duration = r.DurationS is > 0 ? r.DurationS : null;
            TitleTokens = ScoringText.TitleKeyTokens(r.Title);
            ArtistTokens = ScoringText.ArtistKeyTokens(r.Artist);
            var raw = ScoringText.Tokens(r.Title);
            raw.AddRange(ScoringText.Tokens(r.Album));
            RefTokens = raw;
        }

        public int? Duration { get; }
        public List<string> TitleTokens { get; }
        public List<string> ArtistTokens { get; }
        public List<string> RefTokens { get; }
    }

    private static (ScoredFile?, RejectReason?) Evaluate(Context ctx, SlskdSearchResponse peer, SlskdFile file)
    {
        var segments = file.Filename.Split('\\', '/');
        var name = segments[^1];

        // 1 format, from the file name
        var dot = name.LastIndexOf('.');
        var ext = dot >= 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
        AudioFormat format;
        if (ext == "flac") format = AudioFormat.Flac;
        else if (ext == "mp3") format = AudioFormat.Mp3;
        else return (null, RejectReason.Format);

        // 2 queue
        if (peer.QueueLength > ScorerLimits.MaxQueueLength) return (null, RejectReason.Queue);

        var length = file.Length is > 0 ? file.Length : null;
        var size = file.Size;
        int? kbps = null;

        // 3 bitrate (MP3)
        if (format == AudioFormat.Mp3)
        {
            if (file.BitRate is int br && br < 320) return (null, RejectReason.Bitrate);
            if (file.BitRate == 320 && file.IsVariableBitRate != true)
            {
                kbps = 320;
            }
            else
            {
                var len = length ?? ctx.Duration;
                if (len is null || size <= 0) return (null, RejectReason.Bitrate);
                var est = (int)(size * 8.0 / 1000 / len.Value);
                if (est < ScorerLimits.Mp3MinKbps) return (null, RejectReason.Bitrate);
                kbps = est;
            }
        }

        // 4 length against the reference
        if (length is not null && ctx.Duration is not null
            && Math.Abs(length.Value - ctx.Duration.Value) > ScorerLimits.LengthToleranceS)
            return (null, RejectReason.Length);

        // 5 size
        if (format == AudioFormat.Mp3)
        {
            var d = ctx.Duration ?? length;
            if (d is not null)
            {
                var upper = 42_000L * d.Value + 3_000_000;
                var lower = length is null ? 36_000L * d.Value : 0;
                if (size < lower || size > upper) return (null, RejectReason.Size);
            }
        }
        else if (length is null && ctx.Duration is int d)
        {
            if (size < 43_750L * d || size > 750_000L * d + 5_000_000) return (null, RejectReason.Size);
        }

        // 6 tokens
        var path = ScoringText.Tokens(file.Filename);
        if (!TokensMatch(ctx, path)) return (null, RejectReason.Tokens);

        // 7 variant words in the file name or its parent folder
        // keep the name and the folder apart so a phrase never spans them
        var nameTokens = ScoringText.Tokens(name[..(dot >= 0 ? dot : name.Length)]);
        var folderTokens = segments.Length > 1 ? ScoringText.Tokens(segments[^2]) : [];
        foreach (var phrase in VariantPhrases)
        {
            var inFile = ScoringText.ContainsPhrase(nameTokens, phrase) || ScoringText.ContainsPhrase(folderTokens, phrase);
            if (inFile && !ScoringText.ContainsPhrase(ctx.RefTokens, phrase)) return (null, RejectReason.Variant);
        }

        // 8 FLAC sanity: a real FLAC is well above 350 kbps
        if (format == AudioFormat.Flac)
        {
            var len = length ?? ctx.Duration;
            if (len is not null && size > 0) kbps = (int)(size * 8.0 / 1000 / len.Value);
            if (length is not null && kbps < ScorerLimits.FlacMinKbps) return (null, RejectReason.Size);
        }

        var free = peer.HasFreeUploadSlot;
        var wait = free ? 0 : 60 + 30.0 * peer.QueueLength;
        var speed = Math.Max(peer.UploadSpeed, ScorerLimits.MinSpeedBytesPerSecond);
        var eta = wait + (double)size / speed;
        return (new ScoredFile(peer.Username, file, format, kbps, free, peer.QueueLength, eta), null);
    }

    private static bool TokensMatch(Context ctx, List<string> path)
    {
        var set = new HashSet<string>(path, StringComparer.Ordinal);

        var titleLong = ctx.TitleTokens.Where(t => t.Length >= 2).ToList();
        var titleOk = ctx.TitleTokens.Count > 0
            && (titleLong.Count > 0 ? titleLong.All(set.Contains) : ScoringText.ContainsPhrase(path, ctx.TitleTokens));
        if (!titleOk) return false;

        var artistLong = ctx.ArtistTokens.Where(t => t.Length >= 3).ToList();
        return ctx.ArtistTokens.Count > 0
            && (artistLong.Count > 0 ? artistLong.Any(set.Contains) : ScoringText.ContainsPhrase(path, ctx.ArtistTokens));
    }
}
