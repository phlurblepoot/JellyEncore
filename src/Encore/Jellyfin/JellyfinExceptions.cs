namespace Encore.Jellyfin;

/// <summary>
/// Base for failures of a Jellyfin call. Messages name the operation and the HTTP status only:
/// never request headers, the API key, or response bodies.
/// </summary>
public class JellyfinException(string operation, string message, int? statusCode = null) : Exception(message)
{
    public string Operation { get; } = operation;

    /// <summary>HTTP status of the last response, or null for a timeout or transport failure.</summary>
    public int? StatusCode { get; } = statusCode;
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

    public JellyfinRequestException(string operation, string reason)
        : base(operation, $"{operation} failed: {reason}")
    {
    }
}
