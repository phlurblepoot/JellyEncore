namespace Encore.Slskd;

public sealed record SlskdServerState(bool IsConnected, bool IsLoggedIn, string State);

/// <summary>The search to run. The client adds the fixed options (timeout, limits, filters).</summary>
public sealed record SlskdSearchRequest(Guid Id, string SearchText);

public sealed record SlskdSearch(Guid Id, string State, bool IsComplete, int ResponseCount, int FileCount);

public sealed record SlskdSearchResponse(string Username, bool HasFreeUploadSlot, long UploadSpeed, long QueueLength, IReadOnlyList<SlskdFile> Files);

/// <summary>One file a peer offers. <see cref="Extension"/> is lowercase without the dot, taken from the filename when the peer leaves it empty.</summary>
public sealed record SlskdFile(
    string Filename, long Size, int? BitRate, bool? IsVariableBitRate, int? Length, int? SampleRate, int? BitDepth, string Extension);

/// <summary>One file in a batch whose download folder is <see cref="Destination"/> (relative to slskd's downloads dir).</summary>
public sealed record SlskdEnqueueRequest(Guid BatchId, Guid SearchId, string Username, string Filename, long Size, string Destination);

/// <summary>The first transfer id when the file was queued; <see cref="Failures"/> holds the peer's or slskd's reasons otherwise.</summary>
public sealed record SlskdEnqueueResult(Guid? TransferId, IReadOnlyList<string> Failures);

public sealed record SlskdTransfer(
    Guid Id, string Username, string Filename, long Size, string State,
    long BytesTransferred, double AverageSpeed, int? PlaceInQueue, int Attempts, DateTimeOffset? NextAttemptAt)
{
    /// <summary>Terminal: the state contains "Completed" (slskd gives flag strings such as "Completed, Succeeded").</summary>
    public bool IsTerminal => State.Contains("Completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exactly "Completed, Succeeded".</summary>
    public bool IsSucceeded => string.Equals(State, "Completed, Succeeded", StringComparison.OrdinalIgnoreCase);

    /// <summary>Waiting to start: "Requested", "Initializing" or a "Queued..." state.</summary>
    public bool IsQueued =>
        State.StartsWith("Queued", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "Requested", StringComparison.OrdinalIgnoreCase)
        || string.Equals(State, "Initializing", StringComparison.OrdinalIgnoreCase);
}
