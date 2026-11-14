namespace Encore.Fetch;

/// <summary>Fixed fetch limits (v0.2 design section 11). They are constants, not settings.</summary>
public static class FetchLimits
{
    public const int QueriesPerSong = 2;
    public const int PeersPerSong = 3;

    /// <summary>slskd's own searchTimeout: a search ends after this much silence.</summary>
    public const int SearchInactivityMs = 8000;
    public static readonly TimeSpan SearchMaxWait = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan EarlyStopAfter = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan SearchPoll = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SearchScoreEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SearchMinGap = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SearchBusyWait = TimeSpan.FromSeconds(2);
    public const int SearchBusyRetries = 3;

    public static readonly TimeSpan TransferPoll = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StallQueued = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StallIdle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TooSlowCheckAfter = TimeSpan.FromSeconds(10);

    /// <summary>Best-effort clean-up (cancel and remove a transfer, delete a search) at shutdown.</summary>
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan IngestWait = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan IngestPoll = TimeSpan.FromSeconds(5);
    public const int MaxPassOvers = 1;
    public static readonly TimeSpan NegativeCache = TimeSpan.FromHours(24);
    public const int FetchesPerHour = 30;
    /// <summary>20 GB in decimal units, as Unraid shows free space. The only free-space floor (the importer uses it too).</summary>
    public const long FreeSpaceFloorBytes = 20_000_000_000;
    public static readonly TimeSpan ExternalLookupTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OwnedIndexRefresh = TimeSpan.FromMinutes(30);
    public const int OwnedIndexPageSize = 500;
    public static readonly TimeSpan SlskdStateEvery = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LibraryCheckEvery = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FreeSpaceEvery = TimeSpan.FromMinutes(5);
}
