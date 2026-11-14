using Encore.Config;

namespace Encore.Slskd;

/// <summary>
/// The Soulseek search budget, shared by all users: at most <c>Searches</c> searches in any <c>Seconds</c> window
/// (ENCORE_SLSK_SEARCH_LIMIT) and at least <see cref="DefaultMinGap"/> between two searches. Slots are handed out in
/// order (first come, first served): each caller reserves the next free slot and waits for it.
/// A caller that is cancelled while waiting keeps its slot used; that only makes the budget more careful.
/// </summary>
public sealed class SearchBudget
{
    public static readonly TimeSpan DefaultMinGap = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly int _searches;
    private readonly TimeSpan _window;
    private readonly TimeSpan _minGap;
    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _slots = new();   // the last `_searches` reserved slot times, ascending
    private DateTimeOffset? _lastSlot;

    public SearchBudget(SearchLimit limit, TimeProvider? time = null, TimeSpan? minGap = null)
    {
        if (limit.Searches < 1 || limit.Seconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "searches and seconds must be at least 1");
        }

        _searches = limit.Searches;
        _window = TimeSpan.FromSeconds(limit.Seconds);
        _minGap = minGap ?? DefaultMinGap;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Searches whose slot is within the last window (including ones still waiting), for /health.</summary>
    public int SearchesInWindow
    {
        get
        {
            lock (_gate)
            {
                var from = _time.GetUtcNow() - _window;
                return _slots.Count(s => s > from);
            }
        }
    }

    /// <summary>
    /// Takes the next free slot and waits for it. Returns false, taking nothing, when the slot would come after
    /// <paramref name="deadline"/>.
    /// </summary>
    public async Task<bool> TryAcquireAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset slot;
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            slot = now;
            if (_lastSlot is { } last && last + _minGap > slot)
            {
                slot = last + _minGap;
            }

            if (_slots.Count >= _searches)
            {
                var limit = _slots.Peek() + _window;
                if (limit > slot)
                {
                    slot = limit;
                }
            }

            if (slot > deadline)
            {
                return false;
            }

            _slots.Enqueue(slot);
            while (_slots.Count > _searches)
            {
                _slots.Dequeue();
            }

            _lastSlot = slot;
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }

        return true;
    }
}
