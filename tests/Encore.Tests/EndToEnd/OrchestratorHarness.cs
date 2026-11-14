using System.Threading.Channels;
using Encore.Config;
using Encore.Jellyfin;
using Encore.Orchestration;
using Encore.Picks;
using Encore.Store;
using Encore.Tests.Trigger;
using Encore.Tests.Watch;
using Encore.Trigger;
using Encore.Watch;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// Builds the orchestrator on a real SQLite file in its own temp folder, with a fake Jellyfin, a settable clock
/// and a log capture.
/// </summary>
internal sealed class OrchestratorHarness : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "encore-e2e-" + Guid.NewGuid().ToString("N"));

    public OrchestratorHarness()
    {
        Directory.CreateDirectory(_dir);
        DbPath = Path.Combine(_dir, "encore.db");
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
        Jellyfin = Catalog.Build();
        Store = EncoreStore.Open(DbPath, Time);
        Channel = SessionSnapshots.CreateChannel();
    }

    public string DbPath { get; }

    public ManualTimeProvider Time { get; }

    public FakeJellyfin Jellyfin { get; }

    public EncoreStore Store { get; private set; }

    public Channel<SessionsSnapshot> Channel { get; }

    public ListLogger<Orchestrator> Log { get; } = new();

    public EncoreOptions Options { get; set; } = new()
    {
        JellyfinUrl = new Uri("http://jellyfin.test:8096"),
        JellyfinApiKey = "test-key-not-real",
        AllowedUsers = ["nathan"],
        DataDir = Path.GetTempPath(),
        DeviceId = TriggerHarness.OwnDeviceId,
        DryRun = false,
    };

    public OrchestratorTiming Timing { get; set; } = new() { Tick = TimeSpan.FromMilliseconds(50) };

    /// <summary>A new orchestrator on the current store with an empty memory, as after a process restart.</summary>
    public Orchestrator CreateOrchestrator() => new(
        Jellyfin,
        new Picker(Jellyfin, NullLogger<Picker>.Instance),
        Store,
        Options,
        Channel.Reader,
        Time,
        Log,
        Timing);

    /// <summary>Closes and reopens the store file, as a restart does.</summary>
    public void RestartStore()
    {
        Store.Dispose();
        Store = EncoreStore.Open(DbPath, Time);
    }

    public static SessionsSnapshot Snapshot(TimeProvider time, params SessionInfoDto[] sessions) =>
        new(time.GetUtcNow(), SnapshotSource.Socket, sessions);

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }
}

/// <summary>Session snapshots shaped like jellyfin-web, for the end-to-end tests.</summary>
internal static class Sessions
{
    public const string WebId = "sess-web";

    /// <summary>A jellyfin-web session. The queue is listed with its PlaylistItemIds; the current song is <paramref name="current"/>.</summary>
    public static SessionInfoDto Web(Guid user, Guid current, string playlistItemId, IEnumerable<(Guid Id, string PlaylistItemId)> queue, BaseItemDto? item = null) => new()
    {
        Id = WebId,
        UserId = user,
        UserName = user == Catalog.User ? "nathan" : "guest",
        Client = "Jellyfin Web",
        DeviceId = "web-device",
        SupportsRemoteControl = true,
        NowPlayingItem = item ?? Catalog.Song(current, Catalog.SeedArtist, "Now playing"),
        NowPlayingQueue = queue.Select(q => new QueueItem { Id = q.Id, PlaylistItemId = q.PlaylistItemId }).ToList(),
        PlaylistItemId = playlistItemId,
        PlayState = new PlayerStateInfo { RepeatMode = JellyfinRepeatMode.RepeatNone, PlaybackOrder = JellyfinPlaybackOrder.Default },
    };
}
