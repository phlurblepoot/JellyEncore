namespace Encore.Slskd;

/// <summary>
/// Base for failures of a slskd call. Messages name the operation and the HTTP status only: never headers, the API
/// key, query text, URLs or response bodies.
/// </summary>
public class SlskdException(string operation, string message, int? statusCode = null) : Exception(message)
{
    public string Operation { get; } = operation;

    /// <summary>HTTP status of the last response, or null for a timeout or transport failure.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>slskd rejected the API key (HTTP 401 or 403). Never retried.</summary>
public sealed class SlskdAuthException(string operation, int statusCode)
    : SlskdException(operation, $"{operation}: slskd rejected the API key (HTTP {statusCode})", statusCode);

/// <summary>HTTP 429: slskd allows one search being created, or two download requests, at a time.</summary>
public sealed class SlskdBusyException(string operation)
    : SlskdException(operation, $"{operation}: slskd is busy (HTTP 429)", 429);

/// <summary>HTTP 404 on enqueue: the peer is offline or blacklisted.</summary>
public sealed class SlskdPeerOfflineException(string operation)
    : SlskdException(operation, $"{operation}: the peer is offline (HTTP 404)", 404);

/// <summary>HTTP 409: slskd cannot do this now (for example it is not connected to Soulseek) or the id already exists.</summary>
public sealed class SlskdUnavailableException(string operation)
    : SlskdException(operation, $"{operation}: slskd cannot do this now (HTTP 409)", 409);

/// <summary>Any other failure: a status, a timeout, a transport error or an unreadable body.</summary>
public sealed class SlskdRequestException : SlskdException
{
    public SlskdRequestException(string operation, int statusCode)
        : base(operation, $"{operation} failed with HTTP {statusCode}", statusCode)
    {
    }

    public SlskdRequestException(string operation, string reason)
        : base(operation, $"{operation} failed: {reason}")
    {
    }
}
