using System.Text.Json.Serialization;
using Encore.Config;
using Encore.Retention;
using Encore.Store;

namespace Encore.Health;

/// <summary>
/// The <c>retention</c> section of /health (v0.4 design section 11). Always present; with ENCORE_RETENTION_MODE=off only <c>mode</c> is
/// written (the other members are null and skipped). Counts, times and states only: no key, no path, no user name.
/// </summary>
public sealed record HealthRetention(
    string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastPassUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? NextPassUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Tracked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, int>? Decisions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, int>? KeptBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Promoted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Deleted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Promoting = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Stalled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? InTrash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRetentionSignals? Signals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRetentionSlskd? Slskd = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HealthRetentionLidarr? LidarrGuard = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? LibraryFreeBytes = null);

/// <summary>What the latest pass read. Null members mean no pass has run yet.</summary>
public sealed record HealthRetentionSignals(int Users, int PlaylistsCounted, int PlaylistsSkipped, bool SmartListsInstalled, bool SmartListsRead);

public sealed record HealthRetentionSlskd(bool RadioShared, int UploadsCounted, DateTimeOffset? LastEventsReadUtc, DateTimeOffset? LastRescanUtc, bool RescanPending);

public sealed record HealthRetentionLidarr(bool Checked, bool Ok, string? Reason);

/// <summary>The retention side as /health reads it. Null in <see cref="HealthInput"/> means mode off.</summary>
public sealed record RetentionHealthInput(
    RetentionMode Mode,
    PassResult? Latest,
    DateTimeOffset NextPassUtc,
    int Promoted,
    int Deleted,
    int Promoting,
    int Stalled,
    int InTrash,
    bool RadioShared,
    int UploadsCounted,
    bool EventsUnreadable,
    DateTimeOffset? LastEventsReadUtc,
    DateTimeOffset? LastRescanUtc,
    bool RescanPending,
    PromotionGuard? Guard);

public static class HealthRetentionEvaluator
{
    private static readonly string[] KeepReasons = ["requested", "favorite", "playlist", "plays"];

    public static HealthRetention Section(HealthInput input)
    {
        if (input.Retention is not { } r || r.Mode == RetentionMode.Off)
        {
            return new HealthRetention("off");
        }

        var latest = r.Latest;
        var decisions = new Dictionary<string, int>();
        var keptBy = KeepReasons.ToDictionary(k => k, _ => 0);
        HealthRetentionSignals? signals = null;
        if (latest is not null)
        {
            foreach (var d in new[] { RetentionDecision.Promote, RetentionDecision.Dedupe, RetentionDecision.Keep, RetentionDecision.Delete, RetentionDecision.Wait, RetentionDecision.Untracked })
            {
                decisions[d] = latest.Count(d);
            }

            foreach (var row in latest.Rows.Where(p => p.Decision == RetentionDecision.Keep && p.Reason is not null))
            {
                keptBy[row.Reason!] = keptBy.GetValueOrDefault(row.Reason!) + 1;
            }

            var s = latest.Signals;
            signals = new HealthRetentionSignals(s.Users, s.Counted.Count(), s.Skipped.Count(), s.SmartListsInstalled, s.SmartListsRead);
        }

        var guard = r.Guard;
        return new HealthRetention(
            r.Mode == RetentionMode.Live ? "live" : "dry_run",
            latest?.AtUtc,
            r.NextPassUtc,
            latest?.Rows.Count,
            latest is null ? null : decisions,
            latest is null ? null : keptBy,
            r.Promoted,
            r.Deleted,
            r.Promoting,
            r.Stalled,
            r.InTrash,
            signals,
            new HealthRetentionSlskd(r.RadioShared, r.UploadsCounted, r.LastEventsReadUtc, r.LastRescanUtc, r.RescanPending),
            new HealthRetentionLidarr(guard?.Lidarr.Checked ?? false, guard?.Lidarr.Ok ?? true, guard?.Lidarr.Reason),
            guard?.LibraryFreeBytes);
    }

    /// <summary>Warnings only: retention problems never change the status. Nothing is reported with the mode off.</summary>
    public static IEnumerable<string> Warnings(HealthInput input)
    {
        var warnings = new List<string>();
        if (input.Retention is not { } r || r.Mode == RetentionMode.Off)
        {
            return warnings;
        }

        if (r.Guard is { Ok: false } g)
        {
            warnings.Add(g.Reason == PromotionPause.LibraryNotWritable
                ? "retention: library dir not writable"
                : $"retention: promotion paused ({g.Reason})");
        }

        if (r.Stalled > 0)
        {
            warnings.Add($"retention: {r.Stalled} promotions stalled");
        }

        if (!r.RadioShared && input.Now - input.Orchestrator.StartedUtc > HealthEvaluator.OwnedIndexGrace)
        {
            warnings.Add("retention: music-radio is not shared on Soulseek");
        }

        if (r.EventsUnreadable)
        {
            warnings.Add("retention: slskd events unreadable");
        }

        if (r.Latest is { Signals: { SmartListsInstalled: true, SmartListsRead: false } })
        {
            warnings.Add("retention: SmartLists list unreadable, deletes skipped");
        }

        return warnings;
    }
}
