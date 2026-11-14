namespace Encore.Trigger;

/// <summary>Whether the songs of one send showed up in the queue the client reported (design section 3, delivery check).</summary>
public enum DeliveryVerdict
{
    /// <summary>A song of the send is the current song or in the reported queue: the whole send arrived.</summary>
    Confirmed,

    /// <summary>None of the songs show up, and the report is newer than the send by at least <see cref="TriggerLimits.DeliveryLag"/>.</summary>
    Missing,

    /// <summary>None of the songs show up, but the report may predate them; judged again at the next change.</summary>
    Deferred,
}

/// <summary>
/// A send still waiting for its delivery verdict (T28). The orchestrator owns these and hands them to the trigger
/// through <see cref="TriggerContext.PendingSends"/>.
/// </summary>
/// <param name="TriggerId">The <c>triggers</c> row of the send.</param>
/// <param name="ItemIds">The songs, in send order.</param>
/// <param name="DoneAt">When the PlayLast call returned or timed out.</param>
/// <param name="IsResend">True for a re-send: a second loss is never re-sent (<c>ResendsPerSong</c> = 1).</param>
/// <param name="MissingLoggedAt">Set when a stop already judged the send missing (logged once); a new song may still take the re-send.</param>
public sealed record PendingSend(
    long TriggerId,
    IReadOnlyList<Guid> ItemIds,
    DateTimeOffset DoneAt,
    bool IsResend,
    DateTimeOffset? MissingLoggedAt = null);

/// <summary>
/// The verdict on one pending send at a new play instance. <paramref name="Resend"/> is set for
/// <see cref="DeliveryVerdict.Missing"/> only, with the <c>resend</c> log value of <c>queue.unconfirmed</c>.
/// </summary>
public sealed record SendVerdict(long TriggerId, DeliveryVerdict Verdict, string? Resend = null);

/// <summary>The <c>resend</c> values of <c>queue.unconfirmed</c> (design section 8).</summary>
public static class ResendNames
{
    /// <summary>A re-send is set on this play instance.</summary>
    public const string Pending = "pending";

    /// <summary>Judged at a stop: re-sent only if a new song starts within the grace.</summary>
    public const string Armed = "armed";

    /// <summary>Nothing is playing, so nothing is sent (PlayLast to a stopped client starts music or loads a new queue).</summary>
    public const string SkippedNotPlaying = "skipped_not_playing";

    /// <summary>Two or more songs are still to come: the radio waits until the user's songs are down to one.</summary>
    public const string SkippedUserSongs = "skipped_user_songs";

    /// <summary>The lost send was itself a re-send; its songs are not tried again.</summary>
    public const string AlreadyResent = "already_resent";
}

/// <summary>The pure delivery verdict, shared by the trigger (new instance) and the orchestrator (stops).</summary>
public static class DeliveryJudge
{
    /// <summary>
    /// Judges one send against one queue report. The verdict is per send, not per song: one PlayLast is applied as one
    /// append, so if any song shows up, all arrived (one that is missing was removed by the user).
    /// </summary>
    /// <param name="itemIds">The send's songs.</param>
    /// <param name="currentItemId">The playing song, or null at a stop.</param>
    /// <param name="reported">The queue the client reported (Full: the whole list; Upcoming: what comes next).</param>
    /// <param name="doneAt">When the send's PlayLast call returned or timed out.</param>
    /// <param name="snapshotAt">When the report was received.</param>
    /// <param name="fromPoll">True when the report came from a poll, which can show a track change from before the send.</param>
    /// <param name="pollSeconds">ENCORE_POLL_SECONDS.</param>
    public static DeliveryVerdict Judge(
        IReadOnlyCollection<Guid> itemIds,
        Guid? currentItemId,
        IReadOnlyList<QueueEntry> reported,
        DateTimeOffset doneAt,
        DateTimeOffset snapshotAt,
        bool fromPoll,
        int pollSeconds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentNullException.ThrowIfNull(reported);

        if ((currentItemId is { } current && itemIds.Contains(current)) || reported.Any(e => itemIds.Contains(e.Id)))
        {
            return DeliveryVerdict.Confirmed;
        }

        return snapshotAt - doneAt < Lag(fromPoll, pollSeconds) ? DeliveryVerdict.Deferred : DeliveryVerdict.Missing;
    }

    /// <summary><see cref="TriggerLimits.DeliveryLag"/>, plus the poll interval for a poll report.</summary>
    public static TimeSpan Lag(bool fromPoll, int pollSeconds) =>
        TriggerLimits.DeliveryLag + (fromPoll ? TimeSpan.FromSeconds(Math.Max(0, pollSeconds)) : TimeSpan.Zero);
}
