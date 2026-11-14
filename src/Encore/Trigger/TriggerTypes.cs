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
    NotLast,
    Duplicate,
    RateLimited,
    RunCap,
}

public enum FireKind
{
    /// <summary>The user's own queue ran out: start a new radio run seeded from the current song.</summary>
    Start,

    /// <summary>The last song Encore added is now the last song: refill the current run.</summary>
    TopUp,
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
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

    /// <summary>All guards passed. The memory returned with it is already in <see cref="InstanceState.Firing"/>.</summary>
    public sealed record Fire(FireKind Kind, QueueModel Model, string DedupeKey, int Attempt) : TriggerDecision;
}

/// <summary>Result of <see cref="TriggerEngine.Evaluate"/>.</summary>
/// <param name="Memory">The session's next memory; null when the session is ignored (no tracker is kept).</param>
/// <param name="Decision">What to do.</param>
/// <param name="InstanceStarted">True when this snapshot began a new play instance (for <c>track.changed</c> and the delivery check).</param>
public readonly record struct TriggerOutcome(SessionMemory? Memory, TriggerDecision Decision, bool InstanceStarted);
