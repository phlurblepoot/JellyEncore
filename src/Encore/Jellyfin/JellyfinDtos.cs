using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encore.Jellyfin;

// Typed subset of Jellyfin 12.4 DTOs (fixtures in tests/Encore.Tests/Jellyfin/JellyfinFixtures.cs, source notes in .knowledge/encore/api.md).
// Names are PascalCase like the server's JSON. Unknown fields are ignored. Ids are Guids, which read both dashed and N forms.
// Session Id and PlaylistItemId stay strings, as the server sends them.

public sealed record SessionInfoDto
{
    public string Id { get; init; } = "";

    public Guid UserId { get; init; }

    public string? UserName { get; init; }

    public string? Client { get; init; }

    public string? DeviceName { get; init; }

    public string? DeviceId { get; init; }

    public bool SupportsRemoteControl { get; init; }

    public BaseItemDto? NowPlayingItem { get; init; }

    /// <summary>
    /// Null means the client sent no queue information. An empty list is a real empty queue.
    /// The trigger rules treat these differently, so do not collapse them.
    /// </summary>
    public IReadOnlyList<QueueItem>? NowPlayingQueue { get; init; }

    public string? PlaylistItemId { get; init; }

    public PlayerStateInfo? PlayState { get; init; }

    public DateTimeOffset? LastActivityDate { get; init; }
}

public sealed record QueueItem
{
    public Guid Id { get; init; }

    public string? PlaylistItemId { get; init; }
}

public sealed record PlayerStateInfo
{
    public long? PositionTicks { get; init; }

    public bool IsPaused { get; init; }

    [JsonConverter(typeof(TolerantEnumConverter<JellyfinRepeatMode>))]
    public JellyfinRepeatMode RepeatMode { get; init; }

    [JsonConverter(typeof(TolerantEnumConverter<JellyfinPlaybackOrder>))]
    public JellyfinPlaybackOrder PlaybackOrder { get; init; }
}

public enum JellyfinRepeatMode
{
    /// <summary>A value this version does not know, or none sent. Treated as not-repeating-all by callers that check the known values.</summary>
    Unknown = 0,
    RepeatNone,
    RepeatOne,
    RepeatAll,
}

public enum JellyfinPlaybackOrder
{
    Unknown = 0,
    Default,
    Shuffle,
}

public sealed record BaseItemDto
{
    public Guid Id { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    public string? MediaType { get; init; }

    public long? RunTimeTicks { get; init; }

    public Guid? AlbumId { get; init; }

    public string? Album { get; init; }

    /// <summary>The album artist's name (Jellyfin sends it on songs and albums).</summary>
    public string? AlbumArtist { get; init; }

    public int? ProductionYear { get; init; }

    public IReadOnlyList<NameGuidPair>? ArtistItems { get; init; }

    public IReadOnlyList<NameGuidPair>? AlbumArtists { get; init; }

    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>The file path as Jellyfin sees it (only when fields=Path was asked for).</summary>
    public string? Path { get; init; }

    /// <summary>When the item was added (only when fields=DateCreated was asked for).</summary>
    public DateTimeOffset? DateCreated { get; init; }

    /// <summary>External ids, e.g. "MusicBrainzArtist" on an artist (only when fields=ProviderIds was asked for).</summary>
    public IReadOnlyDictionary<string, string>? ProviderIds { get; init; }
}

public sealed record NameGuidPair
{
    public Guid Id { get; init; }

    public string? Name { get; init; }
}

public sealed record UserDto
{
    public Guid Id { get; init; }

    public string? Name { get; init; }

    public UserPolicy? Policy { get; init; }
}

public sealed record UserPolicy
{
    public bool IsAdministrator { get; init; }

    public bool IsDisabled { get; init; }

    public bool IsHidden { get; init; }

    /// <summary>Failed logins before Jellyfin disables the user; null, 0 or negative = no limit (Jellyfin default -1).</summary>
    public int? LoginAttemptsBeforeLockout { get; init; }
}

public sealed record QueryResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];

    public int TotalRecordCount { get; init; }
}

/// <summary>Reads enums as strings and maps any unknown or missing value to the enum's zero member (<c>Unknown</c>), so a new server value never breaks a session poll.</summary>
internal sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (text is not null && Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value))
            {
                return value;
            }

            return default;
        }

        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Jellyfin writes ids as 32 hex digits without dashes. System.Text.Json's default Guid reader accepts only the dashed form, so this reader takes both.</summary>
internal sealed class JellyfinGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id))
        {
            return id;
        }

        throw new JsonException("expected a Jellyfin id");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("N"));
}

internal static class JellyfinJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JellyfinGuidConverter());
        return options;
    }
}

/// <summary>One library from GET /Library/VirtualFolders. <see cref="Locations"/> are paths inside the Jellyfin container.</summary>
public sealed record VirtualFolderInfo
{
    public string? Name { get; init; }

    public IReadOnlyList<string>? Locations { get; init; }

    /// <summary>Lowercase, e.g. "music"; null for a mixed library.</summary>
    public string? CollectionType { get; init; }

    /// <summary>The library's item id, used as parentId in /Items.</summary>
    public Guid ItemId { get; init; }
}

/// <summary>GET /System/Info/Public: what Jellyfin tells anyone. <see cref="Id"/> is the server id (the "serverId" of a details link).</summary>
public sealed record PublicSystemInfo
{
    public string? Id { get; init; }

    public string? ServerName { get; init; }

    public string? Version { get; init; }
}
