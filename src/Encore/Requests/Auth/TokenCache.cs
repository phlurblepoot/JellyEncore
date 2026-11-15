namespace Encore.Requests.Auth;

/// <summary>
/// Validated-token cache (design 3.2): keyed by the SHA-256 hex of the token, never the token. A good answer lives
/// 60 s, a rejected token 10 s; at most 1000 entries, least recently used out first.
/// </summary>
internal sealed class TokenCache(TimeProvider time)
{
    internal sealed record Entry(RequestUser? User, DateTimeOffset Expires);

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Entry Entry)>> _map = [];
    private readonly LinkedList<(string Key, Entry Entry)> _order = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>The live entry (User null = a cached rejection), or null on a miss or an expired entry.</summary>
    public Entry? Get(string key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node.Value.Entry.Expires <= time.GetUtcNow())
            {
                Remove(node);
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Entry;
        }
    }

    public void PutUser(string key, RequestUser user) => Put(key, new Entry(user, time.GetUtcNow() + RequestLimits.TokenCacheTtl));

    public void PutRejected(string key) => Put(key, new Entry(null, time.GetUtcNow() + RequestLimits.TokenNegativeTtl));

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                Remove(node);
            }
        }
    }

    private void Put(string key, Entry entry)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                Remove(existing);
            }

            var node = _order.AddFirst((key, entry));
            _map[key] = node;
            while (_map.Count > RequestLimits.TokenCacheMax)
            {
                Remove(_order.Last!);
            }
        }
    }

    private void Remove(LinkedListNode<(string Key, Entry Entry)> node)
    {
        _order.Remove(node);
        _map.Remove(node.Value.Key);
    }
}
