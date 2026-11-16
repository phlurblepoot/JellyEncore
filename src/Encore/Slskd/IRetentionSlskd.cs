namespace Encore.Slskd;

/// <summary>
/// The slskd calls v0.4 retention needs (v04 design sections 5 and 13). A separate interface so <see cref="ISlskdClient"/> and
/// its fakes stay as they are; <see cref="SlskdClient"/> implements both on the same HttpClient. Failures throw the usual
/// <see cref="SlskdException"/> subclasses (no key, URL or body in their text).
/// </summary>
public interface IRetentionSlskd
{
    /// <summary>GET /api/v0/events?offset=&amp;limit= (newest first). Only <c>UploadFileComplete</c> records come back as uploads.</summary>
    Task<SlskdEventsPage> GetEventsAsync(int offset, int limit, CancellationToken ct);

    /// <summary>GET /api/v0/transfers/uploads (non-removed only), flattened from user, directory and files.</summary>
    Task<IReadOnlyList<SlskdUpload>> GetUploadsAsync(CancellationToken ct);

    /// <summary>GET /api/v0/shares: a dictionary keyed by host, flattened.</summary>
    Task<IReadOnlyList<SlskdShare>> GetSharesAsync(CancellationToken ct);

    /// <summary>GET /api/v0/application, the <c>shares</c> part (the scan state; /shares has none).</summary>
    Task<SlskdShareState> GetShareStateAsync(CancellationToken ct);

    /// <summary>PUT /api/v0/shares: starts a scan. slskd answers 200 even when one is already running, so check the state first. Never retried.</summary>
    Task RescanSharesAsync(CancellationToken ct);
}
