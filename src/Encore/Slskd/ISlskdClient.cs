namespace Encore.Slskd;

/// <summary>slskd 0.26.2 REST client (see .knowledge/encore/slskd-api.md).</summary>
public interface ISlskdClient
{
    /// <summary>GET /api/v0/server. Encore is ready when <see cref="SlskdServerState.IsLoggedIn"/> is true.</summary>
    Task<SlskdServerState> GetServerStateAsync(CancellationToken ct);

    /// <summary>POST /api/v0/searches with the fixed options. Throws <see cref="SlskdBusyException"/> on 429 and <see cref="SlskdUnavailableException"/> on 409.</summary>
    Task<SlskdSearch> StartSearchAsync(SlskdSearchRequest request, CancellationToken ct);

    /// <summary>GET /api/v0/searches/{id}; null when slskd does not know the search.</summary>
    Task<SlskdSearch?> GetSearchAsync(Guid id, CancellationToken ct);

    /// <summary>GET /api/v0/searches/{id}/responses.</summary>
    Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(Guid id, CancellationToken ct);

    /// <summary>PUT /api/v0/searches/{id}; an unknown search is fine.</summary>
    Task CancelSearchAsync(Guid id, CancellationToken ct);

    /// <summary>DELETE /api/v0/searches/{id}; an unknown search is fine.</summary>
    Task DeleteSearchAsync(Guid id, CancellationToken ct);

    /// <summary>POST /api/v0/transfers/downloads/batches. 429 waits and retries, then <see cref="SlskdBusyException"/>; 404 is <see cref="SlskdPeerOfflineException"/>. Never retried after a timeout.</summary>
    Task<SlskdEnqueueResult> EnqueueAsync(SlskdEnqueueRequest request, CancellationToken ct);

    /// <summary>GET /api/v0/transfers/downloads/{username}/{id}; null on 404.</summary>
    Task<SlskdTransfer?> GetTransferAsync(string username, Guid transferId, CancellationToken ct);

    /// <summary>DELETE /api/v0/transfers/downloads/{username}/{id}?remove=...; an unknown transfer is fine.</summary>
    Task CancelTransferAsync(string username, Guid transferId, bool remove, CancellationToken ct);
}
