using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>One thing that happened to a device, from whichever LibreNMS log said so.</summary>
/// <param name="Source">eventlog, alertlog or outage.</param>
/// <param name="Kind">The event log's type; for an alert log row, what it means for its alert (active, acknowledged, worse, better, changed, recovered, or fault_added / fault_cleared for one fault of several - #77); or went_down / came_back.</param>
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
/// start when the window is complete; later when a log ran out first. Null when
/// any history source failed, because common coverage cannot be established.
/// </param>
public sealed record Timeline(
    IReadOnlyList<TimelineEntry> Entries,
    int Events,
    int AlertChanges,
    int Outages,
    bool WindowComplete,
    DateTime? CoveredSince,
    IReadOnlyList<Unavailable> Unavailable)
{
    /// <summary>Why the logs weren't read back to the window's start, when that's known (<see cref="LogReads"/>).</summary>
    public string? Stopped { get; init; }
}

/// <summary>
/// A device's event log, alert log and outages for a window, merged newest
/// first - the LibreNMS half of device_events and what_changed.
/// </summary>
/// <remarks>
/// Each log is read back far enough to cover the window (<see cref="LogReads"/>).
/// When that stops short, the result says so instead of passing off part of the
/// window as all of it.
/// </remarks>
public sealed class DeviceTimeline(ILibreNmsClient client, AgentOptions options, TimeProvider time)
{
    public DeviceTimeline(ILibreNmsClient client, AgentOptions options)
        : this(client, options, TimeProvider.System)
    {
    }

    public const int FirstDepth = 200;
    public const int MaxDepth = 2000;

    public async Task<Timeline> BuildAsync(Device device, TimeWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(window);
        var id = device.DeviceId;

        var logs = new LogReads(options, time);
        var events = logs.EventsAsync(client.Logs, id, window, keep: null, cancellationToken);
        var alertLog = logs.AlertsAsync(client.Logs, id, window, keep: null, toNow: false, cancellationToken);
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

        // What each row means for its alert (#77): one fault of several clearing isn't the alert recovering.
        var alertKinds = DashyNMS.Agent.Summaries.AlertLogKinds.Describe(alertLog.Result.Value?.Items ?? []);
        foreach (var a in alertLog.Result.Value?.Items ?? [])
        {
            if (Sources.Utc(a.TimeLogged, options) is { } at && window.Contains(at))
            {
                var rule = ruleNames.TryGetValue(a.RuleId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"Rule {a.RuleId}";
                entries.Add(new TimelineEntry(at, "alertlog", alertKinds.GetValueOrDefault(a, a.State.ToString().ToLowerInvariant()), Rule: rule));
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
            unavailable.Count == 0 && covered <= window.Since,
            unavailable.Count == 0 ? covered : null,
            unavailable)
        {
            Stopped = unavailable.Count > 0 ? null
                : events.Result.Value?.StoppedAt(covered) ?? alertLog.Result.Value?.StoppedAt(covered),
        };
    }

    /// <summary>A log read back as far as the window needs, newest first, and how far it reached.</summary>
    public sealed record ReadBack<T>(IReadOnlyList<T> Items, DateTime CoveredSince)
    {
        /// <summary>Why it stopped before the window's start, when a limit of ours or a failure did it.</summary>
        public string? Stopped { get; init; }

        /// <summary><see cref="Stopped"/>, when this log is the one that set how far back an answer reaches.</summary>
        internal string? StoppedAt(DateTime covered) => CoveredSince == covered ? Stopped : null;
    }

    /// <summary>
    /// Reads a newest-first log until it reaches the window's start or
    /// <see cref="MaxDepth"/>. A log that returns fewer entries than asked for
    /// has nothing older, so it covers everything. Only for a log that can't be
    /// read back from a time; <see cref="LogReads"/> reads the rest.
    /// </summary>
    public static Task<Fetched<ReadBack<T>>> ReadBackAsync<T>(
        Func<int, Task<IReadOnlyList<T>>> read,
        Func<T, DateTime?> timeOf,
        TimeWindow window,
        CancellationToken cancellationToken)
        => Sources.TryAsync(() => ReadBackCoreAsync(read, timeOf, window), cancellationToken);

    internal static async Task<ReadBack<T>> ReadBackCoreAsync<T>(Func<int, Task<IReadOnlyList<T>>> read, Func<T, DateTime?> timeOf, TimeWindow window)
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
    }

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
