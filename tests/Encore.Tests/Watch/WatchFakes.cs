using System.Collections.Concurrent;
using System.Threading.Channels;
using Encore.Jellyfin;
using Encore.Watch;
using Microsoft.Extensions.Logging;

namespace Encore.Tests.Watch;

/// <summary>Scripted socket: tests push server messages in and read what Encore sent.</summary>
internal sealed class FakeSocket : ISessionSocket
{
    private readonly Channel<string?> _inbound = Channel.CreateUnbounded<string?>();

    public Func<CancellationToken, Task>? OnConnect { get; init; }

    public Uri? ConnectedUri { get; private set; }

    public string? Authorization { get; private set; }

    public ConcurrentQueue<string> Sent { get; } = new();

    public bool Disposed { get; private set; }

    public string? CloseStatus { get; set; }

    public string? CloseDescription { get; set; }

    public void Push(string message) => _inbound.Writer.TryWrite(message);

    /// <summary>The server closes the socket.</summary>
    public void Close(string status = "NormalClosure")
    {
        CloseStatus = status;
        _inbound.Writer.TryWrite(null);
    }

    public async Task ConnectAsync(Uri uri, string authorization, CancellationToken ct)
    {
        ConnectedUri = uri;
        Authorization = authorization;
        if (OnConnect is not null)
        {
            await OnConnect(ct);
        }
    }

    public Task SendTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Sent.Enqueue(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct) => await _inbound.Reader.ReadAsync(ct);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out sockets in order; after the scripted ones, every socket fails to connect.</summary>
internal sealed class FakeSocketFactory : ISessionSocketFactory
{
    private readonly ConcurrentQueue<FakeSocket> _script = new();

    public ConcurrentQueue<FakeSocket> Created { get; } = new();

    public FakeSocketFactory Then(FakeSocket socket)
    {
        _script.Enqueue(socket);
        return this;
    }

    public ISessionSocket Create()
    {
        var socket = _script.TryDequeue(out var next)
            ? next
            : new FakeSocket { OnConnect = _ => throw new JellyfinRequestException("socket", "connect failed (Faulted)") };
        Created.Enqueue(socket);
        return socket;
    }

    public static FakeSocket Refused() => new() { OnConnect = _ => throw new JellyfinRequestException("socket", "connect failed (Faulted)") };

    public static FakeSocket Unauthorized() => new() { OnConnect = _ => throw new JellyfinAuthException("socket", 401) };
}

/// <summary>Only GET /Sessions is used by the watcher.</summary>
internal sealed class FakeJellyfinClient : IJellyfinClient
{
    private int _calls;

    public Func<int, IReadOnlyList<SessionInfoDto>> Sessions { get; set; } = _ => [];

    public int SessionCalls => Volatile.Read(ref _calls);

    public Task<IReadOnlyList<SessionInfoDto>> GetSessionsAsync(CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref _calls);
        return Task.FromResult(Sessions(call));
    }

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarArtistsAsync(Guid artistId, Guid userId, int limit = 10, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSongsByArtistsAsync(IReadOnlyCollection<Guid> artistIds, Guid userId, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetSimilarSongsAsync(Guid songId, Guid userId, int limit = 30, IReadOnlyCollection<Guid>? excludeArtistIds = null, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetAudioPageAsync(int startIndex, int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<BaseItemDto>> GetItemsByIdsAsync(IReadOnlyCollection<Guid> ids, string fields, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<QueryResult<BaseItemDto>> GetInstantMixAsync(Guid songId, Guid userId, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();

    public Task PlayLastAsync(string sessionId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Captures log events with their structured fields.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<string, object?>();
        Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), fields));
    }

    public IEnumerable<LogEntry> Named(string name) => Entries.Where(e => e.EventId.Name == name);
}

internal static class Wait
{
    /// <summary>Polls a condition with a generous deadline, so real-time tests stay stable on a slow CI runner.</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }
}
