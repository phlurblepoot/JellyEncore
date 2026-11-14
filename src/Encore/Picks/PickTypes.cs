using Encore.Jellyfin;

namespace Encore.Picks;

/// <summary>Where a picked song came from (v0.1 design, section 4). Stored as the wire name in <c>triggers.sources</c>.</summary>
public enum PickSource
{
    /// <summary>A song by an artist similar to a seed artist (primary, and the history-seed step).</summary>
    SimilarArtist,

    /// <summary><c>/Items/{id}/Similar</c> on the seed song, seed artist excluded.</summary>
    SimilarSong,

    /// <summary><c>/Items/{id}/InstantMix</c> on the seed song (genre-based).</summary>
    InstantMix,

    /// <summary>Another song by the seed artist (last resort).</summary>
    SeedArtist,
}

public static class PickSourceNames
{
    /// <summary>The snake_case name used in logs and the store.</summary>
    public static string ToWire(this PickSource source) => source switch
    {
        PickSource.SimilarArtist => "similar_artist",
        PickSource.SimilarSong => "similar_song",
        PickSource.InstantMix => "instant_mix",
        PickSource.SeedArtist => "seed_artist",
        _ => "unknown",
    };
}

/// <summary>One planned song. <see cref="ArtistId"/> is the first track artist, else the first album artist.</summary>
public sealed record PickedItem(Guid Id, string? Name, Guid? ArtistId, string? ArtistName, PickSource Source)
{
    public override string ToString() => $"{Id:N} {ArtistName ?? "?"} - {Name ?? "?"} [{Source.ToWire()}]";
}

/// <summary>A song played or added earlier in this session, used for secondary seeds. Newest first in <see cref="PickRequest.SessionHistory"/>.</summary>
public sealed record HistorySong(Guid ItemId, Guid? ArtistId);

/// <summary>Input to one picker run.</summary>
public sealed record PickRequest
{
    /// <summary>The Jellyfin user the songs are for (library access).</summary>
    public required Guid UserId { get; init; }

    /// <summary>The current song. Its id is always excluded.</summary>
    public required BaseItemDto Seed { get; init; }

    /// <summary>Earlier songs of this session, newest first. Up to 2 distinct artists become secondary seeds.</summary>
    public IReadOnlyList<HistorySong> SessionHistory { get; init; } = [];

    /// <summary>Ids that must not be picked: the queue, the recent window and the remaining plan. The seed is added by the picker.</summary>
    public IReadOnlySet<Guid> Exclusions { get; init; } = new HashSet<Guid>();

    /// <summary>Songs already planned, in order. They count towards the per-artist cap and the back-to-back rule; they are never returned again.</summary>
    public IReadOnlyList<PickedItem> Planned { get; init; } = [];

    /// <summary>How many new songs are wanted.</summary>
    public required int Need { get; init; }
}

/// <summary>Picker output. An empty <see cref="Items"/> is the <c>no_picks</c> outcome, not an error.</summary>
public sealed record PickResult(
    IReadOnlyList<PickedItem> Items,
    int CallsMade,
    int Shortfall,
    IReadOnlyList<PickSource> SourcesTried)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>Seam for the orchestrator's tests.</summary>
public interface IPicker
{
    Task<PickResult> PickAsync(PickRequest request, CancellationToken ct = default);
}

/// <summary>Fixed picker limits (design 1.1, <c>EncoreLimits</c>).</summary>
public static class PickLimits
{
    /// <summary>Most REST calls one pick may make.</summary>
    public const int MaxCalls = 8;

    /// <summary>Most songs by one artist in one plan.</summary>
    public const int MaxPerArtist = 2;

    /// <summary>Shortest song kept (drops interludes). Unknown length is allowed.</summary>
    public static readonly TimeSpan MinLength = TimeSpan.FromSeconds(60);

    /// <summary>Longest song kept (drops long mixes).</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(12);

    public const int SimilarArtistLimit = 10;
    public const int SimilarSongLimit = 30;
    public const int InstantMixLimit = 50;

    /// <summary>Songs fetched per artist-songs call is this times the number still needed.</summary>
    public const int SongsPerNeed = 3;

    /// <summary>Earlier distinct artists from the session tried as secondary seeds.</summary>
    public const int MaxHistorySeeds = 2;
}
