using System.Net;
using System.Text;
using Encore.Jellyfin;

namespace Encore.Tests.Jellyfin;

/// <summary>What the client sent, captured when the request reached the bottom of the handler chain.</summary>
internal sealed record SentRequest(string Method, string PathAndQuery, string? Authorization, IReadOnlyList<string> HeaderNames, string? Body = null, string? ContentType = null);

/// <summary>Fake Jellyfin: plays back one scripted reply per request and records every request.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public List<SentRequest> Sent { get; } = [];

    public ScriptedHandler Reply(HttpStatusCode status, string? json = null) =>
        Add((_, _) => Task.FromResult(Make(status, json)));

    public ScriptedHandler Fail(Exception exception) =>
        Add((_, _) => throw exception);

    /// <summary>Never answers. Only the HttpClient timeout or the caller's token ends it.</summary>
    public ScriptedHandler Hang() =>
        Add(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Make(HttpStatusCode.OK);
        });

    private ScriptedHandler Add(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sent.Add(new SentRequest(
            request.Method.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.Contains("Authorization") ? string.Join("|", request.Headers.GetValues("Authorization")) : null,
            request.Headers.Select(h => h.Key).ToList(),
            request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult(),
            request.Content?.Headers.ContentType?.ToString()));

        if (_script.Count == 0)
        {
            throw new InvalidOperationException("ScriptedHandler: no reply scripted for this request");
        }

        return _script.Dequeue()(request, cancellationToken);
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
