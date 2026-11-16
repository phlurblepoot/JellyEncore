namespace Encore.Requests.Search;

/// <summary>
/// A per-person search rate (review-v03 issue 4): at most <see cref="RequestLimits.SearchesPerMinute"/> searches in any
/// rolling minute. Memory only; a person who has not searched for a minute leaves no entry.
/// </summary>
public sealed class SearchRateLimiter(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Counts one search. Returns the wait until the next one is allowed, or null when it may go ahead.</summary>
    public TimeSpan? TryAcquire(string userKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_hits.Count > 1000)
            {
                foreach (var stale in _hits.Where(p => p.Value.Count == 0 || now - p.Value.Peek() >= Window).Select(p => p.Key).ToList())
                {
                    _hits.Remove(stale);
                }
            }

            if (!_hits.TryGetValue(userKey, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _hits[userKey] = queue;
            }

            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= RequestLimits.SearchesPerMinute)
            {
                return queue.Peek() + Window - now;
            }

            queue.Enqueue(now);
            return null;
        }
    }
}
