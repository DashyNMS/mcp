using System.Globalization;
using System.Text.RegularExpressions;

namespace DashyNMS.Agent;

/// <summary>
/// The span of time a tool looks at, in UTC (#57): "since 6am", "the last
/// 3 days", or "between 02:00 and 04:00 yesterday".
/// </summary>
public sealed partial record TimeWindow(DateTime Since, DateTime Until)
{
    /// <summary>The longest window a tool accepts. LibreNMS keeps its logs for a month by default, and its log APIs can only be read newest first, so longer would rarely be complete.</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(31);

    /// <summary>The "hours" shorthand's range, unchanged from before since/until existed.</summary>
    public const int MaxHours = 168;

    public TimeSpan Span => Until - Since;

    public bool Contains(DateTime utc) => utc >= Since && utc <= Until;

    /// <summary>
    /// Reads a tool's <c>since</c>, <c>until</c> and (older shorthand)
    /// <c>hours</c>. Each time is relative ("90m", "24h", "7d", "2w", meaning
    /// that long before now) or absolute ISO 8601 (a time without a zone is
    /// UTC). <c>until</c> defaults to now; with neither <c>since</c> nor
    /// <c>hours</c>, the window is the last <paramref name="defaultHours"/>.
    /// </summary>
    public static TimeWindow Resolve(string? since, string? until, int? hours, int defaultHours, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow().UtcDateTime;

        if (hours is not null && !string.IsNullOrWhiteSpace(since))
        {
            throw new ToolException("Give either hours or since, not both.");
        }

        if (hours is { } h && (h < 1 || h > MaxHours))
        {
            throw new ToolException($"Hours must be between 1 and {MaxHours} (a week). For a longer or older window, use since and until.");
        }

        var end = string.IsNullOrWhiteSpace(until) ? now : Parse(until, "until", now);
        if (end > now)
        {
            end = now;
        }

        // hours counts back from until, so "6 hours up to 04:00" works too.
        var start = string.IsNullOrWhiteSpace(since)
            ? end.AddHours(-(hours ?? defaultHours))
            : Parse(since, "since", now);

        if (start >= end)
        {
            throw new ToolException("Since must be before until.");
        }

        if (end - start > MaxSpan)
        {
            throw new ToolException($"The window can be at most {MaxSpan.TotalDays:0} days long.");
        }

        return new TimeWindow(start, end);
    }

    private static DateTime Parse(string text, string name, DateTime now)
    {
        var value = text.Trim();

        var relative = Relative().Match(value);
        if (relative.Success)
        {
            var amount = int.Parse(relative.Groups["n"].Value, CultureInfo.InvariantCulture);
            var span = relative.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(amount),
                "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(amount),
                "d" or "day" or "days" => TimeSpan.FromDays(amount),
                _ => TimeSpan.FromDays(7 * amount),
            };
            return now - span;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var absolute))
        {
            return absolute.UtcDateTime;
        }

        throw new ToolException($"{name} must be a time like 2026-10-17T06:00:00Z, or a duration before now like 90m, 24h or 7d.");
    }

    [GeneratedRegex(@"^(?<n>\d{1,5})\s*(?<unit>m|mins?|minutes?|h|hrs?|hours?|d|days?|w|weeks?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Relative();
}
