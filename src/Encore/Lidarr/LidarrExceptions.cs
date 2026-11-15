namespace Encore.Lidarr;

/// <summary>One entry of Lidarr's 400 validation array (only the two fields Encore reads; the names are PascalCase on the wire).</summary>
public sealed record LidarrValidationFailure(string PropertyName, string ErrorMessage);

/// <summary>
/// Base for failures of a Lidarr call. Messages name the operation and the HTTP status only: never headers, the API
/// key, URLs, query text or response bodies.
/// </summary>
public class LidarrException(string operation, string message, int? statusCode = null) : Exception(message)
{
    public string Operation { get; } = operation;

    /// <summary>HTTP status of the last response, or null for a timeout or transport failure.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Lidarr rejected the API key (HTTP 401 or 403). Never retried.</summary>
public sealed class LidarrAuthException(string operation, int statusCode)
    : LidarrException(operation, $"{operation}: Lidarr rejected the API key (HTTP {statusCode})", statusCode);

/// <summary>HTTP 400 with Lidarr's validation array. <see cref="Failures"/> may be empty when the body was another 400 shape.</summary>
public sealed class LidarrValidationException : LidarrException
{
    public LidarrValidationException(string operation, IReadOnlyList<LidarrValidationFailure> failures)
        : base(operation, $"{operation}: Lidarr rejected the request (HTTP 400, {failures.Count} validation failure(s))", 400)
    {
        Failures = failures;
    }

    public IReadOnlyList<LidarrValidationFailure> Failures { get; }

    /// <summary>True for "This album has already been added." / "This artist has already been added." (a race or a hand edit).</summary>
    public bool IsAlreadyAdded =>
        Failures.Any(f => f.ErrorMessage.Contains("already been added", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Any other failure: a status, a timeout, a transport error or an unreadable body.</summary>
public sealed class LidarrRequestException : LidarrException
{
    public LidarrRequestException(string operation, int statusCode)
        : base(operation, $"{operation} failed with HTTP {statusCode}", statusCode)
    {
    }

    public LidarrRequestException(string operation, string reason)
        : base(operation, $"{operation} failed: {reason}")
    {
    }
}
