namespace Encore.Watch;

/// <summary>
/// Reconnect delays: 1, 2, 5, 10, 30 s, then 60 s for every later attempt, each with ±20% jitter.
/// A connection that stayed up for 60 s resets the sequence. A rejected key (401/403) waits the full cap.
/// </summary>
public sealed class ReconnectBackoff
{
    public static readonly IReadOnlyList<TimeSpan> DefaultSteps =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    public static readonly TimeSpan DefaultResetAfter = TimeSpan.FromSeconds(60);

    public const double DefaultJitter = 0.2;

    private readonly IReadOnlyList<TimeSpan> _steps;
    private readonly TimeSpan _resetAfter;
    private readonly double _jitter;
    private readonly Func<double> _random;
    private int _index;

    /// <param name="random">Returns a value in [0, 1). Tests pass a fixed value.</param>
    public ReconnectBackoff(IReadOnlyList<TimeSpan>? steps = null, TimeSpan? resetAfter = null, double jitter = DefaultJitter, Func<double>? random = null)
    {
        _steps = steps is { Count: > 0 } ? steps : DefaultSteps;
        _resetAfter = resetAfter ?? DefaultResetAfter;
        _jitter = Math.Clamp(jitter, 0, 1);
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>The delay before the next attempt.</summary>
    /// <param name="authFailed">The last attempt was refused with 401/403.</param>
    /// <param name="uptime">How long the last connection stayed open (zero if it never opened).</param>
    public TimeSpan Next(bool authFailed, TimeSpan uptime)
    {
        if (uptime >= _resetAfter)
        {
            _index = 0;
        }

        TimeSpan delay;
        if (authFailed)
        {
            delay = _steps[^1];
            _index = _steps.Count - 1;
        }
        else
        {
            delay = _steps[Math.Min(_index, _steps.Count - 1)];
            _index = Math.Min(_index + 1, _steps.Count - 1);
        }

        var factor = 1 + ((_random() * 2) - 1) * _jitter;
        return TimeSpan.FromTicks((long)(delay.Ticks * factor));
    }
}
