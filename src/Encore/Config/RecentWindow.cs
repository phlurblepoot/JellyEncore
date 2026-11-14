using System.Globalization;
using System.Text.RegularExpressions;

namespace Encore.Config;

/// <summary>Exclusion window for repeated picks: either "the current radio session" or a fixed duration.</summary>
public sealed partial record RecentWindow
{
    public static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Max = TimeSpan.FromDays(30);

    public static RecentWindow Session { get; } = new();

    private RecentWindow() { }

    private RecentWindow(TimeSpan duration) => Duration = duration;

    public bool IsSession => Duration is null;

    public TimeSpan? Duration { get; }

    /// <summary>Parses <c>session</c> or a duration such as <c>30m</c>, <c>6h</c>, <c>2d</c> (1m to 30d).</summary>
    public static bool TryParse(string? text, out RecentWindow? window)
    {
        window = null;
        var value = text?.Trim() ?? "";
        if (value.Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            window = Session;
            return true;
        }

        var match = DurationPattern().Match(value);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        var duration = char.ToLowerInvariant(match.Groups[2].Value[0]) switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };
        if (duration < Min || duration > Max)
        {
            return false;
        }

        window = new RecentWindow(duration);
        return true;
    }

    public override string ToString() => IsSession ? "session" : FormatDuration(Duration!.Value);

    private static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 && d.TotalDays % 1 == 0 ? $"{(long)d.TotalDays}d"
        : d.TotalHours >= 1 && d.TotalHours % 1 == 0 ? $"{(long)d.TotalHours}h"
        : $"{(long)d.TotalMinutes}m";

    // At most 6 digits, so the TimeSpan conversion above cannot overflow.
    [GeneratedRegex(@"^(\d{1,6})([mhdMHD])$")]
    private static partial Regex DurationPattern();
}
