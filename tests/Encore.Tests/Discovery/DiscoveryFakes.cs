using System.Net;
using System.Text;

namespace Encore.Tests.Discovery;

/// <summary>A clock that moves only when told to, including its timers (so Task.Delay and timeouts follow it).</summary>
internal sealed class ManualClock : TimeProvider
{
    private sealed class FakeTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due = DateTimeOffset.MaxValue;
        public TimeSpan Period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                Period = period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Timers that are waiting to fire (a Task.Delay or a request timeout in progress).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due != DateTimeOffset.MaxValue);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that falls due on the way, in time order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due > _now ? next.Due : _now;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : DateTimeOffset.MaxValue;
            }

            next.Fire();
        }
    }
}

internal sealed record Sent(string PathAndQuery, bool HasAuthorization);

/// <summary>Fake Deezer or ListenBrainz: a function answers each request; every request is recorded.</summary>
internal sealed class FuncHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    private int _count;

    public List<Sent> Sent { get; } = [];

    public int Calls => Volatile.Read(ref _count);

    public static FuncHandler Always(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new((r, _, _) => Task.FromResult(answer(r)));

    public static FuncHandler Script(params Func<HttpResponseMessage>[] steps) =>
        new((_, n, _) => Task.FromResult(steps[Math.Min(n, steps.Length - 1)]()));

    public static FuncHandler Hanging() =>
        new(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, "{}");
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _count) - 1;
        lock (Sent)
        {
            Sent.Add(new Sent(request.RequestUri!.PathAndQuery, request.Headers.Authorization is not null || request.Headers.Contains("Authorization")));
        }

        return answer(request, n, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}
