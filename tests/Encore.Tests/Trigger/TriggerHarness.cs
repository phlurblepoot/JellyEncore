using Encore.Trigger;

namespace Encore.Tests.Trigger;

/// <summary>Hand-written clock for tests (no extra package).</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Feeds snapshot sequences to <see cref="TriggerEngine"/> and keeps the session memory between them.</summary>
public sealed class TriggerHarness
{
    public const string SessionId = "e3a7f0c2b1d94a5e8f6c7b2a1d0e9f8c";
    public const string OwnDeviceId = "encore-0123456789ab";
    public static readonly Guid User = Guid.Parse("4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f");
    public static readonly Guid OtherUser = Guid.Parse("11111111222233334444555555555555");
    public static readonly Guid AlbumSource = Guid.Parse("aaaaaaaa000000000000000000000001");

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    public SessionMemory? Memory { get; set; }

    public HashSet<Guid> Allowed { get; } = [User];

    public Func<string, bool> IsDuplicate { get; set; } = static _ => false;

    public RunInfo? Run { get; set; }

    public DateTimeOffset? LastSessionFireAt { get; set; }

    public int SessionItemsLastHour { get; set; }

    public int GlobalItemsLastHour { get; set; }

    public int Batch { get; set; } = 2;

    /// <summary>The session's sends waiting for a verdict (the orchestrator's job in production; tests set it by hand).</summary>
    public List<PendingSend> Pending { get; set; } = [];

    /// <summary>True when the snapshots come from a poll (adds ENCORE_POLL_SECONDS to the delivery lag).</summary>
    public bool FromPoll { get; set; }

    public int PollSeconds { get; set; } = 10;

    public List<TriggerOutcome> Outcomes { get; } = [];

    public TriggerContext Context() => new()
    {
        AllowedUserIds = Allowed,
        Now = Time.GetUtcNow(),
        OwnDeviceId = OwnDeviceId,
        Batch = Batch,
        IsDuplicate = IsDuplicate,
        LastSessionFireAt = LastSessionFireAt,
        SessionItemsLastHour = SessionItemsLastHour,
        GlobalItemsLastHour = GlobalItemsLastHour,
        Run = Run,
        PendingSends = Pending,
        SnapshotAt = Time.GetUtcNow(),
        SnapshotFromPoll = FromPoll,
        PollSeconds = PollSeconds,
    };

    /// <summary>A pending send of <paramref name="ids"/> whose PlayLast returned now.</summary>
    public PendingSend SendNow(long triggerId, Guid[] ids, bool isResend = false) => new(triggerId, ids, Time.GetUtcNow(), isResend);

    /// <summary>A run whose newest song is the last of <paramref name="added"/>.</summary>
    public static RunInfo RunOf(params Guid[] added) => new(added[^1], added.Length, added);

    public TriggerOutcome Feed(TriggerSession snapshot)
    {
        var outcome = TriggerEngine.Evaluate(snapshot, Memory, Context());
        Memory = outcome.Memory;
        Outcomes.Add(outcome);
        return outcome;
    }

    /// <summary>Feeds every snapshot in order and returns the last outcome.</summary>
    public TriggerOutcome FeedAll(params TriggerSession[] snapshots)
    {
        TriggerOutcome last = default;
        foreach (var snapshot in snapshots)
        {
            last = Feed(snapshot);
        }

        return last;
    }

    /// <summary>The orchestrator finished the fire; <paramref name="sentIds"/> are the songs it sent or dry-ran.</summary>
    public void MarkFired(params Guid[] sentIds) => Memory = TriggerEngine.MarkFired(Memory!, sentIds);

    public bool MarkFailed()
    {
        var (memory, gaveUp) = TriggerEngine.MarkFailed(Memory!, Time.GetUtcNow());
        Memory = memory;
        return gaveUp;
    }

    /// <summary>A deterministic item id made from a short name such as "E1" (at most 16 characters).</summary>
    public static Guid Item(string name) =>
        new(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(name.PadRight(16, '_'))));

    /// <summary>A jellyfin-web snapshot: the full list as <c>playlistItem0..n</c>, current entry at <paramref name="currentIndex"/>.</summary>
    public static TriggerSession Web(Guid[] list, int currentIndex, Func<TriggerSession, TriggerSession>? change = null)
    {
        var snapshot = new TriggerSession
        {
            Id = SessionId,
            UserId = User,
            UserName = "nathan",
            Client = "Jellyfin Web",
            DeviceId = "browser-1",
            SupportsRemoteControl = true,
            NowPlayingItem = new NowPlaying(list[currentIndex], "Audio"),
            NowPlayingQueue = [.. list.Select((id, i) => new QueueEntry(id, "playlistItem" + i))],
            PlaylistItemId = "playlistItem" + currentIndex,
            RepeatMode = "RepeatNone",
            PlaybackOrder = "Default",
        };
        return change is null ? snapshot : change(snapshot);
    }

    /// <summary>A Finamp snapshot: <paramref name="upcoming"/> is the list after the current song, as the last stop reported it.</summary>
    public static TriggerSession Finamp(Guid current, Guid[] upcoming, Func<TriggerSession, TriggerSession>? change = null)
    {
        var snapshot = new TriggerSession
        {
            Id = SessionId,
            UserId = User,
            UserName = "nathan",
            Client = "Finamp",
            DeviceId = "phone-1",
            SupportsRemoteControl = true,
            NowPlayingItem = new NowPlaying(current, "Audio"),
            NowPlayingQueue = [.. upcoming.Select(id => new QueueEntry(id, "queue"))],
            PlaylistItemId = AlbumSource.ToString("N"),
            RepeatMode = "RepeatNone",
            PlaybackOrder = "Default",
        };
        return change is null ? snapshot : change(snapshot);
    }

    /// <summary>The stop half of a track change, or real idle: no NowPlayingItem.</summary>
    public static TriggerSession Idle(TriggerSession like) => like with { NowPlayingItem = null };
}
