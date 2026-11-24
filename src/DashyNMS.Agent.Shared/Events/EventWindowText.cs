using DashyNMS.Agent.Devices;

namespace DashyNMS.Agent.Events;

public sealed record EventWindowInfo(DateTime Since, DateTime Until, bool WindowComplete, DateTime? CoveredSince)
{
    /// <summary>The part of the window that wasn't read, and why, when that's known (#70).</summary>
    public EventGap? NotCovered { get; init; }

    internal static EventWindowInfo For(TimeWindow window, bool complete, DateTime? covered, string? stopped)
        => new(window.Since, window.Until, complete, complete ? null : covered)
        {
            NotCovered = !complete && covered is { } until && stopped is not null ? new EventGap(window.Since, until, stopped) : null,
        };
}

/// <param name="Reason">
/// read_limit (more entries than one call reads), time_limit (reading further took
/// too long) or source_failed (LibreNMS stopped answering part way). The gap can be
/// asked for on its own, with <paramref name="Until"/> as the window's end.
/// </param>
public sealed record EventGap(DateTime Since, DateTime Until, string Reason);

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
        => EventWindowInfo.For(window, result.WindowComplete, result.CoveredSince, result.Stopped);

    /// <summary>How far back <paramref name="logs"/> reach, when that's short of the window, and what to do about it.</summary>
    internal static string Shortfall(string logs, DateTime covered, string? stopped)
        => $"LibreNMS's {logs} {(stopped is null ? "only reach" + (logs.EndsWith('s') ? string.Empty : "es") : (logs.EndsWith('s') ? "were" : "was") + " read")} back to {covered:yyyy-MM-dd HH:mm} UTC, " +
            "so the start of the window isn't covered." + stopped switch
            {
                LogReads.ReadLimit => " More entries are in it than one call reads: narrow it by type, device, group or location, or ask for the rest with until set to then.",
                LogReads.TimeLimit => " Reading further took too long: ask for the rest with until set to then.",
                LogReads.SourceFailed => " LibreNMS stopped answering part way: try again.",
                _ => string.Empty,
            };

    public static string TimelineSentence(Timeline result, TimeWindow window, DateTime now)
        => $"{Describe(window, now)}: {DeviceHealth.Count(result.Events, "event")}, " +
            $"{DeviceHealth.Count(result.AlertChanges, "alert change")}, {DeviceHealth.Count(result.Outages, "outage")}" +
            (result.Unavailable.Count > 0 ? $" ({DeviceHealth.Count(result.Unavailable.Count, "log")} couldn't be read)" : string.Empty) +
            (result.WindowComplete ? "." : result.CoveredSince is null
                ? ". History coverage is unknown because a source couldn't be read."
                : ". " + Shortfall("logs", result.CoveredSince.Value, result.Stopped));
}
