using Encore.Config;
using Encore.Fetch;
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
    HealthStore Store,
    IReadOnlyList<string> Warnings,
    HealthSlskd Slskd,
    HealthFetch Fetch,
    HealthRequests? Requests = null,
    HealthRetention? Retention = null);

public sealed record HealthJellyfin(string Url, DateTimeOffset? LastOkUtc, string? LastError);

public sealed record HealthSocket(string State, DateTimeOffset SinceUtc, int Reconnects, DateTimeOffset? LastMessageUtc, DateTimeOffset? LastSessionsUtc);

public sealed record HealthPoll(bool Active, DateTimeOffset? LastPollUtc);

public sealed record HealthAllowList(
    IReadOnlyList<string> Configured,
    IReadOnlyDictionary<string, string> Resolved,
    IReadOnlyList<string> Unresolved,
    DateTimeOffset? ResolvedUtc,
    string? Error,
    bool Everyone = false);

public sealed record HealthSessions(int Tracked, int AllowListedPlaying);

public sealed record HealthTriggers(int Fired, int Sent, int DryRun, int NoPicks, int Failed, DateTimeOffset? LastFireUtc, string? LastOutcome);

public sealed record HealthPlayLast(int ItemsSent, int Unconfirmed, DateTimeOffset? LastSentUtc);

public sealed record HealthStore(bool Ok, string Path, int SchemaVersion);

/// <summary>The slskd connection (v0.2 design section 12). Mode off: Configured false, the rest null or 0. Never holds the key.</summary>
public sealed record HealthSlskd(
    bool Configured,
    string? Url,
    bool? Reachable,
    bool? LoggedIn,
    DateTimeOffset? LastOkUtc,
    string? LastError,
    int SearchesInWindow,
    string? SearchLimit);

public sealed record HealthOwnedIndex(bool Loaded, int Items, DateTimeOffset? RefreshedUtc);

public sealed record HealthFetch(
    string Mode,
    bool Available,
    string? UnavailableReason,
    HealthOwnedIndex OwnedIndex,
    int Queued,
    int InFlight,
    int Ingesting,
    int Ready,
    int Failed,
    int Cancelled,
    int PassedOver,
    int Skipped,
    int WouldDownload,
    int LastHour,
    int HourlyCap,
    bool? RadioDirInLibrary,
    string? RadioLibrary,
    long? FreeBytes,
    int SourceLeftBehind,
    DateTimeOffset? LastReadyUtc,
    string? LastError);

/// <summary>The fetch side as the health endpoint reads it. A null value in <see cref="HealthInput"/> means mode off.</summary>
public sealed record FetchHealthInput(
    FetchStatus Status,
    int SearchesInWindow,
    bool OwnedLoaded,
    int OwnedItems,
    DateTimeOffset? OwnedRefreshedUtc,
    DateTimeOffset? DeezerLastFailureUtc);

/// <summary>Everything the health report is computed from. Built per request from immutable snapshots.</summary>
public sealed record HealthInput(
    EncoreOptions Options,
    string Version,
    DateTimeOffset Now,
    WatcherStatus Watcher,
    OrchestratorStatus Orchestrator,
    string StorePath,
    int StoreSchemaVersion,
    FetchHealthInput? Fetch = null,
    RequestsHealthInput? Requests = null,
    RetentionHealthInput? Retention = null);

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

        var warnings = Warn(input);

        var report = new HealthReport(
            Status: status,
            Version: input.Version,
            DryRun: input.Options.DryRun,
            StartedUtc: o.StartedUtc,
            Jellyfin: new HealthJellyfin(input.Options.JellyfinUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'), o.JellyfinLastOkUtc, o.JellyfinLastError),
            Socket: new HealthSocket(w.State.ToString().ToLowerInvariant(), w.Since, w.Reconnects, w.LastMessageAt, w.LastSessionsAt),
            Poll: new HealthPoll(w.PollActive, w.LastPollAt),
            AllowList: new HealthAllowList(o.AllowListConfigured, o.AllowListResolved, o.AllowListUnresolved, o.AllowListResolvedUtc, o.AllowListError, o.AllowListEveryone),
            Sessions: new HealthSessions(o.SessionsTracked, o.AllowListedPlaying),
            Triggers: new HealthTriggers(o.Fired, o.Sent, o.DryRun, o.NoPicks, o.Failed, o.LastFireUtc, o.LastOutcome),
            PlayLast: new HealthPlayLast(o.ItemsSent, o.Unconfirmed, o.LastSentUtc),
            Store: new HealthStore(o.StoreOk, input.StorePath, input.StoreSchemaVersion),
            Warnings: warnings,
            Slskd: SlskdSection(input),
            Fetch: FetchSection(input),
            Requests: RequestsSection(input),
            Retention: HealthRetentionEvaluator.Section(input));

        return (stale ? 503 : 200, report);
    }

    /// <summary>The owned index counts as missing when it is still not loaded this long after start.</summary>
    public static readonly TimeSpan OwnedIndexGrace = TimeSpan.FromMinutes(5);

    /// <summary>A Deezer error this recent shows as a warning.</summary>
    public static readonly TimeSpan DeezerRecent = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Fetch problems never change the status (only these strings): owned radio still works without them.
    /// Nothing is reported in mode off.
    /// </summary>
    private static List<string> Warn(HealthInput input)
    {
        var warnings = FetchWarnings(input);
        warnings.AddRange(RequestWarnings(input));
        warnings.AddRange(HealthRetentionEvaluator.Warnings(input));
        return warnings;
    }

    private static List<string> FetchWarnings(HealthInput input)
    {
        var warnings = new List<string>();
        if (input.Fetch is not { } f || f.Status.Mode == FetchMode.Off)
        {
            return warnings;
        }

        var s = f.Status;
        if (s.SlskdKeyRejected)
        {
            warnings.Add("slskd: key rejected");
        }
        else if (s.SlskdReachable == false)
        {
            warnings.Add("slskd: unreachable");
        }
        else if (s.SlskdLoggedIn == false)
        {
            warnings.Add("slskd: not logged in");
        }

        if (s.RadioDirInLibrary == false)
        {
            warnings.Add("music-radio is not in a Jellyfin music library");
        }
        else if (s.RadioDirScanned == false)
        {
            warnings.Add("music-radio not scanned by Jellyfin yet (run Scan All Libraries)");
        }

        if (s.FreeBytes is { } free && free < FetchLimits.FreeSpaceFloorBytes)
        {
            warnings.Add("music-radio free space below 20 GB");
        }

        else if (s.FreeBytes is null && input.Now - input.Orchestrator.StartedUtc > OwnedIndexGrace)
        {
            warnings.Add("music-radio free space unknown (disk guard off)");
        }

        if (!f.OwnedLoaded &&input.Now - input.Orchestrator.StartedUtc > OwnedIndexGrace)
        {
            warnings.Add("owned index not loaded");
        }

        if (s.SourceLeftBehind > 0)
        {
            warnings.Add($"slskd copies left behind: {s.SourceLeftBehind}");
        }

        if (f.DeezerLastFailureUtc is { } failed && input.Now - failed < DeezerRecent)
        {
            warnings.Add("deezer: failing");
        }

        return warnings;
    }

    private static HealthRequests RequestsSection(HealthInput input) => HealthRequestsEvaluator.Section(input);

    private static IEnumerable<string> RequestWarnings(HealthInput input) => HealthRequestsEvaluator.Warnings(input);

    private static HealthSlskd SlskdSection(HealthInput input)
    {
        if (input.Fetch is not { } f || f.Status.Mode == FetchMode.Off)
        {
            return new HealthSlskd(false, null, null, null, null, null, 0, null);
        }

        var s = f.Status;
        var url = input.Options.SlskdUrl?.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new HealthSlskd(
            input.Options.SlskdUrl is not null && input.Options.SlskdApiKey is not null,
            url, s.SlskdReachable, s.SlskdLoggedIn, s.SlskdLastOkUtc, s.SlskdLastError,
            f.SearchesInWindow, input.Options.SearchLimit.ToString());
    }

    private static HealthFetch FetchSection(HealthInput input)
    {
        var o = input.Orchestrator;
        if (input.Fetch is not { } f)
        {
            var off = FetchStatus.Off;
            return new HealthFetch(
                "off", false, AvailabilityReasons.Off, new HealthOwnedIndex(false, 0, null), 0, 0, 0, 0, 0, 0, o.PassedOver, o.Skipped,
                0, 0, off.HourlyCap, null, null, null, 0, null, null);
        }

        var s = f.Status;
        return new HealthFetch(
            ModeName(s.Mode), s.Available, s.UnavailableReason, new HealthOwnedIndex(f.OwnedLoaded, f.OwnedItems, f.OwnedRefreshedUtc),
            s.Queued, s.InFlight, s.Ingesting, s.Ready, s.Failed, s.Cancelled, o.PassedOver, o.Skipped,
            s.WouldDownload, s.LastHour, s.HourlyCap, s.RadioDirInLibrary, s.RadioLibrary, s.FreeBytes,
            s.SourceLeftBehind, s.LastReadyUtc, s.LastError);
    }

    private static string ModeName(FetchMode mode) => mode switch
    {
        FetchMode.DryRun => "dry_run",
        FetchMode.Live => "live",
        _ => "off",
    };
}
