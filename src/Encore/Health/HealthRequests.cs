using System.Text.Json.Serialization;
using Encore.Config;
using Encore.Fetch;
using Encore.Lidarr;
using Encore.Requests.Auth;

namespace Encore.Health;

/// <summary>
/// The <c>requests</c> section of /health (v0.3 design section 12). With ENCORE_REQUESTS=off only <c>enabled</c> is written
/// (the other members are null and skipped). Holds counts and states only: no key, no token, no user name, no IP.
/// </summary>
public sealed record HealthRequests(
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Songs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Albums = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRequestsActive? Active = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRequestsAuth? Auth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRequestsToday? Today = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRequestsLidarr? Lidarr = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRequestsNotify? Notify = null);

public sealed record HealthRequestsActive(int Queued, int Searching, int Downloading, int Importing);

public sealed record HealthRequestsAuth(int BlockedIps, int LockedNames, int FailedLoginsLastHour);

/// <summary>Requests created, made ready and failed in the last 24 hours.</summary>
public sealed record HealthRequestsToday(int Created, int Ready, int Failed);

public sealed record HealthRequestsLidarr(bool Configured, bool? Reachable, string? Version, DateTimeOffset? LastOkUtc, string? LastError);

public sealed record HealthRequestsNotify(int Sent, int Failed);

/// <summary>The request side as /health reads it. Null in <see cref="HealthInput"/> means ENCORE_REQUESTS=off.</summary>
public sealed record RequestsHealthInput(
    IReadOnlyDictionary<string, int> ActiveByState,
    GuardSnapshot Auth,
    (int Created, int Ready, int Failed) Today,
    LidarrState? Lidarr,
    int NotifySent,
    int NotifyFailed,
    int AlbumsWaitingForJellyfin);

public static class HealthRequestsEvaluator
{
    public static HealthRequests Section(HealthInput input)
    {
        if (input.Requests is not { } r)
        {
            return new HealthRequests(false);
        }

        int Count(string state) => r.ActiveByState.TryGetValue(state, out var n) ? n : 0;

        var lidarr = r.Lidarr is { } l
            ? new HealthRequestsLidarr(true, l.Reachable, l.Version, l.LastOkUtc, l.LastError is null ? null : Clean(l.LastError))
            : new HealthRequestsLidarr(false, null, null, null, null);

        return new HealthRequests(
            true,
            Songs(input),
            Albums(r),
            new HealthRequestsActive(Count("queued"), Count("searching"), Count("downloading"), Count("importing")),
            new HealthRequestsAuth(r.Auth.BlockedIps, r.Auth.LockedNames, r.Auth.FailedLoginsLastHour),
            new HealthRequestsToday(r.Today.Created, r.Today.Ready, r.Today.Failed),
            lidarr,
            new HealthRequestsNotify(r.NotifySent, r.NotifyFailed));
    }

    public static IEnumerable<string> Warnings(HealthInput input)
    {
        if (input.Requests is not { } r)
        {
            yield break;
        }

        if (r.Lidarr is { } l)
        {
            if (l.KeyRejected)
            {
                yield return "lidarr: key rejected";
            }
            else if (l.Reachable == false)
            {
                yield return "lidarr: unreachable";
            }
        }

        if (r.AlbumsWaitingForJellyfin > 0)
        {
            yield return $"requests: {r.AlbumsWaitingForJellyfin} album(s) in Lidarr but not in Jellyfin";
        }

        if (input.Fetch is null || input.Fetch.Status.Mode == FetchMode.Off)
        {
            yield return "requests: song fetching is off";
        }
    }

    private static string Songs(HealthInput input)
    {
        if (input.Fetch is not { } f || f.Status.Mode == FetchMode.Off)
        {
            return "fetch_off";
        }

        return f.Status.Available ? "available" : $"fetch_unavailable:{f.Status.UnavailableReason ?? "unknown"}";
    }

    private static string Albums(RequestsHealthInput r)
    {
        if (r.Lidarr is not { } l)
        {
            return "lidarr_not_configured";
        }

        return l.KeyRejected ? "lidarr_key_rejected" : l.Reachable == false ? "lidarr_unreachable" : "available";
    }

    /// <summary>The last error text from Lidarr's client: a message only, kept short.</summary>
    private static string Clean(string error) => error.Length <= 200 ? error : error[..200];
}
