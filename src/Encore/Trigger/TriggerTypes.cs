namespace Encore.Trigger;

/// <summary>How a session's queue is read (design section 2).</summary>
public enum QueueModel
{
    /// <summary>jellyfin-web and its wrappers: the whole list, with the current entry in it.</summary>
    Full,

    /// <summary>Finamp: only the songs after the current one.</summary>
    Upcoming,

    /// <summary>Anything else; never counts as the last song.</summary>
    Unknown,
}

/// <summary>Why a snapshot did not fire (design section 8, <c>trigger.skipped</c>).</summary>
public enum SkipReason
{
    NotAllowed,
    NotAudio,
    NoRemoteControl,
    RepeatOn,
    UnknownClient,
    StaleQueue,
    NoHistory,

    /// <summary>Two or more songs still to come (the wire name is kept from before the one-song buffer).</summary>
    NotLast,
    Duplicate,
    RateLimited,
    RunCap,

    /// <summary>The previous instance sent songs that this report does not show yet; the delivery check owns that case (T28).</summary>
    AlreadyQueued,

    /// <summary>
    /// T75: Finamp's queue-first report (the old song again with the list that follows the next song). It belongs to
    /// the old song's instance and never fires.
    /// </summary>
    QueueFirst,

    /// <summary>
    /// T110: a song played on its own (no real queue reported) that is paused or has not reached its near-end point
    /// yet. The single-song start waits for that point.
    /// </summary>
    SingleWaiting,
}

public enum FireKind
{
    /// <summary>The user's own queue is running out: start a new radio run seeded from the current song.</summary>
    Start,

    /// <summary>The one song left (or the current last song) is the last song Encore added: refill the current run.</summary>
    TopUp,

    /// <summary>A send the client's queue report did not show: send the same songs once more (T28).</summary>
    Resend,
}

/// <summary>Which rule made a fire (design section 2, T28): logged as <c>rule</c> on <c>trigger.fired</c>.</summary>
public enum FireRule
{
    /// <summary>One song is left after the current one (the main rule).</summary>
    NearEnd,

    /// <summary>The current song is the last one (the fallback).</summary>
    LastSong,

    /// <summary>The delivery check called for a re-send.</summary>
    Resend,

    /// <summary>T110: a song started on its own (no real queue reported) reached its near-end point.</summary>
    Single,
}

/// <summary>Per play instance state (design section 3).</summary>
public enum InstanceState
{
    Evaluating,
    Firing,
    Fired,
    FireFailed,
    GaveUp,
}

public static class TriggerNames
{
    /// <summary>The snake_case name used in logs, for example <c>no_remote_control</c>.</summary>
    public static string ToWire(this SkipReason reason) => reason switch
    {
        SkipReason.NotAllowed => "not_allowed",
        SkipReason.NotAudio => "not_audio",
        SkipReason.NoRemoteControl => "no_remote_control",
        SkipReason.RepeatOn => "repeat_on",
        SkipReason.UnknownClient => "unknown_client",
        SkipReason.StaleQueue => "stale_queue",
        SkipReason.NoHistory => "no_history",
        SkipReason.NotLast => "not_last",
        SkipReason.Duplicate => "duplicate",
        SkipReason.RateLimited => "rate_limited",
        SkipReason.RunCap => "run_cap",
        SkipReason.AlreadyQueued => "already_queued",
        SkipReason.QueueFirst => "queue_first",
        SkipReason.SingleWaiting => "single_waiting",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static string ToWire(this QueueModel model) => model switch
    {
        QueueModel.Full => "full",
        QueueModel.Upcoming => "upcoming",
        QueueModel.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
    };

    public static string ToWire(this FireKind kind) => kind switch
    {
        FireKind.Start => "start",
        FireKind.TopUp => "topup",
        FireKind.Resend => "resend",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string ToWire(this FireRule rule) => rule switch
    {
        FireRule.NearEnd => "near_end",
        FireRule.LastSong => "last_song",
        FireRule.Resend => "resend",
        FireRule.Single => "single",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
    };

    public static string ToWire(this DeliveryVerdict verdict) => verdict switch
    {
        DeliveryVerdict.Confirmed => "confirmed",
        DeliveryVerdict.Missing => "missing",
        DeliveryVerdict.Deferred => "deferred",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };
}

/// <summary>The trigger's answer for one snapshot: <see cref="None"/>, <see cref="Skip"/> or <see cref="Fire"/>.</summary>
public abstract record TriggerDecision
{
    private TriggerDecision() { }

    /// <summary>Nothing to do: idle, ignored, or the instance already fired, is firing, or is waiting to retry.</summary>
    public sealed record None : TriggerDecision
    {
        public static None Instance { get; } = new();
    }

    /// <summary>
    /// A guard failed. <paramref name="ShouldLog"/> is true the first time this reason is seen in the
    /// play instance (for <see cref="SkipReason.NotAllowed"/>: in the session), so logs stay quiet.
    /// </summary>
    public sealed record Skip(SkipReason Reason, QueueModel Model, bool ShouldLog) : TriggerDecision;

    /// <summary>
    /// All guards passed. The memory returned with it is already in <see cref="InstanceState.Firing"/>.
    /// <paramref name="ItemIds"/> is set only for <see cref="FireKind.Resend"/>: the songs to send again, in order.
    /// </summary>
    public sealed record Fire(
        FireKind Kind,
        QueueModel Model,
        string DedupeKey,
        int Attempt,
        FireRule Rule,
        IReadOnlyList<Guid>? ItemIds = null) : TriggerDecision;
}

/// <summary>Result of <see cref="TriggerEngine.Evaluate"/>.</summary>
/// <param name="Memory">The session's next memory; null when the session is ignored (no tracker is kept).</param>
/// <param name="Decision">What to do.</param>
/// <param name="InstanceStarted">True when this snapshot began a new play instance (for <c>track.changed</c> and the delivery check).</param>
/// <param name="Verdicts">T28: one delivery verdict per pending send, only on the first snapshot of a new instance.</param>
public readonly record struct TriggerOutcome(
    SessionMemory? Memory,
    TriggerDecision Decision,
    bool InstanceStarted,
    IReadOnlyList<SendVerdict>? Verdicts = null)
{
    /// <summary>The verdicts, never null.</summary>
    public IReadOnlyList<SendVerdict> AllVerdicts => Verdicts ?? [];
}
