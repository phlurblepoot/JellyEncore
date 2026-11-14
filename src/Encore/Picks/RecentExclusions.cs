using Encore.Config;

namespace Encore.Picks;

/// <summary>A song a user played or was given, as the store's <c>plays</c> table holds it.</summary>
public sealed record RecentPlay(string SessionId, Guid UserId, Guid ItemId, DateTimeOffset AtUtc);

/// <summary>Builds the id set the picker must avoid (v0.1 design, section 4, <c>Exclusions</c>).</summary>
public static class RecentExclusions
{
    /// <summary>
    /// The queue ids, the seed, and the songs played or added inside the recent window:
    /// <list type="bullet">
    /// <item><c>session</c>: every play of this Jellyfin session, however old;</item>
    /// <item>a duration: every play of this user, in any session, at or after <c>now - duration</c>.</item>
    /// </list>
    /// The remaining plan is not included here: <see cref="RadioPlan"/> adds it itself, so its own
    /// items are not dropped when it re-checks them.
    /// </summary>
    public static HashSet<Guid> Build(
        IEnumerable<Guid> queueIds,
        Guid seedId,
        IEnumerable<RecentPlay> plays,
        RecentWindow window,
        string sessionId,
        Guid userId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        var set = new HashSet<Guid>(queueIds) { seedId };
        var since = window.Duration is { } d ? now - d : DateTimeOffset.MinValue;
        foreach (var play in plays)
        {
            var inWindow = window.IsSession
                ? string.Equals(play.SessionId, sessionId, StringComparison.Ordinal)
                : play.UserId == userId && play.AtUtc >= since;
            if (inWindow)
            {
                set.Add(play.ItemId);
            }
        }

        set.Remove(Guid.Empty);
        return set;
    }
}
