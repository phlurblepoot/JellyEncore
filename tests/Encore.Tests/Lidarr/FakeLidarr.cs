using System.Net;
using System.Text;

namespace Encore.Tests.Lidarr;

/// <summary>What the client sent, captured at the bottom of the handler chain.</summary>
internal sealed record LidarrSent(
    string Method, string PathAndQuery, string FullUri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public bool IsWrite => Method != "GET";
}

/// <summary>Fake Lidarr: plays back one scripted reply per request and records every request with its headers and body.</summary>
internal sealed class FakeLidarr : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public List<LidarrSent> Sent { get; } = [];

    /// <summary>Every POST, PUT and DELETE, in order.</summary>
    public IEnumerable<LidarrSent> Writes => Sent.Where(s => s.IsWrite);

    public FakeLidarr Reply(HttpStatusCode status, string? json = null) =>
        Add((_, _) => Task.FromResult(Make(status, json)));

    public FakeLidarr Fail(Exception exception) =>
        Add((_, _) => throw exception);

    public FakeLidarr Hang() =>
        Add(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Make(HttpStatusCode.OK);
        });

    private FakeLidarr Add(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join("|", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add(new LidarrSent(request.Method.Method, request.RequestUri!.PathAndQuery, request.RequestUri.ToString(), headers, body));

        if (_script.Count == 0)
        {
            throw new InvalidOperationException("FakeLidarr: no reply scripted for this request");
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

/// <summary>JSON shaped like lidarr-api.md (built from the resource classes, not captured live).</summary>
internal static class LidarrJson
{
    public const string Status = """{"appName":"Lidarr","instanceName":"Lidarr","version":"3.1.6.5079","isDocker":true,"urlBase":"","branch":"master"}""";

    public const string RootFolders = """
        [{"id":1,"name":"Music","path":"/media/music","defaultMetadataProfileId":2,"defaultQualityProfileId":3,"defaultMonitorOption":"all","defaultNewItemMonitorOption":"all","defaultTags":[],"accessible":true,"freeSpace":1,"totalSpace":2}]
        """;

    public const string Artist = """
        {"id":42,"status":"continuing","ended":false,"artistName":"Radiohead","foreignArtistId":"a74b1b7f-71a5-4011-9441-d0b5e4122711","mbId":"a74b1b7f-71a5-4011-9441-d0b5e4122711","artistType":"Group","images":[],"path":"/media/music/Radiohead","qualityProfileId":3,"metadataProfileId":2,"monitored":false,"monitorNewItems":"none","rootFolderPath":"/media/music","folder":"Radiohead","genres":[],"tags":[],"added":"2026-11-15T10:00:00Z","statistics":{"albumCount":0,"trackFileCount":0,"trackCount":0,"totalTrackCount":0,"sizeOnDisk":0,"percentOfTracks":0}}
        """;

    public const string Album = """
        {"id":314,"title":"OK Computer","disambiguation":"","artistId":42,"foreignAlbumId":"b1392450-e666-3926-a536-22c65f834433","monitored":true,"anyReleaseOk":true,"profileId":3,"duration":3210000,"albumType":"Album","secondaryTypes":[],"releaseDate":"1997-05-21T00:00:00Z","releases":[{"id":900,"albumId":314,"foreignReleaseId":"c0b2500e-0cef-4130-869d-732b23ed9df5","title":"OK Computer","status":"Official","trackCount":12,"mediumCount":1,"country":["United Kingdom"],"format":"CD","monitored":true}],"genres":[],"artist":{"id":42,"artistName":"Radiohead","foreignArtistId":"a74b1b7f-71a5-4011-9441-d0b5e4122711","monitored":true},"images":[{"coverType":"cover","url":"/MediaCover/Albums/314/cover.jpg","remoteUrl":"https://example.invalid/cover.jpg"}],"statistics":{"trackFileCount":5,"trackCount":12,"totalTrackCount":12,"sizeOnDisk":1000,"percentOfTracks":41.5}}
        """;

    public const string Lookup = """
        [{"id":0,"title":"OK Computer","disambiguation":"","artistId":0,"foreignAlbumId":"b1392450-e666-3926-a536-22c65f834433","monitored":false,"anyReleaseOk":true,"albumType":"Album","secondaryTypes":["Live"],"releaseDate":"1997-05-21T00:00:00Z","releases":[{"id":0,"albumId":0,"foreignReleaseId":"c0b2500e-0cef-4130-869d-732b23ed9df5","title":"OK Computer","trackCount":12,"format":"CD","monitored":false}],"genres":[],"artist":{"id":0,"artistName":"Radiohead","foreignArtistId":"a74b1b7f-71a5-4011-9441-d0b5e4122711","qualityProfileId":0,"metadataProfileId":0,"monitored":false},"images":[{"coverType":"cover","url":"https://example.invalid/cover.jpg","remoteUrl":"https://example.invalid/cover.jpg"}],"remoteCover":"https://example.invalid/cover.jpg"}]
        """;

    public const string AlreadyAdded = """
        [{"propertyName":"ForeignAlbumId","errorMessage":"This album has already been added.","attemptedValue":"b1392450-e666-3926-a536-22c65f834433","severity":"error","errorCode":"AlbumExistsValidator","formattedMessagePlaceholderValues":{"PropertyName":"Foreign Album Id"}}]
        """;
}
