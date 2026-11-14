using System.Net;
using System.Text;

namespace Encore.Tests.Slskd;

/// <summary>What the client sent, captured at the bottom of the handler chain.</summary>
internal sealed record SlskdSent(
    string Method, string PathAndQuery, string FullUri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>Fake slskd: plays back one scripted reply per request and records every request with its headers and body.</summary>
internal sealed class SlskdHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public List<SlskdSent> Sent { get; } = [];

    public SlskdHandler Reply(HttpStatusCode status, string? json = null) =>
        Add((_, _) => Task.FromResult(Make(status, json)));

    public SlskdHandler Fail(Exception exception) =>
        Add((_, _) => throw exception);

    public SlskdHandler Hang() =>
        Add(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Make(HttpStatusCode.OK);
        });

    private SlskdHandler Add(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join("|", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add(new SlskdSent(request.Method.Method, request.RequestUri!.PathAndQuery, request.RequestUri.ToString(), headers, body));

        if (_script.Count == 0)
        {
            throw new InvalidOperationException("SlskdHandler: no reply scripted for this request");
        }

        return await _script.Dequeue()(request, cancellationToken);
    }

    public static HttpResponseMessage Make(HttpStatusCode status, string? json = null)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return response;
    }
}

internal static class SlskdJson
{
    public const string Search = """
        { "id": "6f1c0000-0000-0000-0000-000000000001", "searchText": "x", "token": 1, "state": "Completed, TimedOut",
          "isComplete": true, "responseCount": 2, "fileCount": 3, "lockedFileCount": 0, "responses": [] }
        """;

    public const string Responses = """
        [ { "username": "peer1", "hasFreeUploadSlot": true, "uploadSpeed": 1048576, "queueLength": 0, "token": 1,
            "fileCount": 2, "lockedFileCount": 0, "lockedFiles": [],
            "files": [
              { "filename": "@@abc\\Music\\Radiohead\\In Rainbows\\04 Weird Fishes.FLAC", "size": 31457280, "bitRate": null,
                "isVariableBitRate": null, "length": 318, "sampleRate": 44100, "bitDepth": 16, "extension": "", "code": 1, "isLocked": false },
              { "filename": "@@abc\\Music\\x\\01 a.mp3", "size": 5000000, "bitRate": 320, "isVariableBitRate": false, "length": 200,
                "sampleRate": null, "bitDepth": null, "extension": "mp3", "code": 1, "isLocked": false } ] } ]
        """;

    public const string Enqueued = """
        { "batch": { "id": "b7a00000-0000-0000-0000-000000000002", "username": "peer1", "direction": "Download",
            "transfers": [ { "id": "0d9e0000-0000-0000-0000-000000000003", "state": "Requested" } ] }, "failures": [] }
        """;

    public const string Transfer = """
        { "id": "0d9e0000-0000-0000-0000-000000000003", "username": "peer1", "direction": "Download",
          "filename": "@@abc\\Music\\x\\01 a.mp3", "size": 5000000, "state": "Completed, Succeeded",
          "bytesTransferred": 5000000, "averageSpeed": 524288.0, "placeInQueue": null, "attempts": 1,
          "nextAttemptAt": "2026-11-14T22:00:00Z", "removed": false }
        """;
}
