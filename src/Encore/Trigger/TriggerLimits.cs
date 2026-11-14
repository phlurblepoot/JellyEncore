namespace Encore.Trigger;

/// <summary>Fixed v0.1 limits the trigger applies (design section 1.1). Not configurable.</summary>
public static class TriggerLimits
{
    /// <summary>How long a durable dedupe key blocks a repeat fire (store layer, Full model only).</summary>
    public static readonly TimeSpan DedupeTtl = TimeSpan.FromMinutes(60);

    /// <summary>Minimum gap between fires on one session.</summary>
    public static readonly TimeSpan SessionCooldown = TimeSpan.FromSeconds(20);

    public const int SessionItemsPerHour = 30;

    public const int GlobalItemsPerHour = 120;

    /// <summary>Encore songs per radio run, about 4 hours.</summary>
    public const int RunCap = 60;

    /// <summary>Fire attempts per play instance.</summary>
    public const int FireAttempts = 2;

    /// <summary>Wait before a failed fire is tried again.</summary>
    public static readonly TimeSpan FireRetryDelay = TimeSpan.FromSeconds(15);
}
