namespace Encore.Lidarr;

/// <summary>
/// The Lidarr calls v0.4 retention needs (v04 design sections 7.5 and 13). A separate interface so <see cref="ILidarrClient"/>
/// and its fakes stay as they are; <see cref="LidarrClient"/> implements both on the same HttpClient.
/// </summary>
public interface IRetentionLidarr
{
    /// <summary>GET /api/v1/command: every command in Lidarr's in-memory queue, finished ones included.</summary>
    Task<IReadOnlyList<LidarrCommand>> GetCommandsAsync(CancellationToken ct);

    /// <summary>
    /// GET /api/v1/config/metadataprovider, field <c>writeAudioTags</c>: one of the <see cref="LidarrWriteAudioTags"/> values
    /// (a value Lidarr adds later is returned as it is).
    /// </summary>
    Task<string> GetWriteAudioTagsAsync(CancellationToken ct);
}
