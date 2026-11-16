using System.Globalization;
using Encore.Config;
using Encore.Discovery;
using Encore.Fetch;
using Encore.Requests.Api;
using Encore.Requests.Auth;
using Encore.Store;

namespace Encore.Requests.Songs;

/// <summary>Error codes of the request routes (the JSON <c>error</c> field, v0.3 design 4).</summary>
public static class RequestCodes
{
    public const string DailyLimit = "daily_limit";
    public const string SongsUnavailable = "songs_unavailable";
    public const string AlbumsUnavailable = "albums_unavailable";
    public const string DeezerUnavailable = "deezer_unavailable";
    public const string Conflict = "conflict";
    public const string AdminOnly = "admin_only";
    public const string NotFound = "not_found";
}

/// <summary>The settings a <see cref="RequestService"/> needs: the fetch mode and the per-person daily song cap.</summary>
public sealed record SongRequestSettings(FetchMode Mode, int SongsPerDay)
{
    public static SongRequestSettings From(EncoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SongRequestSettings(options.FetchMode, options.RequestSongsPerDay);
    }
}

/// <summary>What a request route answers: an HTTP status and the request, or an error code.</summary>
public sealed record RequestOutcome(int Status, RequestRow? Row, string? Error = null)
{
    public bool Ok => Error is null;

    public static RequestOutcome Fail(int status, string code) => new(status, null, code);
}

/// <summary>What one worker pass did: how many requests it handed to the fetch side, and the requests to notify.</summary>
public sealed record RequestTickResult(int Submitted, IReadOnlyList<RequestRow> Notify);

/// <summary>
/// Song requests (v0.3 design 6.1 and 6.3). Creates them (daily cap, Deezer track detail, owned -> ready at once, otherwise a
/// <c>req:{id}</c> fetch job with request priority), cancels and retries them, and runs the lifecycle pass the
/// <see cref="RequestWorker"/> calls every 2 s. Requests meet the fetch side only through <see cref="IFetchRequests"/>
/// (invariant 5). Every state change happens under one lock, so the worker and the routes never race on a row; no I/O
/// other than the (synchronous, local) stores runs under it.
/// </summary>
public sealed class RequestService
{
    /// <summary>Waits after the first and the second failed try (design 6.3); the third failure is final.</summary>
    public static readonly TimeSpan[] RetryWaits = [TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    public const int MaxAttempts = 3;

    public const string PickPrefix = "req:";

    public const string RunId = "request";

    private static readonly HashSet<string> FinalReasons = new(StringComparer.Ordinal)
    {
        FetchReasons.ImportFailed, FetchReasons.NotScanned, FetchReasons.Off,
    };

    private readonly RequestStore _store;
    private readonly IFetchRequests _fetch;
    private readonly IOwnedIndex? _owned;
    private readonly IDeezerClient _deezer;
    private readonly FetchStore? _fetchStore;
    private readonly SongRequestSettings _settings;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // Requests whose req: pick the fetch side holds in this process. Empty after a start, so every active request is
    // submitted again by the first pass (pick ids from before a restart are unknown to the coordinator).
    private readonly HashSet<string> _submitted = new(StringComparer.Ordinal);

    public RequestService(
        RequestStore store,
        IFetchRequests fetch,
        IOwnedIndex? owned,
        IDeezerClient deezer,
        FetchStore? fetchStore,
        SongRequestSettings settings,
        ILogger<RequestService> logger,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _deezer = deezer ?? throw new ArgumentNullException(nameof(deezer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = logger ?? throw new ArgumentNullException(nameof(logger));
        _owned = owned;
        _fetchStore = fetchStore;
        _time = time ?? TimeProvider.System;
    }

    public SongRequestSettings Settings => _settings;

    public static string PickId(string requestId) => PickPrefix + requestId;

    /// <summary>
    /// POST /requests {kind:"song", deezerTrackId} (design 6.1). 201 with a new request; 200 with the caller's own active
    /// request for the same song; 429 at the daily cap; 404 when Deezer has no such track; 503 when song requests are off
    /// (fetch mode off) or Deezer cannot be reached. Unavailable fetching is not an error: the request waits in queued.
    /// The caller has checked that the user may request.
    /// </summary>
    public async Task<(RequestOutcome Outcome, IReadOnlyList<RequestRow> Notify)> CreateSongAsync(RequestUser user, long deezerTrackId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_settings.Mode == FetchMode.Off)
        {
            return (RequestOutcome.Fail(StatusCodes.Status503ServiceUnavailable, RequestCodes.SongsUnavailable), []);
        }

        var userKey = RequestUserKey.Of(user.Id);
        if (_store.ListByUser(userKey, activeOnly: true).FirstOrDefault(r => r.Kind == "song" && r.DeezerTrackId == deezerTrackId) is { } same)
        {
            return (new RequestOutcome(StatusCodes.Status200OK, same), []);
        }

        if (OverCap(user, userKey))
        {
            return (RequestOutcome.Fail(StatusCodes.Status429TooManyRequests, RequestCodes.DailyLimit), []);
        }

        // Encore never trusts artist or title from the client: everything comes from Deezer's track detail.
        DeezerTrackDetail? detail;
        try
        {
            detail = await _deezer.GetTrackAsync(deezerTrackId, ct).ConfigureAwait(false);
        }
        catch (DiscoveryException ex)
        {
            _log.WorkerError("deezer_track", ex.GetType().Name + ": " + ex.Message);
            return (RequestOutcome.Fail(StatusCodes.Status503ServiceUnavailable, RequestCodes.DeezerUnavailable), []);
        }

        var title = detail is null ? null : string.IsNullOrWhiteSpace(detail.Title) ? detail.TitleShort : detail.Title;
        if (detail is null || !TrackKey.TryCreate(detail.Artist.Name, title, out var key))
        {
            return (RequestOutcome.Fail(StatusCodes.Status404NotFound, RequestCodes.NotFound), []);
        }

        var notify = new List<RequestRow>();
        RequestRow row;
        lock (_gate)
        {
            if (_store.ActiveForKey(key.Value).FirstOrDefault(r => r.UserId == userKey) is { } mine)
            {
                return (new RequestOutcome(StatusCodes.Status200OK, mine), []);
            }

            // Checked again under the lock, so two quick posts cannot both pass the last free slot.
            if (OverCap(user, userKey))
            {
                return (RequestOutcome.Fail(StatusCodes.Status429TooManyRequests, RequestCodes.DailyLimit), []);
            }

            var others = _store.ActiveForKey(key.Value);
            var now = _time.GetUtcNow();
            var id = Guid.NewGuid().ToString("N");
            _store.Insert(new NewRequest(
                id, "song", userKey, user.Name, detail.Artist.Name.Trim(), now,
                Title: title!.Trim(),
                Album: string.IsNullOrWhiteSpace(detail.Album.Title) ? null : detail.Album.Title.Trim(),
                Year: YearOf(detail.ReleaseDate) ?? YearOf(detail.Album.ReleaseDate),
                CoverUrl: detail.Album.CoverUrl,
                TrackKey: key.Value,
                DeezerTrackId: detail.Id,
                DurationSeconds: detail.DurationS > 0 ? detail.DurationS : null));
            row = _store.Get(id)!;

            if (OwnedCopy(row, key) is { } copy)
            {
                _log.Created(id, "song", userKey, row.Artist, row.Title, RequestState.Ready);
                row = MarkReadyLocked(row, copy.ItemId, key, now, notify);
            }
            else
            {
                _log.Created(id, "song", userKey, row.Artist, row.Title, RequestState.Queued);
                if (others.Count > 0)
                {
                    _log.Joined(id, key.Value, "request");
                }

                row = SubmitLocked(row, key, now, notify) ?? row;
            }
        }

        return (new RequestOutcome(StatusCodes.Status201Created, row), notify);
    }

    /// <summary>One request for its owner or an admin; 404 otherwise (another person's request is not revealed).</summary>
    public RequestOutcome Get(RequestUser user, string id)
    {
        ArgumentNullException.ThrowIfNull(user);
        var row = string.IsNullOrWhiteSpace(id) ? null : _store.Get(id);
        return row is not null && MayAccess(user, row)
            ? new RequestOutcome(StatusCodes.Status200OK, row)
            : RequestOutcome.Fail(StatusCodes.Status404NotFound, RequestCodes.NotFound);
    }

    /// <summary>The caller's requests, or everyone's for an admin (<paramref name="all"/>), newest first, at most 200.</summary>
    public IReadOnlyList<RequestRow> List(RequestUser user, bool all, bool activeOnly)
    {
        ArgumentNullException.ThrowIfNull(user);
        return all
            ? _store.ListAll(activeOnly, limit: 200)
            : _store.ListByUser(RequestUserKey.Of(user.Id), activeOnly, limit: 200);
    }

    /// <summary>
    /// DELETE /requests/{id}: the owner while queued or searching, an admin in any active state (design 4). The fetch pick is
    /// abandoned (a queued or searching job no other pick holds is cancelled; a later stage finishes for the radio's sake).
    /// </summary>
    public RequestOutcome Cancel(RequestUser user, string id)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var row = string.IsNullOrWhiteSpace(id) ? null : _store.Get(id);
            if (row is null || !MayAccess(user, row))
            {
                return RequestOutcome.Fail(StatusCodes.Status404NotFound, RequestCodes.NotFound);
            }

            var early = row.State is RequestState.Queued or RequestState.Searching;
            if (!IsActive(row.State) || (!user.IsAdmin && !early))
            {
                return RequestOutcome.Fail(StatusCodes.Status409Conflict, RequestCodes.Conflict);
            }

            var pick = PickId(row.Id);
            if (_submitted.Remove(row.Id))
            {
                _fetch.Abandon(pick, "request_cancelled");
                _fetch.Forget(pick);
            }

            var by = RequestUserKey.Of(user.Id);
            var now = _time.GetUtcNow();
            _store.Update(row.Id, RequestState.Cancelled, now, new RequestUpdate(Detail: "Cancelled", CancelledBy: by, ClearNextTry: true));
            _log.StateChanged(row.Id, row.State, RequestState.Cancelled, "Cancelled");
            _log.Cancelled(row.Id, by);
            return new RequestOutcome(StatusCodes.Status200OK, _store.Get(row.Id));
        }
    }

    /// <summary>
    /// POST /requests/{id}/retry: owner or admin, only a failed request; back to queued with the attempts reset. A person
    /// who is at the daily cap cannot retry (429); an admin always can.
    /// </summary>
    public (RequestOutcome Outcome, IReadOnlyList<RequestRow> Notify) Retry(RequestUser user, string id)
    {
        ArgumentNullException.ThrowIfNull(user);
        var notify = new List<RequestRow>();
        lock (_gate)
        {
            var row = string.IsNullOrWhiteSpace(id) ? null : _store.Get(id);
            if (row is null || !MayAccess(user, row))
            {
                return (RequestOutcome.Fail(StatusCodes.Status404NotFound, RequestCodes.NotFound), []);
            }

            if (row.State != RequestState.Failed)
            {
                return (RequestOutcome.Fail(StatusCodes.Status409Conflict, RequestCodes.Conflict), []);
            }

            if (row.Kind != "song")
            {
                return (RequestOutcome.Fail(StatusCodes.Status503ServiceUnavailable, RequestCodes.AlbumsUnavailable), []);
            }

            if (_settings.Mode == FetchMode.Off)
            {
                return (RequestOutcome.Fail(StatusCodes.Status503ServiceUnavailable, RequestCodes.SongsUnavailable), []);
            }

            if (OverCap(user, RequestUserKey.Of(user.Id)))
            {
                return (RequestOutcome.Fail(StatusCodes.Status429TooManyRequests, RequestCodes.DailyLimit), []);
            }

            var now = _time.GetUtcNow();
            _store.Update(row.Id, RequestState.Queued, now, new RequestUpdate(
                Detail: Details.Queued, Attempts: 0, ClearNextTry: true, ClearFailReason: true));
            _log.StateChanged(row.Id, row.State, RequestState.Queued, "retry");
            row = _store.Get(row.Id)!;
            if (TrackKey.TryParse(row.TrackKey, out var key))
            {
                row = OwnedCopy(row, key) is { } copy
                    ? MarkReadyLocked(row, copy.ItemId, key, now, notify)
                    : SubmitLocked(row, key, now, notify) ?? row;
            }

            return (new RequestOutcome(StatusCodes.Status200OK, row), notify);
        }
    }

    /// <summary>
    /// One lifecycle pass over the active song requests (design 6.3): an owned song is ready; a held pick's readiness
    /// moves the state; a failed try waits 1 h, then 6 h, then fails; a request with no live pick (a start, a retry
    /// due, fetching unavailable at create time) is submitted again. One bad row never stops the pass.
    /// </summary>
    public RequestTickResult Tick()
    {
        var notify = new List<RequestRow>();
        var submitted = 0;
        lock (_gate)
        {
            IReadOnlyList<RequestRow> active;
            try
            {
                active = _store.Active();
            }
            catch (StoreException ex)
            {
                _log.WorkerError("list", ex.Message);
                return new RequestTickResult(0, []);
            }

            foreach (var row in active)
            {
                if (row.Kind != "song")
                {
                    continue;
                }

                try
                {
                    if (StepLocked(row, notify))
                    {
                        submitted++;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.WorkerError("song:" + row.Id, ex.GetType().Name);
                }
            }
        }

        return new RequestTickResult(submitted, notify);
    }

    /// <summary>Moves one active song request on. True when it was (re)submitted to the fetch side in this step.</summary>
    private bool StepLocked(RequestRow row, List<RequestRow> notify)
    {
        var now = _time.GetUtcNow();
        if (!TrackKey.TryParse(row.TrackKey, out var key))
        {
            FailLocked(row, FetchReasons.Internal, row.Attempts, now, notify);
            return false;
        }

        var pick = PickId(row.Id);
        var held = _submitted.Contains(row.Id);

        // The song is on the server (found by the owned-index refresh, for example after an ingest timeout).
        if (OwnedCopy(row, key) is { } copy)
        {
            if (held)
            {
                _submitted.Remove(row.Id);
                _fetch.Forget(pick);
            }

            MarkReadyLocked(row, copy.ItemId, key, now, notify);
            return false;
        }

        if (!held)
        {
            if (row.NextTryUtc is { } next && next > now)
            {
                return false;
            }

            return SubmitLocked(row, key, now, notify) is not null && _submitted.Contains(row.Id);
        }

        switch (_fetch.GetReadiness(pick))
        {
            case FetchReadiness.Pending pending:
                var (state, detail) = Map(pending);
                MoveLocked(row, state, detail, now);
                break;
            case FetchReadiness.Ready ready:
                _submitted.Remove(row.Id);
                _fetch.Forget(pick);
                MarkReadyLocked(row, ready.ItemId, key, now, notify);
                break;
            case FetchReadiness.Failed failed:
                _submitted.Remove(row.Id);
                _fetch.Forget(pick);
                FailedTryLocked(row, failed.Reason, now, notify);
                break;
        }

        return false;
    }

    /// <summary>Hands the request to the fetch side. Returns the updated row, or null when it could not be submitted now (it stays queued, or fails when fetching is off).</summary>
    private RequestRow? SubmitLocked(RequestRow row, TrackKey key, DateTimeOffset now, List<RequestRow> notify)
    {
        var candidate = new Candidate(
            key, row.Artist, row.Title ?? string.Empty, row.Album, row.DurationSeconds,
            row.DeezerTrackId, null, null, CandidateOrigin.Request, 0);
        var request = new FetchRequest(
            PickId(row.Id), RunId, string.Empty, ParseUser(row.UserId), candidate,
            DryRun: _settings.Mode == FetchMode.DryRun,
            Deadline: now + FetchLimits.RequestDeadline,
            Priority: FetchPriority.Request,
            Preference: FetchPreference.FlacFirst);

        if (_fetch.Request(request))
        {
            _submitted.Add(row.Id);
            _store.Update(row.Id, RequestState.Queued, now, new RequestUpdate(Detail: Details.Queued, ClearNextTry: true));
            if (row.State != RequestState.Queued)
            {
                _log.StateChanged(row.Id, row.State, RequestState.Queued, Details.Queued);
            }

            return _store.Get(row.Id);
        }

        var reason = _fetch.Availability.Reason;
        if (reason == AvailabilityReasons.Off || _settings.Mode == FetchMode.Off)
        {
            FailLocked(row, FetchReasons.Off, row.Attempts, now, notify);
            return null;
        }

        // Fetching is unavailable for now (or the file waits for Jellyfin already): the request waits and the next pass tries again.
        var detail = reason is null ? Details.WaitingForJellyfin : Details.Unavailable(reason);
        MoveLocked(row, RequestState.Queued, detail, now);
        return null;
    }

    /// <summary>A try failed: final reasons and the third failure fail the request; others wait 1 h, then 6 h.</summary>
    private void FailedTryLocked(RequestRow row, string reason, DateTimeOffset now, List<RequestRow> notify)
    {
        if (reason == FetchReasons.DryRun)
        {
            FailLocked(row, reason, row.Attempts + 1, now, notify, $"dry run: would download {row.Artist} - {row.Title}");
            return;
        }

        if (FinalReasons.Contains(reason))
        {
            FailLocked(row, reason, row.Attempts + 1, now, notify);
            return;
        }

        if (reason is FetchReasons.Unknown or FetchReasons.Restart)
        {
            // The fetch side lost the pick (not a try of its own): the next pass submits it again.
            return;
        }

        var attempts = row.Attempts + 1;
        if (attempts >= MaxAttempts)
        {
            FailLocked(row, reason, attempts, now, notify);
            return;
        }

        var at = now + RetryWaits[Math.Min(attempts, RetryWaits.Length) - 1];
        var ingest = reason == FetchReasons.IngestTimeout;
        var state = ingest ? RequestState.Importing : RequestState.Queued;
        var detail = ingest
            ? "Downloaded; waiting for Jellyfin to list it"
            : string.Create(CultureInfo.InvariantCulture, $"Not found yet, trying again at {at.UtcDateTime:HH:mm} UTC");
        _store.Update(row.Id, state, now, new RequestUpdate(Detail: detail, FailReason: reason, Attempts: attempts, NextTryUtc: at));
        if (row.State != state)
        {
            _log.StateChanged(row.Id, row.State, state, detail);
        }

        _log.RetryScheduled(row.Id, attempts, at);
    }

    private void FailLocked(RequestRow row, string reason, int attempts, DateTimeOffset now, List<RequestRow> notify, string? detail = null)
    {
        detail ??= Details.Failure(reason);
        _store.Update(row.Id, RequestState.Failed, now, new RequestUpdate(Detail: detail, FailReason: reason, Attempts: attempts, ClearNextTry: true));
        _log.StateChanged(row.Id, row.State, RequestState.Failed, detail);
        _log.Failed(row.Id, reason, attempts);
        if (_store.Get(row.Id) is { } failed)
        {
            notify.Add(failed);
        }
    }

    private RequestRow MarkReadyLocked(RequestRow row, Guid itemId, TrackKey key, DateTimeOffset now, List<RequestRow> notify)
    {
        var item = RequestUserKey.Of(itemId);
        _store.Update(row.Id, RequestState.Ready, now, new RequestUpdate(
            Detail: "Ready", JellyfinItemId: item, ReadyUtc: now, ClearNextTry: true, ClearFailReason: true));
        try
        {
            // The radio-fetched file (if this song is one) is kept for good (Phase 4 retention reads this).
            if ((_fetchStore?.MarkRequested(key.Value) ?? 0) > 0)
            {
                _log.Joined(row.Id, key.Value, "fetched_file");
            }
        }
        catch (StoreException ex)
        {
            _log.WorkerError("mark_requested", ex.Message);
        }

        _log.StateChanged(row.Id, row.State, RequestState.Ready, "Ready");
        _log.Ready(row.Id, item, (long)Math.Max(0, (now - row.CreatedUtc).TotalMilliseconds));
        var ready = _store.Get(row.Id) ?? row;
        notify.Add(ready);
        return ready;
    }

    /// <summary>Writes a state and detail when either changed.</summary>
    private void MoveLocked(RequestRow row, string state, string detail, DateTimeOffset now)
    {
        if (row.State == state && row.Detail == detail)
        {
            return;
        }

        _store.Update(row.Id, state, now, new RequestUpdate(Detail: detail));
        if (row.State != state)
        {
            _log.StateChanged(row.Id, row.State, state, detail);
        }
    }

    private OwnedEntry? OwnedCopy(RequestRow row, TrackKey key) =>
        _owned is { IsLoaded: true } owned ? owned.Resolve(key, row.DurationSeconds) : null;

    private bool OverCap(RequestUser user, string userKey)
    {
        if (user.IsAdmin)
        {
            return false;
        }

        var used = _store.CountCreatedSince(userKey, "song", _time.GetUtcNow() - TimeSpan.FromHours(24));
        if (used < _settings.SongsPerDay)
        {
            return false;
        }

        _log.Limit(userKey, "song", _settings.SongsPerDay);
        return true;
    }

    private static bool MayAccess(RequestUser user, RequestRow row) => user.IsAdmin || row.UserId == RequestUserKey.Of(user.Id);

    public static bool IsActive(string state) =>
        state is RequestState.Queued or RequestState.Searching or RequestState.Downloading or RequestState.Importing;

    /// <summary>The request state and UI detail for a pending fetch readiness (design 6.3).</summary>
    public static (string State, string Detail) Map(FetchReadiness.Pending pending) => pending.Stage switch
    {
        FetchStage.Queued when pending.Waiting == FetchWaiting.HourlyCap => (RequestState.Queued, "Waiting: hourly download limit"),
        FetchStage.Queued => (RequestState.Queued, Details.Queued),
        FetchStage.Searching => (RequestState.Searching, "Searching Soulseek"),
        FetchStage.Downloading => (RequestState.Downloading, "Downloading from a peer"),
        FetchStage.Importing => (RequestState.Importing, "Adding to the library"),
        _ => (RequestState.Importing, "Waiting for Jellyfin to list it"),
    };

    private static Guid ParseUser(string userId) => Guid.TryParseExact(userId, "N", out var id) ? id : Guid.Empty;

    private static int? YearOf(string? date)
    {
        if (date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year > 0)
        {
            return year;
        }

        return null;
    }

    /// <summary>Plain-text details for the UI.</summary>
    public static class Details
    {
        public const string Queued = "Waiting for a download slot";

        public const string WaitingForJellyfin = "Waiting for an earlier download to show up in Jellyfin";

        public static string Unavailable(string reason) => reason switch
        {
            AvailabilityReasons.HourlyCap => "Waiting: hourly download limit",
            AvailabilityReasons.IndexNotReady => "Waiting: Encore is still reading the library",
            AvailabilityReasons.DiskLow => "Waiting: the music disk is nearly full",
            _ => "Waiting: downloads are unavailable right now (" + reason + ")",
        };

        public static string Failure(string reason) => reason switch
        {
            FetchFailure.NoResults => "Not found on Soulseek",
            FetchFailure.NoAcceptable => "No good enough copy found on Soulseek",
            FetchFailure.AllPeersFailed => "Every download attempt failed",
            FetchFailure.Deadline => "Took too long",
            FetchFailure.Budget => "Too many searches right now",
            FetchFailure.SlskdUnavailable => "Soulseek was not reachable",
            FetchReasons.IngestTimeout => "Downloaded, but Jellyfin did not list it",
            FetchReasons.ImportFailed => "The download could not be added to the library",
            FetchReasons.NotScanned => "The radio folder is not in a Jellyfin library",
            FetchReasons.Off => "Song downloads are switched off",
            _ => "Failed (" + reason + ")",
        };
    }
}
