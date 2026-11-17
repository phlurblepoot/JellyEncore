using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Encore.Requests.Api;
using Encore.Store;

namespace Encore.Retention;

/// <summary>One song line of the report.</summary>
public sealed record ReportSong(
    long Id, string Artist, string Title, string Decision, string? Reason, double AgeDays, int Uploads, int Plays,
    double? DaysLeft, int? UploadsLeft, int? PlaysLeft, string? TargetPath, string? WouldDelete);

/// <summary>One playlist line of the report.</summary>
public sealed record ReportPlaylist(string Id, string Name, string? Why, int Entries, int CandidateUsers);

/// <summary>The latest pass as the report shows it (JSON shape of <c>GET /retention/report</c>).</summary>
public sealed record RetentionReportModel(
    string PassId, DateTimeOffset AtUtc, string Mode, bool DryRun, int Days, int Uploads, int Plays,
    IReadOnlyDictionary<string, int> Counts, bool DeletesSkipped, IReadOnlyList<string> Failures,
    int Users, int Favorites, bool SmartListsInstalled, bool SmartListsRead,
    IReadOnlyList<ReportSong> Songs, IReadOnlyList<string> Untracked,
    IReadOnlyList<ReportPlaylist> PlaylistsCounted, IReadOnlyList<ReportPlaylist> PlaylistsSkipped);

/// <summary>Builds the report model and its plain-text form (v04 §8).</summary>
public static class RetentionReport
{
    private static readonly string[] Order =
    [
        RetentionDecision.Promote, RetentionDecision.Dedupe, RetentionDecision.Keep, RetentionDecision.Delete, RetentionDecision.Skip, RetentionDecision.Wait,
    ];

    public static RetentionReportModel Build(PassResult pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        var counts = new Dictionary<string, int>();
        foreach (var d in Order.Append(RetentionDecision.Untracked))
        {
            counts[d] = pass.Count(d);
        }

        var songs = pass.Rows.Select(p => new ReportSong(
            p.Row.Id, p.Row.Artist, p.Row.Title, p.Decision, p.Reason, Math.Round(p.AgeDays, 1), p.Uploads, p.Plays,
            p.DaysLeft is { } d ? Math.Round(Math.Max(0, d), 1) : null, p.UploadsLeft is { } u ? Math.Max(0, u) : null,
            p.PlaysLeft is { } l ? Math.Max(0, l) : null, p.TargetPath, p.WouldDelete)).ToList();
        var signals = pass.Signals;
        return new RetentionReportModel(
            pass.PassId, pass.AtUtc, pass.Mode == Config.RetentionMode.Live ? "live" : "dry_run", pass.DryRun,
            pass.Settings.Days, pass.Settings.Uploads, pass.Settings.Plays, counts, !signals.DeletesAllowed,
            signals.Failures.Select(f => f.What + (f.Status is { } s ? " (" + s.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty) + ": " + f.Error).ToList(),
            signals.Users, signals.Favorites.Count, signals.SmartListsInstalled, signals.SmartListsRead,
            songs, pass.Untracked,
            signals.Counted.Select(p => Playlist(p)).ToList(), signals.Skipped.Select(p => Playlist(p)).ToList());
    }

    private static ReportPlaylist Playlist(SignalPlaylist p) =>
        new(p.Id.ToString("N"), p.Name, p.SkippedWhy, p.Entries.Count, p.CandidateUsers.Count);

    /// <summary>The skip reason in words.</summary>
    public static string WhySkipped(string? why) => why switch
    {
        PlaylistSkipReason.SmartListsPlugin => "SmartLists plugin list",
        PlaylistSkipReason.SmartSuffix => "name ends with [Smart]",
        PlaylistSkipReason.IgnoreList => "ENCORE_RETENTION_IGNORE_PLAYLISTS",
        _ => "not counted",
    };

    /// <summary>
    /// Plain text grouped by decision: song, artist, age, uploads, plays, reason and the library path a promotion would use; then
    /// untracked files, the playlists counted and the ones skipped.
    /// </summary>
    public static string ToText(RetentionReportModel m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Encore retention report\n");
        sb.Append(ci, $"Pass {m.PassId} at {m.AtUtc:yyyy-MM-dd HH:mm:ss}Z ({(m.DryRun ? "dry run: nothing is deleted or moved" : "live")})\n");
        sb.Append(ci, $"Rules: delete at {m.Days} days or {m.Uploads} uploads; keep for good when requested, favorited, in a hand-made playlist or played to the end {m.Plays} times\n");
        sb.Append(ci, $"Signals: {m.Users} users, {m.Favorites} favorites, {m.PlaylistsCounted.Count} playlists counted, {m.PlaylistsSkipped.Count} skipped; ");
        sb.Append(m.SmartListsInstalled ? (m.SmartListsRead ? "SmartLists installed and read\n" : "SmartLists installed but its list is UNREADABLE\n") : "SmartLists not installed\n");
        if (m.DeletesSkipped)
        {
            sb.Append("DELETES SKIPPED this pass: a signal could not be read (" + string.Join("; ", m.Failures) + ")\n");
        }

        foreach (var decision in Order)
        {
            var group = m.Songs.Where(s => s.Decision == decision).ToList();
            sb.Append(ci, $"\n{decision.ToUpperInvariant()} ({group.Count})\n");
            foreach (var s in group)
            {
                sb.Append(ci, $"  {s.Title} - {s.Artist} | age {s.AgeDays:0.0} d | uploads {s.Uploads} | plays {s.Plays}");
                if (s.Reason is not null)
                {
                    sb.Append(decision == RetentionDecision.Delete ? " | delete: " : decision == RetentionDecision.Skip ? " | would delete: " : " | kept: ").Append(s.Reason == RetentionSkip.SignalsFailed ? s.WouldDelete + " (skipped, signals failed)" : s.Reason);
                }

                if (decision == RetentionDecision.Wait)
                {
                    sb.Append(ci, $" | {s.DaysLeft:0.0} days, {s.UploadsLeft} uploads, {s.PlaysLeft} plays left");
                }

                if (s.TargetPath is not null)
                {
                    sb.Append(decision == RetentionDecision.Dedupe ? " | library has it: " : " | to ").Append(s.TargetPath);
                }

                sb.Append('\n');
            }
        }

        sb.Append(ci, $"\nUNTRACKED ({m.Untracked.Count})\n");
        foreach (var path in m.Untracked)
        {
            sb.Append("  ").Append(path).Append('\n');
        }

        sb.Append(ci, $"\nPLAYLISTS COUNTED ({m.PlaylistsCounted.Count})\n");
        foreach (var p in m.PlaylistsCounted)
        {
            sb.Append(ci, $"  {p.Name} ({p.Id}) | {p.Entries} songs\n");
        }

        sb.Append(ci, $"\nPLAYLISTS SKIPPED ({m.PlaylistsSkipped.Count})\n");
        foreach (var p in m.PlaylistsSkipped)
        {
            sb.Append(ci, $"  {p.Name} ({p.Id}) | {WhySkipped(p.Why)}\n");
        }

        return sb.ToString();
    }
}

/// <summary><c>GET /retention/report</c>: LAN only, never through a trusted proxy (v04 §8).</summary>
public static class RetentionReportEndpoint
{
    public const string Route = "/retention/report";

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(Route, (HttpContext context, RetentionPass pass, string? format) =>
        {
            if (!IsDirectLan(context, pass.Options.TrustedProxies))
            {
                return Results.NotFound();
            }

            var latest = pass.Latest;
            var text = string.Equals(format, "text", StringComparison.OrdinalIgnoreCase);
            if (latest is null)
            {
                return text
                    ? Results.Text("No retention pass has run yet.\n", "text/plain", Encoding.UTF8)
                    : Results.Json(new { passId = (string?)null, mode = pass.Options.RetentionMode == Config.RetentionMode.Live ? "live" : "dry_run" });
            }

            var model = RetentionReport.Build(latest);
            return text ? Results.Text(RetentionReport.ToText(model), "text/plain", Encoding.UTF8) : Results.Json(model);
        });
    }

    /// <summary>
    /// A request reaches the report only from a private-network peer that is not a trusted proxy and carries no forwarding header.
    /// The peer is read before any forwarded-headers middleware could rewrite it (the requests gate records that as an item).
    /// </summary>
    public static bool IsDirectLan(HttpContext context, IReadOnlyList<IPNetwork> trustedProxies)
    {
        if (context.Items[RequestsApi.ProxiedItem] is true)
        {
            return false;
        }

        var h = context.Request.Headers;
        if (h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Forwarded") || h.ContainsKey("X-Real-IP") || h.ContainsKey("X-Forwarded-Host"))
        {
            return false;
        }

        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || new ProxyTrust(trustedProxies).IsTrusted(peer))
        {
            return false;
        }

        return IsPrivate(peer);
    }

    /// <summary>Loopback, RFC 1918, link-local, unique-local IPv6 and the 100.64.0.0/10 shared range (Tailscale), IPv4-mapped forms included.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC);
    }
}
