using Encore.Config;
using Encore.Orchestration;
using Encore.Watch;

namespace Encore.Health;

/// <summary>The body of <c>GET /health</c> (design section 7). Holds no key, no header and no user data beyond the configured names and their ids.</summary>
public sealed record HealthReport(
    string Status,
    string Version,
    bool DryRun,
    DateTimeOffset StartedUtc,
    HealthJellyfin Jellyfin,
    HealthSocket Socket,
    HealthPoll Poll,
    HealthAllowList AllowList,
    HealthSessions Sessions,
    HealthTriggers Triggers,
    HealthPlayLast PlayLast,
    HealthStore Store);

public sealed record HealthJellyfin(string Url, DateTimeOffset? LastOkUtc, string? LastError);

public sealed record HealthSocket(string State, DateTimeOffset SinceUtc, int Reconnects, DateTimeOffset? LastMessageUtc, DateTimeOffset? LastSessionsUtc);

public sealed record HealthPoll(bool Active, DateTimeOffset? LastPollUtc);

public sealed record HealthAllowList(
    IReadOnlyList<string> Configured,
    IReadOnlyDictionary<string, string> Resolved,
    IReadOnlyList<string> Unresolved,
    DateTimeOffset? ResolvedUtc,
    string? Error);

public sealed record HealthSessions(int Tracked, int AllowListedPlaying);

public sealed record HealthTriggers(int Fired, int Sent, int DryRun, int NoPicks, int Failed, DateTimeOffset? LastFireUtc, string? LastOutcome);

public sealed record HealthPlayLast(int ItemsSent, int Unconfirmed, DateTimeOffset? LastSentUtc);

public sealed record HealthStore(bool Ok, string Path, int SchemaVersion);

/// <summary>Everything the health report is computed from. Built per request from immutable snapshots.</summary>
public sealed record HealthInput(
    EncoreOptions Options,
    string Version,
    DateTimeOffset Now,
    WatcherStatus Watcher,
    OrchestratorStatus Orchestrator,
    string StorePath,
    int StoreSchemaVersion);

public static class HealthEvaluator
{
    /// <summary>The loop must heartbeat within this window, or the endpoint returns 503 (the Docker HEALTHCHECK then fails).</summary>
    public static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(60);

    /// <summary>The first seconds after start report "starting".</summary>
    public static readonly TimeSpan StartingFor = TimeSpan.FromSeconds(30);

    /// <summary>A successful poll this recent counts as "polling succeeds".</summary>
    public static readonly TimeSpan PollFresh = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Status: <c>stopped</c> (503) when the loop has not heartbeat for 60 s; <c>starting</c> for the first 30 s;
    /// <c>ok</c> when the socket is subscribed or polling works, every allow-list name resolves, and the store is open;
    /// otherwise <c>degraded</c> (200).
    /// </summary>
    public static (int HttpStatus, HealthReport Report) Evaluate(HealthInput input)
    {
        var o = input.Orchestrator;
        var w = input.Watcher;
        var now = input.Now;

        var stale = now - o.LastHeartbeatUtc > HeartbeatStaleAfter;
        var starting = now - o.StartedUtc < StartingFor;
        var socketUp = w.State == SocketState.Subscribed;
        var pollUp = w.LastPollAt is { } poll && now - poll < PollFresh;
        var allowListOk = o.AllowListConfigured.Count > 0
            && o.AllowListResolvedUtc is not null
            && o.AllowListUnresolved.Count == 0
            && o.AllowListError is null;
        var ok = (socketUp || pollUp) && allowListOk && o.StoreOk;

        var status = stale ? "stopped" : starting ? "starting" : ok ? "ok" : "degraded";

        var report = new HealthReport(
            Status: status,
            Version: input.Version,
            DryRun: input.Options.DryRun,
            StartedUtc: o.StartedUtc,
            Jellyfin: new HealthJellyfin(input.Options.JellyfinUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'), o.JellyfinLastOkUtc, o.JellyfinLastError),
            Socket: new HealthSocket(w.State.ToString().ToLowerInvariant(), w.Since, w.Reconnects, w.LastMessageAt, w.LastSessionsAt),
            Poll: new HealthPoll(w.PollActive, w.LastPollAt),
            AllowList: new HealthAllowList(o.AllowListConfigured, o.AllowListResolved, o.AllowListUnresolved, o.AllowListResolvedUtc, o.AllowListError),
            Sessions: new HealthSessions(o.SessionsTracked, o.AllowListedPlaying),
            Triggers: new HealthTriggers(o.Fired, o.Sent, o.DryRun, o.NoPicks, o.Failed, o.LastFireUtc, o.LastOutcome),
            PlayLast: new HealthPlayLast(o.ItemsSent, o.Unconfirmed, o.LastSentUtc),
            Store: new HealthStore(o.StoreOk, input.StorePath, input.StoreSchemaVersion));

        return (stale ? 503 : 200, report);
    }
}
