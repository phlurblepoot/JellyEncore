namespace Encore.Jellyfin;

/// <summary>
/// Base for failures of a Jellyfin call. Messages name the operation and the HTTP status only:
/// never request headers, the API key, or response bodies.
/// </summary>
public class JellyfinException(string operation, string message, int? statusCode = null, bool outcomeUnknown = false) : Exception(message)
{
    public string Operation { get; } = operation;

    /// <summary>HTTP status of the last response, or null for a timeout or transport failure.</summary>
    public int? StatusCode { get; } = statusCode;

    /// <summary>
    /// True when the request may have reached Jellyfin and been carried out, but no answer came back: a timeout, or a
    /// connection that broke after the request was sent. False when Jellyfin answered with an error status, or when
    /// the connection could not be opened at all (nothing was sent).
    /// </summary>
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

/// <summary>Jellyfin rejected the API key (HTTP 401 or 403). Never retried.</summary>
public sealed class JellyfinAuthException(string operation, int statusCode)
    : JellyfinException(operation, $"{operation}: Jellyfin rejected the API key (HTTP {statusCode})", statusCode)
{
}

/// <summary>A Jellyfin call failed: a non-success status after any retries, a timeout, a transport error, or an unreadable body.</summary>
public sealed class JellyfinRequestException : JellyfinException
{
    public JellyfinRequestException(string operation, int statusCode)
        : base(operation, $"{operation} failed with HTTP {statusCode}", statusCode)
    {
    }

    public JellyfinRequestException(string operation, string reason, bool outcomeUnknown = false)
        : base(operation, $"{operation} failed: {reason}", outcomeUnknown: outcomeUnknown)
    {
    }
}
