using System.Collections.Concurrent;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Jellyfin;
using Encore.Picks;

namespace Encore.Tests.Picks;

/// <summary>An <see cref="IFetchRequests"/> with scripted availability and readiness; records every call.</summary>
internal sealed class FakeFetchRequests : IFetchRequests
{
    public FetchAvailability Availability { get; set; } = FetchAvailability.Yes;

    /// <summary>When set, decides each Request's answer; otherwise the request is accepted when available.</summary>
    public Func<FetchRequest, bool>? Accept { get; set; }

    public ConcurrentQueue<FetchRequest> Requests { get; } = new();

    public ConcurrentDictionary<string, FetchReadiness> Readiness { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string PickId, string Reason)> Abandoned { get; } = new();

    public ConcurrentQueue<(string RunId, string Reason)> EndedRuns { get; } = new();
    public ConcurrentQueue<string?> EndedSessions { get; } = new();

    public bool Request(FetchRequest request)
    {
        var ok = Accept?.Invoke(request) ?? Availability.Available;
        if (ok)
        {
            Requests.Enqueue(request);
        }

        return ok;
    }

    public FetchReadiness GetReadiness(string pickId) =>
        Readiness.TryGetValue(pickId, out var r) ? r : new FetchReadiness.Pending(FetchStage.Queued);

    public void Abandon(string pickId, string reason) => Abandoned.Enqueue((pickId, reason));

    public void EndRun(string runId, string reason, string? sessionId = null)
    {
        EndedRuns.Enqueue((runId, reason));
        EndedSessions.Enqueue(sessionId);
    }

    public void Forget(string pickId)
    {
    }

    public FetchStatus Status => FetchStatus.Off;

    /// <summary>Sets the readiness of the pick requested for this song title.</summary>
    public void Set(string title, FetchReadiness readiness) =>
        Readiness[Requests.Single(r => r.Candidate.Title == title).PickId] = readiness;
}

/// <summary>An <see cref="ICandidateSource"/> that returns a fixed list (or hangs, or throws); records the queries.</summary>
internal sealed class FakeCandidates : ICandidateSource
{
    public List<ResolvedCandidate> List { get; set; } = [];

    public bool Hang { get; set; }

    public Exception? Failure { get; set; }

    public ConcurrentQueue<CandidateQuery> Queries { get; } = new();

    public async Task<IReadOnlyList<ResolvedCandidate>> GetAsync(CandidateQuery query, CancellationToken ct)
    {
        Queries.Enqueue(query);
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        if (Failure is { } e)
        {
            throw e;
        }

        // Like the real source: drop excluded ids and keys.
        return [.. List.Where(c => !query.ExcludedKeys.Contains(c.Candidate.Key)
            && (c.Owned is null || !query.ExcludedIds.Contains(c.Owned.ItemId)))];
    }
}

/// <summary>An owned index over a dictionary; <see cref="IsLoaded"/> is settable.</summary>
internal sealed class TestOwned : IOwnedIndex
{
    private readonly ConcurrentDictionary<Guid, OwnedEntry> _byId = new();

    public bool IsLoaded { get; set; } = true;

    public OwnedEntry? Resolve(TrackKey key, int? durationS) => _byId.Values.FirstOrDefault(e => e.Key == key);

    public TrackKey? KeyOf(Guid itemId) => _byId.TryGetValue(itemId, out var e) ? e.Key : null;

    public void Add(OwnedEntry entry) => _byId[entry.ItemId] = entry;

    public int Count => _byId.Count;

    public DateTimeOffset? RefreshedUtc => null;
}

/// <summary>An owned chain: returns the first <c>Need</c> scripted songs not excluded or planned; records the needs.</summary>
internal sealed class ScriptPicker(List<PickedItem> chain) : IPicker
{
    public List<PickedItem> Chain { get; } = chain;

    public List<int> Needs { get; } = [];

    public Task<PickResult> PickAsync(PickRequest request, CancellationToken ct = default)
    {
        Needs.Add(request.Need);
        var items = Chain
            .Where(i => i.Id != request.Seed.Id && !request.Exclusions.Contains(i.Id) && request.Planned.All(p => p.Id != i.Id))
            .Take(request.Need)
            .ToList();
        return Task.FromResult(new PickResult(items, 1, Math.Max(0, request.Need - items.Count), [PickSource.SimilarArtist]));
    }
}

/// <summary>Builders for pool tests: owned-chain songs, outside candidates and the owned index entries behind them.</summary>
internal sealed class PoolWorld
{
    public TestOwned Owned { get; } = new();

    public FakeCandidates Candidates { get; } = new();

    public FakeFetchRequests Fetch { get; } = new();

    public static Guid IdOf(string name) => Lib.IdOf(name);

    /// <summary>An owned v0.1 chain song, known to the index.</summary>
    public PickedItem Own(string title, string artist)
    {
        var id = IdOf("song:" + title);
        Owned.Add(new OwnedEntry(id, TrackKey.Create(artist, title)!.Value, title, IdOf("artist:" + artist), artist, 200, "/music/" + title, null, false));
        return new PickedItem(id, title, IdOf("artist:" + artist), artist, PickSource.SimilarArtist);
    }

    /// <summary>An outside suggestion the server owns (resolved to its server copy).</summary>
    public ResolvedCandidate ExtOwned(string title, string artist, CandidateOrigin origin = CandidateOrigin.DeezerRadio)
    {
        Own(title, artist);
        var key = TrackKey.Create(artist, title)!.Value;
        return new ResolvedCandidate(Cand(key, artist, title, origin), Owned.Resolve(key, null));
    }

    /// <summary>An outside suggestion the server does not own (needs a fetch).</summary>
    public static ResolvedCandidate ExtNew(string title, string artist, CandidateOrigin origin = CandidateOrigin.DeezerRadio) =>
        new(Cand(TrackKey.Create(artist, title)!.Value, artist, title, origin), null);

    private static Candidate Cand(TrackKey key, string artist, string title, CandidateOrigin origin) =>
        new(key, artist, title, null, 200, 1, 1, null, origin, 0);

    public static BaseItemDto Seed(string title = "Seed", string artist = "SeedArtist") => Lib.Song(title, artist);
}
