using DashyNMS.Agent.Devices;

namespace DashyNMS.Agent.Events;

public sealed record EventWindowInfo(DateTime Since, DateTime Until, bool WindowComplete, DateTime? CoveredSince)
{
    /// <summary>The part of the window that wasn't read, and why, when that's known (#70).</summary>
    public EventGap? NotCovered { get; init; }

    internal static EventWindowInfo For(TimeWindow window, bool complete, DateTime? covered, string? stopped)
        => new(window.Since, window.Until, complete, complete ? null : covered)
        {
            // A read from now may not have got as far back as the window's end.
            NotCovered = !complete && covered is { } until
                ? new EventGap(window.Since, until < window.Until ? until : window.Until, stopped ?? EventGap.DepthLimit)
                : null,
        };
}

/// <param name="Reason">
/// depth_limit (the log can only be read from its newest entry, and no further back),
/// read_limit (more entries than one call reads), time_limit (reading further took too
/// long) or source_failed (LibreNMS stopped answering part way). Events and alert changes
/// can then be asked for over the gap alone, with <paramref name="Until"/> as the window's
/// end. A depth_limit gap can't, nor can one in the alerts raised: they're read from now.
/// </param>
public sealed record EventGap(DateTime Since, DateTime Until, string Reason)
{
    internal const string DepthLimit = "depth_limit";
}

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
    /// <param name="canContinue">Whether asking for the part missing, with until, reads on from there.</param>
    internal static string Shortfall(string logs, DateTime covered, string? stopped, bool canContinue = true)
    {
        var plural = logs.EndsWith('s');
        var reach = stopped is null ? (plural ? "only reach" : "only reaches") : (plural ? "were read" : "was read");
        var rest = canContinue ? "ask for the rest with until set to then" : "ask about a window closer to now";
        var advice = stopped switch
        {
            LogReads.ReadLimit => $" More entries are in it than one call reads: narrow it by {(canContinue ? "type, " : string.Empty)}device, group or location, or {rest}.",
            LogReads.TimeLimit => $" Reading further took too long: narrow it, or {rest}.",
            LogReads.SourceFailed => " LibreNMS stopped answering part way: try again.",
            _ => string.Empty,
        };
        return $"LibreNMS's {logs} {reach} back to {covered:yyyy-MM-dd HH:mm} UTC, so the start of the window isn't covered.{advice}";
    }

    public static string TimelineSentence(Timeline result, TimeWindow window, DateTime now)
        => $"{Describe(window, now)}: {DeviceHealth.Count(result.Events, "event")}, " +
            $"{DeviceHealth.Count(result.AlertChanges, "alert change")}, {DeviceHealth.Count(result.Outages, "outage")}" +
            (result.Unavailable.Count > 0 ? $" ({DeviceHealth.Count(result.Unavailable.Count, "log")} couldn't be read)" : string.Empty) +
            (result.WindowComplete ? "." : result.CoveredSince is null
                ? ". History coverage is unknown because a source couldn't be read."
                : ". " + Shortfall("logs", result.CoveredSince.Value, result.Stopped));
}
