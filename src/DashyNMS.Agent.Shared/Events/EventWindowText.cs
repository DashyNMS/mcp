using DashyNMS.Agent.Devices;

namespace DashyNMS.Agent.Events;

public sealed record EventWindowInfo(DateTime Since, DateTime Until, bool WindowComplete, DateTime? CoveredSince);

/// <summary>Consistent coverage and time-window wording for all historical tools.</summary>
public static class EventWindowText
{
    public static readonly TimeSpan NowSlack = TimeSpan.FromMinutes(5);
    public static string Describe(TimeWindow window, DateTime now)
    {
        var span = window.Span;
        var length = span.TotalHours >= 48 && span.TotalHours % 24 == 0 ? DeviceHealth.Count((int)span.TotalDays, "day")
            : span.TotalMinutes >= 120 ? DeviceHealth.Count((int)Math.Round(span.TotalHours), "hour")
            : DeviceHealth.Count((int)Math.Round(span.TotalMinutes), "minute");
        return window.Until >= now - NowSlack ? $"In the last {length}" : $"In the {length} to {window.Until:yyyy-MM-dd HH:mm} UTC";
    }
    public static EventWindowInfo WindowData(TimeWindow window, Timeline result)
        => new(window.Since, window.Until, result.WindowComplete, result.WindowComplete ? null : result.CoveredSince);

    public static string TimelineSentence(Timeline result, TimeWindow window, DateTime now)
        => $"{Describe(window, now)}: {DeviceHealth.Count(result.Events, "event")}, " +
            $"{DeviceHealth.Count(result.AlertChanges, "alert change")}, {DeviceHealth.Count(result.Outages, "outage")}" +
            (result.Unavailable.Count > 0 ? $" ({DeviceHealth.Count(result.Unavailable.Count, "log")} couldn't be read)" : string.Empty) +
            (result.WindowComplete ? "." : $". LibreNMS's logs only reach back to {result.CoveredSince:yyyy-MM-dd HH:mm} UTC, so the start of the window isn't covered.");
}
