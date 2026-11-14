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

    /// <summary>v0.2: Deezer artist radio of the seed artist (wire <c>deezer_radio+&lt;copy&gt;</c>).</summary>
    DeezerRadio,

    /// <summary>v0.2: top songs of a Deezer related artist (wire <c>deezer_related+&lt;copy&gt;</c>).</summary>
    DeezerRelated,

    /// <summary>v0.2: top songs of a ListenBrainz similar artist (wire <c>lb_similar+&lt;copy&gt;</c>).</summary>
    ListenBrainzSimilar,
}

/// <summary>Which copy of a song is sent: the one the server already had, or one fetched from Soulseek (v0.2 design 4.5).</summary>
public enum PickCopy
{
    Server,
    Soulseek,
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
        PickSource.DeezerRadio => "deezer_radio",
        PickSource.DeezerRelated => "deezer_related",
        PickSource.ListenBrainzSimilar => "lb_similar",
        _ => "unknown",
    };

    /// <summary>True for the v0.2 outside sources, whose stored name carries the copy (<c>&lt;origin&gt;+&lt;copy&gt;</c>).</summary>
    public static bool IsExternal(this PickSource source) =>
        source is PickSource.DeezerRadio or PickSource.DeezerRelated or PickSource.ListenBrainzSimilar;

    /// <summary>The wire name of a copy: <c>server</c> or <c>soulseek</c>.</summary>
    public static string ToWire(this PickCopy copy) => copy == PickCopy.Soulseek ? "soulseek" : "server";
}

/// <summary>
/// One planned song. <see cref="ArtistId"/> is the first track artist, else the first album artist. <see cref="Copy"/>
/// says whether the server copy is sent or one fetched from Soulseek (v0.1 picks are always the server copy).
/// </summary>
public sealed record PickedItem(Guid Id, string? Name, Guid? ArtistId, string? ArtistName, PickSource Source, PickCopy Copy = PickCopy.Server)
{
    /// <summary>
    /// The stored name (<c>triggers.sources</c>, <c>added_items.source</c>): the v0.1 names for the owned chain (always the
    /// server copy), <c>&lt;origin&gt;+server</c> or <c>&lt;origin&gt;+soulseek</c> for outside suggestions.
    /// </summary>
    public string WireSource() => Source.IsExternal() ? $"{Source.ToWire()}+{Copy.ToWire()}" : Source.ToWire();

    public override string ToString() => $"{Id:N} {ArtistName ?? "?"} - {Name ?? "?"} [{WireSource()}]";
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
