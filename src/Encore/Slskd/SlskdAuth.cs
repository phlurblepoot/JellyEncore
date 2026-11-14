namespace Encore.Slskd;

/// <summary>Adds the slskd <c>X-API-Key</c> header, and only that, to every request. The key never goes in a URL.</summary>
public sealed class SlskdAuthHandler : DelegatingHandler
{
    public const string HeaderName = "X-API-Key";

    private readonly string _apiKey;

    public SlskdAuthHandler(string apiKey)
    {
        // Throws with the parameter name only, never the value.
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("must not be blank", nameof(apiKey));
        }

        if (apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException("contains a character that is not allowed in a header", nameof(apiKey));
        }

        _apiKey = apiKey;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, _apiKey);
        return base.SendAsync(request, cancellationToken);
    }

    public override string ToString() => nameof(SlskdAuthHandler);
}
