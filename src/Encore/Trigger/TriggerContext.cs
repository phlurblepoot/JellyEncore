namespace Encore.Trigger;

/// <summary>Everything outside the session snapshot that the trigger needs. Built by the orchestrator per evaluation.</summary>
public sealed record TriggerContext
{
    /// <summary>Resolved allow-list. Matching is by user id, so a renamed user still matches.</summary>
    public required IReadOnlySet<Guid> AllowedUserIds { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>Encore's own DeviceId; a session with this DeviceId is ignored.</summary>
    public string? OwnDeviceId { get; init; }

    /// <summary>ENCORE_BATCH: songs sent per fire, used by the hourly caps.</summary>
    public int Batch { get; init; } = 2;

    /// <summary>
    /// True when the store holds a trigger with this key younger than <see cref="TriggerLimits.DedupeTtl"/>
    /// whose outcome was sent or dry_run. Only consulted for the Full model.
    /// </summary>
    public Func<string, bool> IsDuplicate { get; init; } = static _ => false;

    /// <summary>Time of the last fire on this session that sent (or dry-ran) songs; null when none.</summary>
    public DateTimeOffset? LastSessionFireAt { get; init; }

    /// <summary>Songs Encore added to this session in the last hour.</summary>
    public int SessionItemsLastHour { get; init; }

    /// <summary>Songs Encore added across all sessions in the last hour.</summary>
    public int GlobalItemsLastHour { get; init; }

    /// <summary>The session's current radio run, or null when there is none.</summary>
    public RunInfo? Run { get; init; }
}

/// <summary>What the trigger needs to know about the session's current radio run.</summary>
public sealed record RunInfo(Guid? LastAddedItemId, int AddedCount);
