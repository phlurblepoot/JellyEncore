using System.Text.Json.Nodes;

namespace Encore.Lidarr;

public sealed partial class LidarrClient : IRetentionLidarr
{
    public async Task<IReadOnlyList<LidarrCommand>> GetCommandsAsync(CancellationToken ct)
    {
        var rows = await GetJsonAsync<JsonArray>("api/v1/command", "GetCommands", ct).ConfigureAwait(false);
        var result = new List<LidarrCommand>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            var name = ReadString(row, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var id = row["id"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
            result.Add(new LidarrCommand(id, name, ReadString(row, "status") ?? ""));
        }

        return result;
    }

    public async Task<string> GetWriteAudioTagsAsync(CancellationToken ct)
    {
        var config = await GetJsonAsync<JsonObject>("api/v1/config/metadataprovider", "GetWriteAudioTags", ct).ConfigureAwait(false);
        return ReadString(config, "writeAudioTags")
            ?? throw new LidarrRequestException("GetWriteAudioTags", "response had no writeAudioTags");
    }
}
