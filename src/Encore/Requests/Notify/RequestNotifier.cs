using System.Collections.Concurrent;
using Encore.Jellyfin;
using Encore.Requests.Songs;
using Encore.Store;

namespace Encore.Requests.Notify;

/// <summary>
/// Tells the requester, through Jellyfin's session message, that a request is ready or failed (v0.3 design 9).
/// Only the requester's own sessions that support remote control get it. One message per request per state
/// (<c>notified_utc</c> records it); a 5xx is tried once more after 30 s in the background. A delivery problem is logged
/// as <c>notify.failed</c> and never changes the request. No session open: nothing is sent.
/// </summary>
public sealed class RequestNotifier : IRequestNotifier
{
    public const string Header = "Encore";
    public const int MessageTimeoutMs = 10_000;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IJellyfinClient _jellyfin;
    private readonly RequestStore _store;
    private readonly ILogger<RequestNotifier> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, bool> _inFlight = new();
    private readonly ConcurrentDictionary<Task, bool> _retries = new();

    public RequestNotifier(
        IJellyfinClient jellyfin, RequestStore store, ILogger<RequestNotifier> log, TimeProvider? time = null,
        TimeSpan? retryDelay = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _jellyfin = jellyfin ?? throw new ArgumentNullException(nameof(jellyfin));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>Completes when no background retry is waiting. For tests and shutdown.</summary>
    public Task IdleAsync() => Task.WhenAll(_retries.Keys.ToArray());

    public async Task NotifyAsync(RequestRow request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Work from the stored row: the state may have moved on since the caller took its copy.
        var row = _store.Get(request.Id);
        if (row is null || row.State is not ("ready" or "failed"))
        {
            return;
        }

        // Once per state: a notification newer than the last state change has already gone out.
        if (row.NotifiedUtc is { } notified && notified >= row.UpdatedUtc)
        {
            return;
        }

        var flight = $"{row.Id}:{row.State}";
        if (!_inFlight.TryAdd(flight, true))
        {
            return;
        }

        var keep = false;
        try
        {
            if (!Guid.TryParse(row.UserId, out var requester))
            {
                return;
            }

            IReadOnlyList<SessionInfoDto> sessions;
            try
            {
                sessions = await _jellyfin.GetSessionsAsync(ct).ConfigureAwait(false);
            }
            catch (JellyfinException ex)
            {
                _log.NotifyFailed(row.Id, "-", row.State, Describe(ex));
                return;
            }

            var targets = sessions.Where(s => s.UserId == requester && s.SupportsRemoteControl && !string.IsNullOrEmpty(s.Id)).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var text = TextFor(row);
            var sent = 0;
            var retry = new List<SessionInfoDto>();
            foreach (var session in targets)
            {
                var result = await TrySendAsync(row, session, text, ct).ConfigureAwait(false);
                if (result == Attempt.Sent)
                {
                    sent++;
                }
                else if (result == Attempt.ServerError)
                {
                    retry.Add(session);
                }
            }

            if (sent > 0)
            {
                Mark(row);
                _log.NotifySent(row.Id, sent, row.State);
            }

            if (retry.Count > 0)
            {
                keep = true;
                var task = RetryAsync(row, retry, text, sent > 0, ct);
                _retries[task] = true;
                _ = task.ContinueWith(t => _retries.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        finally
        {
            if (!keep)
            {
                _inFlight.TryRemove(flight, out _);
            }
        }
    }

    private async Task RetryAsync(RequestRow row, List<SessionInfoDto> sessions, string text, bool alreadyMarked, CancellationToken ct)
    {
        try
        {
            await _delay(_retryDelay, ct).ConfigureAwait(false);
            var sent = 0;
            foreach (var session in sessions)
            {
                if (await TrySendAsync(row, session, text, ct, final: true).ConfigureAwait(false) == Attempt.Sent)
                {
                    sent++;
                }
            }

            if (sent > 0)
            {
                if (!alreadyMarked)
                {
                    Mark(row);
                }

                _log.NotifySent(row.Id, sent, row.State);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _inFlight.TryRemove($"{row.Id}:{row.State}", out _);
        }
    }

    private async Task<Attempt> TrySendAsync(RequestRow row, SessionInfoDto session, string text, CancellationToken ct, bool final = false)
    {
        try
        {
            await _jellyfin.SendMessageAsync(session.Id, Header, text, MessageTimeoutMs, ct).ConfigureAwait(false);
            return Attempt.Sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (JellyfinException ex)
        {
            var serverError = ex.StatusCode is >= 500 and <= 599;
            if (final || !serverError)
            {
                _log.NotifyFailed(row.Id, session.Id, row.State, Describe(ex));
                return Attempt.Failed;
            }

            return Attempt.ServerError;
        }
    }

    private void Mark(RequestRow row)
    {
        var now = _time.GetUtcNow();
        // Same state back in: the update stamps updated_utc with the same instant, so notified_utc >= updated_utc.
        _store.Update(row.Id, row.State, now, new RequestUpdate(NotifiedUtc: now));
    }

    private static string TextFor(RequestRow row)
    {
        var what = string.IsNullOrWhiteSpace(row.Title ?? row.Album) ? row.Artist : $"{row.Artist} - {row.Title ?? row.Album}";
        return row.State == "ready" ? $"Ready: {what}" : $"Could not get: {what}";
    }

    private static string Describe(JellyfinException ex) => ex.StatusCode is { } code ? $"HTTP {code}" : ex.GetType().Name;

    private enum Attempt
    {
        Sent,
        ServerError,
        Failed,
    }
}
