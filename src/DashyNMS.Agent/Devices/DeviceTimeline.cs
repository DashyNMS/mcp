using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>One thing that happened to a device, from whichever LibreNMS log said so.</summary>
/// <param name="Source">eventlog, alertlog or outage.</param>
/// <param name="Kind">The event log's type, the alert's new state, or went_down / came_back.</param>
/// <param name="Text">The event log's own message - device text, so it's data, not ours.</param>
public sealed record TimelineEntry(
    DateTime Time,
    string Source,
    string? Kind,
    string? Severity = null,
    string? Rule = null,
    string? Text = null,
    string? User = null,
    long? DurationMinutes = null);

/// <summary>A device's timeline for a window, newest first, with how much of the window it really covers.</summary>
/// <param name="CoveredSince">
/// The oldest time every log was read back to. Earlier than the window's
/// start when the window is complete; later when a log ran out first.
/// </param>
public sealed record Timeline(
    IReadOnlyList<TimelineEntry> Entries,
    int Events,
    int AlertChanges,
    int Outages,
    bool WindowComplete,
    DateTime? CoveredSince,
    IReadOnlyList<Unavailable> Unavailable);

/// <summary>
/// A device's event log, alert log and outages for a window, merged newest
/// first - the LibreNMS half of device_events and what_changed.
/// </summary>
/// <remarks>
/// LibreNMS's log APIs have no date filter, only "the newest N entries"
/// (#57), so each log is read back far enough to cover the window: a short
/// read first, a longer one when that doesn't reach the window's start, and
/// no further than <see cref="MaxDepth"/>. When even that stops short, the
/// result says so instead of passing off part of the window as all of it.
/// </remarks>
public sealed class DeviceTimeline(ILibreNmsClient client, AgentOptions options)
{
    public const int FirstDepth = 200;
    public const int MaxDepth = 2000;

    public async Task<Timeline> BuildAsync(Device device, TimeWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(window);
        var id = device.DeviceId;

        var events = ReadBackAsync(depth => client.Logs.ListEventLogAsync(id, depth, cancellationToken), e => Sources.Utc(e.Timestamp, options), window, cancellationToken);
        var alertLog = ReadBackAsync(depth => client.Logs.ListAlertLogAsync(id, depth, cancellationToken), a => Sources.Utc(a.TimeLogged, options), window, cancellationToken);
        var outages = Sources.TryAsync(() => client.Devices.GetOutagesAsync(id, cancellationToken), cancellationToken);
        var rules = Sources.TryAsync(() => client.Rules.ListAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(events, alertLog, outages, rules).ConfigureAwait(false);

        var unavailable = new List<Unavailable>();
        foreach (var (source, error) in new[] { ("event log", events.Result.Error), ("alert log", alertLog.Result.Error), ("outages", outages.Result.Error) })
        {
            if (error is not null)
            {
                unavailable.Add(new Unavailable(source, error));
            }
        }

        // Rule names are a nicety: without them an alert change still says "rule 12".
        var ruleNames = rules.Result.Value?.ToDictionary(r => r.Id, r => r.Name) ?? [];

        var entries = new List<TimelineEntry>();

        foreach (var e in events.Result.Value?.Items ?? [])
        {
            if (Sources.Utc(e.Timestamp, options) is { } at && window.Contains(at))
            {
                entries.Add(new TimelineEntry(at, "eventlog", e.Type, EventSeverity(e.Severity), Text: e.Message, User: string.IsNullOrWhiteSpace(e.Username) ? null : e.Username));
            }
        }

        foreach (var a in alertLog.Result.Value?.Items ?? [])
        {
            if (Sources.Utc(a.TimeLogged, options) is { } at && window.Contains(at))
            {
                var rule = ruleNames.TryGetValue(a.RuleId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"Rule {a.RuleId}";
                entries.Add(new TimelineEntry(at, "alertlog", a.State.ToString().ToLowerInvariant(), Rule: rule));
            }
        }

        var outageCount = 0;
        foreach (var o in outages.Result.Value ?? [])
        {
            // Outage times are Unix timestamps, already UTC.
            var down = o.GoingDown?.ToUniversalTime();
            var up = o.UpAgain?.ToUniversalTime();
            var minutes = down is { } d && up is { } u ? (long?)(u - d).TotalMinutes : null;

            if (down is { } wentDown && window.Contains(wentDown))
            {
                entries.Add(new TimelineEntry(wentDown, "outage", "went_down", DurationMinutes: minutes));
                outageCount++;
            }

            if (up is { } cameBack && window.Contains(cameBack))
            {
                entries.Add(new TimelineEntry(cameBack, "outage", "came_back", DurationMinutes: minutes));
            }
        }

        // The window is only as complete as the shortest log read: a log that
        // ran out before reaching the start leaves the gap unknown.
        var covered = new[] { events.Result.Value?.CoveredSince, alertLog.Result.Value?.CoveredSince }
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .DefaultIfEmpty(window.Since)
            .Max();

        return new Timeline(
            entries.OrderByDescending(e => e.Time).ToList(),
            entries.Count(e => e.Source == "eventlog"),
            entries.Count(e => e.Source == "alertlog"),
            outageCount,
            covered <= window.Since,
            covered,
            unavailable);
    }

    /// <summary>A log read back as far as the window needs, newest first, and how far it reached.</summary>
    public sealed record ReadBack<T>(IReadOnlyList<T> Items, DateTime CoveredSince);

    /// <summary>
    /// Reads a newest-first log until it reaches the window's start or
    /// <see cref="MaxDepth"/>. A log that returns fewer entries than asked for
    /// has nothing older, so it covers everything.
    /// </summary>
    public static Task<Fetched<ReadBack<T>>> ReadBackAsync<T>(
        Func<int, Task<IReadOnlyList<T>>> read,
        Func<T, DateTime?> timeOf,
        TimeWindow window,
        CancellationToken cancellationToken)
        => Sources.TryAsync(async () =>
        {
            var depth = FirstDepth;
            while (true)
            {
                var items = await read(depth).ConfigureAwait(false);
                var oldest = items.Select(timeOf).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();

                if (items.Count < depth)
                {
                    return new ReadBack<T>(items, DateTime.MinValue);
                }

                if (oldest <= window.Since || depth >= MaxDepth)
                {
                    return new ReadBack<T>(items, oldest);
                }

                depth = Math.Min(depth * 5, MaxDepth);
            }
        }, cancellationToken);

    /// <summary>LibreNMS's event log severities (1 to 5, as its web UI colours them).</summary>
    private static string? EventSeverity(int? severity) => severity switch
    {
        1 => "ok",
        2 => "info",
        3 => "notice",
        4 => "warning",
        5 => "critical",
        _ => null,
    };
}
