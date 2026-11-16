using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Encore.Slskd;

public sealed partial class SlskdClient : IRetentionSlskd
{
    private const string UploadFileCompleteType = "UploadFileComplete";

    public async Task<SlskdEventsPage> GetEventsAsync(int offset, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        using var response = await SendAsync(HttpMethod.Get, $"api/v0/events?offset={offset}&limit={limit}", "GetEvents", null, retryServerError: true, ct).ConfigureAwait(false);
        int? total = null;
        if (response!.Headers.TryGetValues("X-Total-Count", out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var t))
        {
            total = t;
        }

        var records = await ReadAsync<JsonArray>(response, "GetEvents", ct).ConfigureAwait(false);
        var uploads = new List<SlskdUploadEvent>();
        DateTimeOffset? oldest = null;
        var count = 0;
        foreach (var node in records)
        {
            if (node is not JsonObject record)
            {
                continue;
            }

            count++;
            var stamp = ReadTime(record["timestamp"]);
            if (stamp is { } s && (oldest is null || s < oldest))
            {
                oldest = s;
            }

            if (ReadString(record["type"]) != UploadFileCompleteType || stamp is null)
            {
                continue;
            }

            // "data" is a JSON-encoded string: a second parse. Only four fields are read, nothing is bound strictly
            // (live "requestedAt" has no "Z" while the other times do).
            if (ParseUpload(record["data"], stamp.Value) is { } upload)
            {
                uploads.Add(upload);
            }
        }

        return new SlskdEventsPage(uploads, count, oldest, total);
    }

    public async Task<IReadOnlyList<SlskdUpload>> GetUploadsAsync(CancellationToken ct)
    {
        var users = await GetJsonAsync<JsonArray>("api/v0/transfers/uploads", "GetUploads", ct).ConfigureAwait(false);
        var result = new List<SlskdUpload>();
        foreach (var user in users.OfType<JsonObject>())
        {
            var userName = ReadString(user["username"]) ?? "";
            foreach (var directory in (user["directories"] as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var file in (directory["files"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (!Guid.TryParse(ReadString(file["id"]), out var id))
                    {
                        continue;
                    }

                    result.Add(new SlskdUpload(id, ReadString(file["username"]) ?? userName, ReadString(file["filename"]) ?? "", ReadString(file["state"]) ?? ""));
                }
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<SlskdShare>> GetSharesAsync(CancellationToken ct)
    {
        var hosts = await GetJsonAsync<JsonObject>("api/v0/shares", "GetShares", ct).ConfigureAwait(false);
        var result = new List<SlskdShare>();
        foreach (var (host, list) in hosts)
        {
            foreach (var share in (list as JsonArray ?? []).OfType<JsonObject>())
            {
                result.Add(new SlskdShare(
                    host, ReadString(share["id"]) ?? "", ReadString(share["alias"]) ?? "",
                    share["isExcluded"] is JsonValue ex && ex.TryGetValue<bool>(out var excluded) && excluded,
                    ReadString(share["localPath"]) ?? "", ReadString(share["remotePath"]) ?? "",
                    ReadCount(share["directories"]), ReadCount(share["files"])));
            }
        }

        return result;
    }

    public async Task<SlskdShareState> GetShareStateAsync(CancellationToken ct)
    {
        var app = await GetJsonAsync<ApplicationSharesDto>("api/v0/application", "GetShareState", ct).ConfigureAwait(false);
        var s = app.Shares ?? throw new SlskdRequestException("GetShareState", "response had no share state");
        return new SlskdShareState(s.Scanning, s.ScanPending, s.Ready, s.Faulted);
    }

    public async Task RescanSharesAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, "api/v0/shares", "RescanShares", null, retryServerError: false, ct).ConfigureAwait(false);
    }

    private static SlskdUploadEvent? ParseUpload(JsonNode? data, DateTimeOffset stamp)
    {
        // The documented shape is a string holding JSON; an object is accepted too.
        JsonNode? root;
        try
        {
            root = data is JsonValue v && v.TryGetValue<string>(out var text) ? JsonNode.Parse(text) : data;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj || obj["transfer"] is not JsonObject transfer)
        {
            return null;
        }

        var local = ReadString(obj["localFilename"]);
        var user = ReadString(transfer["username"]);
        if (string.IsNullOrEmpty(local) || string.IsNullOrEmpty(user) || !Guid.TryParse(ReadString(transfer["id"]), out var id))
        {
            return null;
        }

        return new SlskdUploadEvent(id, user, local, stamp);
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int ReadCount(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    private static DateTimeOffset? ReadTime(JsonNode? node) =>
        ReadString(node) is { } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : null;

    private sealed record ApplicationSharesDto(ShareStateDto? Shares);

    private sealed record ShareStateDto(bool ScanPending, bool Scanning, bool Ready, bool Faulted);
}
