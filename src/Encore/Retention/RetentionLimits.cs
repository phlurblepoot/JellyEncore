namespace Encore.Retention;

/// <summary>Fixed retention limits (v0.4 design, section 10). Not configurable.</summary>
public static class RetentionLimits
{
    public static readonly TimeSpan EventsReadEvery = TimeSpan.FromMinutes(15);

    public const int MaxEventPages = 50;

    public static readonly TimeSpan SharesCheckEvery = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan RescanCheckEvery = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan MinRescanGap = TimeSpan.FromHours(6);

    public static readonly TimeSpan OldGoneWait = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan OldGonePoll = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan LidarrCommandWait = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan IngestWait = TimeSpan.FromMinutes(5);

    public const int MaxPromotionAttempts = 5;

    public static readonly TimeSpan TrashKept = TimeSpan.FromDays(7);

    public static readonly TimeSpan DeleteSkipWindow = TimeSpan.FromHours(3);

    public const long FreeSpaceFloorBytes = 20L * 1024 * 1024 * 1024;

    public static readonly TimeSpan StretchBreak = TimeSpan.FromMinutes(2);

    /// <summary>A play counts when at least this share of the song was listened to.</summary>
    public const double ListenedFraction = 0.50;

    /// <summary>A play counts as finished when the position reached this share of the song.</summary>
    public const double PositionFraction = 0.90;

    public const string StagedSuffix = ".encore-part";

    public const UnixFileMode NewFolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public const UnixFileMode NewFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Name suffix that marks a SmartLists playlist.</summary>
    public const string SmartListSuffix = "[Smart]";

    /// <summary>Hidden folder under the library dir where the write probe and staged copies live.</summary>
    public const string IncomingFolder = ".encore-incoming";
}
