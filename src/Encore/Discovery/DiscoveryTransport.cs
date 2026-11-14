using System.Net.Http.Headers;

namespace Encore.Discovery;

/// <summary>One finished HTTP exchange: status, headers and the whole body, read inside the call's timeout.</summary>
internal sealed record RawResponse(int Status, HttpResponseHeaders Headers, string Body);

internal static class DiscoveryTransport
{
    /// <summary>
    /// Takes a limiter slot, sends one GET and reads the body, all under <paramref name="timeout"/> (on the runtime's
    /// clock). A timeout becomes <see cref="DiscoveryFailure.Timeout"/>, a transport error
    /// <see cref="DiscoveryFailure.Transport"/>; the caller's own cancellation passes through. Never retries.
    /// </summary>
    public static async Task<RawResponse> GetAsync(
        HttpClient http,
        RequestLimiter limiter,
        TimeProvider time,
        TimeSpan timeout,
        string pathAndQuery,
        string endpoint,
        Func<string, DiscoveryFailure, string, DiscoveryException> fail,
        CancellationToken ct)
    {
        await limiter.AcquireAsync(ct).ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var response = await http.GetAsync(pathAndQuery, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return new RawResponse((int)response.StatusCode, response.Headers, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw fail(endpoint, DiscoveryFailure.Timeout, $"{endpoint} timed out after {timeout.TotalSeconds:0.#} s");
        }
        catch (HttpRequestException ex)
        {
            throw fail(endpoint, DiscoveryFailure.Transport, $"{endpoint} failed: {ex.GetType().Name}");
        }
    }
}
