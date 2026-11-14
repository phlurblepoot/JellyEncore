using System.Text.Json;
using Encore.Jellyfin;

namespace Encore.Watch;

public enum SessionMessageKind
{
    Sessions,
    KeepAlive,
    ForceKeepAlive,

    /// <summary>A well-formed message of a type Encore does not use.</summary>
    Other,

    /// <summary>Not JSON, no MessageType, or a Sessions message whose Data could not be read.</summary>
    Invalid,
}

/// <summary>One parsed socket message. <see cref="MessageType"/> is safe to log (trimmed); the body never is.</summary>
public sealed record SessionMessage(SessionMessageKind Kind, string MessageType, IReadOnlyList<SessionInfoDto>? Sessions = null)
{
    public const string SessionsStart = """{"MessageType":"SessionsStart","Data":"0,1500"}""";
    public const string KeepAlive = """{"MessageType":"KeepAlive"}""";

    private const int MaxTypeLength = 64;

    /// <summary>Parses a socket text message. Never throws for bad input.</summary>
    public static SessionMessage Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, "MessageType", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return new SessionMessage(SessionMessageKind.Invalid, "unparseable");
            }

            var type = typeElement.GetString() ?? "";
            if (type.Length > MaxTypeLength)
            {
                type = type[..MaxTypeLength];
            }

            switch (type)
            {
                case "KeepAlive":
                    return new SessionMessage(SessionMessageKind.KeepAlive, type);
                case "ForceKeepAlive":
                    return new SessionMessage(SessionMessageKind.ForceKeepAlive, type);
                case "Sessions":
                    if (!TryGetProperty(root, "Data", out var data) || data.ValueKind != JsonValueKind.Array)
                    {
                        return new SessionMessage(SessionMessageKind.Invalid, type);
                    }

                    List<SessionInfoDto>? sessions;
                    try
                    {
                        sessions = data.Deserialize<List<SessionInfoDto>>(JellyfinJson.Options);
                    }
                    catch (JsonException)
                    {
                        sessions = null;
                    }

                    return sessions is null
                        ? new SessionMessage(SessionMessageKind.Invalid, type)
                        : new SessionMessage(SessionMessageKind.Sessions, type, sessions);
                default:
                    return new SessionMessage(SessionMessageKind.Other, type);
            }
        }
        catch (JsonException)
        {
            return new SessionMessage(SessionMessageKind.Invalid, "unparseable");
        }
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
