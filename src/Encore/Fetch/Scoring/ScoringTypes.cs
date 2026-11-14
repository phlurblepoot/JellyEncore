using Encore.Slskd;

namespace Encore.Fetch.Scoring;

/// <summary>The song being looked for. <see cref="DurationS"/> is null when unknown.</summary>
public sealed record ScoreReference(string Artist, string Title, string? Album, int? DurationS);

/// <summary>Why a file was not accepted (the first failing rule, v0.2 design section 7.1).</summary>
public enum RejectReason { Format, Queue, Bitrate, Length, Size, Tokens, Variant }

public enum AudioFormat { Flac, Mp3 }

/// <summary>One accepted file with its peer and the estimated time to finish. <see cref="Kbps"/> is null when it cannot be told.</summary>
public sealed record ScoredFile(
    string Username, SlskdFile File, AudioFormat Format, int? Kbps, bool HasFreeUploadSlot, long QueueLength, double EtaSeconds);

/// <summary><see cref="Ranked"/>: best first, one file per peer. <see cref="Rejected"/> counts by reason; Seen = accepted files + rejected files.</summary>
public sealed record ScoreResult(IReadOnlyList<ScoredFile> Ranked, IReadOnlyDictionary<RejectReason, int> Rejected, int FilesSeen)
{
    public int RejectedTotal => Rejected.Values.Sum();
}

public interface IScorer
{
    ScoreResult Score(ScoreReference reference, IReadOnlyList<SlskdSearchResponse> responses, int maxPeers = ScorerLimits.PeersPerSong);
}

/// <summary>Fixed scoring constants (v0.2 design section 7).</summary>
public static class ScorerLimits
{
    public const int PeersPerSong = 3;
    public const int MaxQueueLength = 50;
    public const int LengthToleranceS = 3;
    public const int MinSpeedBytesPerSecond = 51_200;
    public const int Mp3MinKbps = 300;
    public const int FlacMinKbps = 350;
}
