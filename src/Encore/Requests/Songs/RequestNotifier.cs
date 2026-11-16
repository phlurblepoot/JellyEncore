using Encore.Store;

namespace Encore.Requests.Songs;

/// <summary>
/// Tells the requester that a request reached <c>ready</c> or <c>failed</c> (v0.3 design 9). T88 provides the Jellyfin
/// message implementation; until then <see cref="NullRequestNotifier"/> does nothing. A notifier must not throw for a
/// delivery problem (it logs <c>notify.failed</c>) and must never change the request's state.
/// </summary>
public interface IRequestNotifier
{
    Task NotifyAsync(RequestRow request, CancellationToken ct);
}

/// <summary>The default notifier: sends nothing.</summary>
public sealed class NullRequestNotifier : IRequestNotifier
{
    public static readonly NullRequestNotifier Instance = new();

    public Task NotifyAsync(RequestRow request, CancellationToken ct) => Task.CompletedTask;
}
