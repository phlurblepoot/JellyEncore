using System.Collections.Concurrent;
using System.Diagnostics;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Fetch.Scoring;
using Encore.Jellyfin;
using Encore.Slskd;
using Encore.Store;
using Encore.Tests.Discovery;
using Encore.Tests.Fetch;
using Encore.Tests.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// A fake clock for the fetch side: it runs <see cref="Scale"/> times faster than real time, timers included, so
/// the fetch flow (search silence, stall rules, the 5 s ingest poll) takes real but short time and is safe for
/// concurrent jobs. It starts at the orchestrator harness's time.
/// </summary>
internal sealed class ScaledClock(DateTimeOffset start, double scale) : TimeProvider
{
    private readonly long _t0 = Stopwatch.GetTimestamp();

    public double Scale { get; } = scale;

    public override DateTimeOffset GetUtcNow() => start + (Stopwatch.GetElapsedTime(_t0) * Scale);

    public override long GetTimestamp() => _t0 + (long)((Stopwatch.GetTimestamp() - _t0) * Scale);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, Shrink(dueTime), Shrink(period));

    private TimeSpan Shrink(TimeSpan t) => t == Timeout.InfiniteTimeSpan || t <= TimeSpan.Zero ? t : t / Scale;
}

/// <summary>The real coordinator behind a recorder: every request, abandon and run end the orchestrator makes.</summary>
internal sealed class RecordingFetch(IFetchRequests inner) : IFetchRequests
{
    public ConcurrentQueue<FetchRequest> Requests { get; } = new();

    public ConcurrentQueue<(string PickId, string Reason)> Abandoned { get; } = new();

    public ConcurrentQueue<(string RunId, string Reason)> EndedRuns { get; } = new();

    public FetchAvailability Availability => inner.Availability;

    public bool Request(FetchRequest request)
    {
        var ok = inner.Request(request);
        if (ok)
        {
            Requests.Enqueue(request);
        }

        return ok;
    }

    public FetchReadiness GetReadiness(string pickId) => inner.GetReadiness(pickId);

    public void Abandon(string pickId, string reason)
    {
        Abandoned.Enqueue((pickId, reason));
        inner.Abandon(pickId, reason);
    }

    public void EndRun(string runId, string reason, string? sessionId = null)
    {
        EndedRuns.Enqueue((runId, reason));
        inner.EndRun(runId, reason, sessionId);
    }

    public FetchStatus Status => inner.Status;

    /// <summary>The pick requested for this title (the first one).</summary>
    public FetchRequest For(string title) => Requests.First(r => r.Candidate.Title == title);
}

/// <summary>
/// The whole fetch side around an <see cref="OrchestratorHarness"/> (v0.2 design section 15, T51): the real
/// candidate source over a fake Deezer and ListenBrainz, the real owned index over the harness's fake Jellyfin, the
/// real coordinator, worker and Soulseek fetcher over a fake slskd, the real Jellyfin ingest (the fake Jellyfin lists a
/// reported file after <see cref="FakeJellyfin.IngestDelay"/>), and a scripted importer. The fetch side runs on a
/// <see cref="ScaledClock"/>; the orchestrator keeps the harness's manual clock. The fetch store is the harness's
/// database file, as in Program.
/// </summary>
internal sealed class FetchWorld : IAsyncDisposable
{
    public const string RadioJellyfinDir = "/music-radio";

    /// <summary>The seed artist's name as the catalog songs carry it (what Deezer is asked for).</summary>
    public static readonly string SeedArtistName = $"Artist {Catalog.SeedArtist:N}";

    private static readonly Guid MusicLibrary = Guid.Parse("32e0aa11-2233-4455-8899-aabbccddeeff");
    private const long SeedDeezerId = 4242;
    private int _nextTrack = 70_000;
    private bool _started;
    private bool _stopped;
    private bool _ownsRoot = true;

    public FetchWorld(OrchestratorHarness h, FetchMode mode, double scale = 100)
        : this(h, mode, null, scale)
    {
    }

    /// <summary>With <paramref name="before"/>: the process after a restart, on the same folders, clock and outside services (slskd, Deezer, ListenBrainz).</summary>
    private FetchWorld(OrchestratorHarness h, FetchMode mode, FetchWorld? before, double scale)
    {
        H = h;
        Mode = mode;
        if (before is null)
        {
            Root = Path.Combine(Path.GetTempPath(), "encore-t51-" + Guid.NewGuid().ToString("N"));
            RadioDir = Path.Combine(Root, "radio");
            DownloadsDir = Path.Combine(Root, "downloads");
            Directory.CreateDirectory(RadioDir);
            Directory.CreateDirectory(DownloadsDir);

            Clock = new ScaledClock(h.Time.GetUtcNow(), scale);
            h.Jellyfin.Clock = Clock;
            h.Jellyfin.IngestDelay = TimeSpan.FromSeconds(60);
            h.Jellyfin.VirtualFolders = [new VirtualFolderInfo { Name = "Music", Locations = [RadioJellyfinDir], CollectionType = "music", ItemId = MusicLibrary }];
            Slskd = new FakeSlskd(Clock, DownloadsDir);
            Deezer = new FakeDeezer();
            ListenBrainz = new FakeListenBrainz();
            Deezer.AddArtist(SeedDeezerId, SeedArtistName);
        }
        else
        {
            Root = before.Root;
            RadioDir = before.RadioDir;
            DownloadsDir = before.DownloadsDir;
            Clock = before.Clock;
            Slskd = before.Slskd;
            Deezer = before.Deezer;
            ListenBrainz = before.ListenBrainz;
            _nextTrack = before._nextTrack;
        }

        Store = FetchStore.Open(h.DbPath);
        Owned = new OwnedIndex(h.Jellyfin, RadioJellyfinDir, NullLogger<OwnedIndex>.Instance, Clock, refreshEvery: TimeSpan.FromDays(1000));
        Coordinator = new FetchCoordinator(mode == FetchMode.Off ? FetchMode.Live : mode, Owned, CoordinatorLog, Clock);
        Fetch = new RecordingFetch(Coordinator);
        Fetcher = new SoulseekFetcher(Slskd, new SearchBudget(new SearchLimit(34, 220), Clock), new Scorer(), Store, DownloadsDir, FetcherLog, Clock);
        Importer = new FakeImporter(RadioDir);
        Ingest = new JellyfinIngest(h.Jellyfin, RadioJellyfinDir, NullLogger<JellyfinIngest>.Instance, Clock);
        Worker = new FetchWorker(Coordinator, Fetcher, Importer, Ingest, Owned, h.Jellyfin, Slskd, Store, new FakeFreeSpace(100_000_000_000),
            new FetchWorkerSettings(RadioDir, RadioJellyfinDir, DownloadsDir, 2), WorkerLog, Clock);
        Candidates = new CandidateSource(Deezer, ListenBrainz, h.Jellyfin, Owned, new FetchStoreNegativeCache(Store),
            new DiscoveryRuntime(null, Clock), NullLogger<CandidateSource>.Instance);

        h.Options = h.Options with { FetchMode = mode };
        h.Fetch = Fetch;
        h.Candidates = Candidates;
        h.Owned = Owned;
    }

    public OrchestratorHarness H { get; }

    public FetchMode Mode { get; }

    public string Root { get; }

    public string RadioDir { get; }

    public string DownloadsDir { get; }

    public ScaledClock Clock { get; }

    public FetchStore Store { get; }

    public OwnedIndex Owned { get; }

    public FetchCoordinator Coordinator { get; }

    public RecordingFetch Fetch { get; }

    public FakeSlskd Slskd { get; }

    public SoulseekFetcher Fetcher { get; }

    public FakeImporter Importer { get; }

    public JellyfinIngest Ingest { get; }

    public FetchWorker Worker { get; }

    public CandidateSource Candidates { get; }

    public FakeDeezer Deezer { get; }

    public FakeListenBrainz ListenBrainz { get; }

    public ListLogger<FetchCoordinator> CoordinatorLog { get; } = new();

    public ListLogger<FetchWorker> WorkerLog { get; } = new();

    public ListLogger<SoulseekFetcher> FetcherLog { get; } = new();

    // ------------------------------------------------------------ the outside world

    /// <summary>Deezer's radio for the seed artist (the outside suggestions, in order).</summary>
    public void Radio(params DeezerTrack[] tracks) => Deezer.Radio[SeedDeezerId] = [.. tracks];

    /// <summary>A Deezer track of 200 s.</summary>
    public DeezerTrack Track(string artist, string title) => Songs.Track(Interlocked.Increment(ref _nextTrack), artist, title);

    /// <summary>A song the server already has (outside music-radio); the owned index loads it at start. Returns its item id.</summary>
    public Guid OwnSong(int n, string artist, string title)
    {
        var item = Songs.Audio(n, artist, title, 200);
        H.Jellyfin.Audio.Add(item);
        return item.Id;
    }

    /// <summary>The song as a 320 kbps MP3 of 200 s in a peer's share.</summary>
    public static SlskdFile Mp3(string artist, string title) =>
        new($"@@share\\Music\\{artist}\\Album\\01 {title}.mp3", 8_000_000, 320, false, 200, 44100, null, "mp3");

    /// <summary>A copy the scorer never accepts (wrong format).</summary>
    public static SlskdFile M4a(string artist, string title) =>
        new($"@@share\\Music\\{artist}\\Album\\01 {title}.m4a", 8_000_000, null, null, 200, 44100, null, "m4a");

    /// <summary>Every search query for this song answers with these peers (user, upload speed, file); complete after 3 s.</summary>
    public void Offer(string artist, string title, params (string User, long Speed, SlskdFile File)[] peers)
    {
        var responses = peers.Select(p => new SlskdSearchResponse(p.User, true, p.Speed, 0, [p.File])).ToList();
        foreach (var query in SoulseekQueries.For(artist, title))
        {
            Slskd.Searches[query] = new SearchScript(responses, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>One peer that sends the song in 6 s.</summary>
    public void OfferGood(string artist, string title, string user = "alice")
    {
        Offer(artist, title, (user, 2_000_000, Mp3(artist, title)));
        Slskd.Peers[user] = FakeSlskd.Succeeds(TimeSpan.FromSeconds(6), 8_000_000);
    }

    /// <summary>The Jellyfin path the importer gives this song in music-radio.</summary>
    public static string JellyfinPathOf(string artist, string title) => $"{RadioJellyfinDir}/soulseek/{artist}/{title}.mp3";

    // ------------------------------------------------------------ running

    /// <summary>Starts the worker (not in mode off: Program registers no worker then) and waits until the owned index is loaded and (unless told not to) fetching is available.</summary>
    public async Task StartAsync(bool waitAvailable = true)
    {
        if (Mode == FetchMode.Off)
        {
            return;
        }

        _started = true;
        await Worker.StartAsync(CancellationToken.None);
        await Worker.Reconciled.WaitAsync(TimeSpan.FromSeconds(10));
        await Wait.Until(() => Owned.IsLoaded, "the owned index loaded");
        if (waitAvailable)
        {
            await Wait.Until(() => Coordinator.Availability.Available, "fetching available");
        }
    }

    /// <summary>
    /// A process restart: stops this worker as the host does (rows stay as they are), closes its store, and builds a new
    /// fetch side on the same database, folders, clock and outside services, then starts it (restart reconciliation runs
    /// first). The harness's fetch, candidates and owned index point at the new side; this one no longer owns the folders.
    /// </summary>
    public async Task<FetchWorld> RestartAsync()
    {
        await StopAsync();
        _ownsRoot = false;
        var next = new FetchWorld(H, Mode, this, Clock.Scale);
        await next.StartAsync();
        return next;
    }

    private async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (_started)
        {
            _started = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Worker.StopAsync(cts.Token);
        }

        Worker.Dispose();
        Store.Dispose();
        SqliteConnection.ClearAllPools();
    }

    public FetchReadiness Readiness(string title) => Coordinator.GetReadiness(Fetch.For(title).PickId);

    public Task UntilReadyAsync(string title, int timeoutMs = 15_000) =>
        Wait.Until(() => Fetch.Requests.Any(r => r.Candidate.Title == title) && Readiness(title) is FetchReadiness.Ready, $"{title} ready", timeoutMs);

    public Task UntilFailedAsync(string title, int timeoutMs = 15_000) =>
        Wait.Until(() => Fetch.Requests.Any(r => r.Candidate.Title == title) && Readiness(title) is FetchReadiness.Failed, $"{title} failed", timeoutMs);

    /// <summary>The item the fake Jellyfin gave the fetched song (null until it was reported).</summary>
    public Guid? FetchedId(string artist, string title) => H.Jellyfin.ItemIdOf(JellyfinPathOf(artist, title));

    public List<string> Query(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={H.DbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (!_ownsRoot)
        {
            return;
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Plays a web album the way the buffer tests do, recording each send and how long each snapshot took.</summary>
internal sealed class Listener(OrchestratorHarness h, Encore.Orchestration.Orchestrator o)
{
    private Encore.Orchestration.Orchestrator _o = o;

    /// <summary>After a restart: the same listener (queue and position) goes on with the new orchestrator.</summary>
    public void Restarted(Encore.Orchestration.Orchestrator orchestrator) => _o = orchestrator;

    public static readonly Guid[] Album = [Catalog.G(500), Catalog.G(501), Catalog.G(502)];

    /// <summary>The queue as the client reports it: the album, then every song Encore sent.</summary>
    public List<Guid> Queue { get; } = [.. Album];

    /// <summary>Real time each snapshot (and any fire in it) took.</summary>
    public List<TimeSpan> Durations { get; } = [];

    /// <summary>The orchestrator's heartbeat matched the snapshot's time after every snapshot.</summary>
    public bool HeartbeatFresh { get; private set; } = true;

    public int Index { get; private set; } = -1;

    /// <summary>Plays entry <paramref name="index"/> of the current queue (200 s after the last song); new sends join the queue. Returns the ids sent, if any.</summary>
    public async Task<Guid[]?> PlayAsync(int index, IReadOnlyList<Guid>? reported = null)
    {
        if (Index >= 0)
        {
            h.Time.Advance(TimeSpan.FromSeconds(200));
        }

        Index = index;
        var before = h.Jellyfin.PlayLasts.Count;
        var list = (reported ?? Queue).ToArray();
        var watch = Stopwatch.StartNew();
        await h.Send(_o, Sessions.WebAt(list, index));
        Durations.Add(watch.Elapsed);
        HeartbeatFresh &= _o.Status.LastHeartbeatUtc == h.Time.GetUtcNow();
        if (h.Jellyfin.PlayLasts.Count == before)
        {
            return null;
        }

        var sent = h.Jellyfin.PlayLasts.Last().ItemIds;
        Queue.AddRange(sent);
        return sent;
    }

    /// <summary>Album song 1 then 2 (the second-to-last song: the start fire). Returns the start batch.</summary>
    public async Task<Guid[]> StartAsync()
    {
        await PlayAsync(0);
        return await PlayAsync(1) ?? throw new InvalidOperationException("no start fire");
    }

    /// <summary>The next song, then the one after (second-to-last: the top-up). Returns the top-up batch.</summary>
    public async Task<Guid[]> TopUpAsync()
    {
        await PlayAsync(Index + 1);
        return await PlayAsync(Index + 1) ?? throw new InvalidOperationException("no top-up fire");
    }
}
