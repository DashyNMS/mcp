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

public sealed record Timeline(IReadOnlyList<TimelineEntry> Entries, int Omitted, int Events, int AlertChanges, int Outages, IReadOnlyList<Unavailable> Unavailable);

/// <summary>
/// A device's event log, alert log and outages for a window, merged newest
/// first - the LibreNMS half of device_events and what_changed.
/// </summary>
public sealed class DeviceTimeline(ILibreNmsClient client, AgentOptions options, TimeProvider time)
{
    public const int MaxHours = 168;

    /// <summary>How far back each log is read; the window filter then keeps what falls inside it.</summary>
    private const int EventLogDepth = 200;
    private const int AlertLogDepth = 100;

    public static int CheckHours(int hours) => hours is >= 1 and <= MaxHours
        ? hours
        : throw new ToolException($"Hours must be between 1 and {MaxHours} (a week).");

    public async Task<Timeline> BuildAsync(Device device, int hours, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var id = device.DeviceId;
        var since = time.GetUtcNow().UtcDateTime.AddHours(-hours);

        var events = Sources.TryAsync(() => client.Logs.ListEventLogAsync(id, EventLogDepth, cancellationToken), cancellationToken);
        var alertLog = Sources.TryAsync(() => client.Logs.ListAlertLogAsync(id, AlertLogDepth, cancellationToken), cancellationToken);
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

        foreach (var e in events.Result.Value ?? [])
        {
            if (Sources.Utc(e.Timestamp, options) is { } at && at >= since)
            {
                entries.Add(new TimelineEntry(at, "eventlog", e.Type, EventSeverity(e.Severity), Text: e.Message, User: string.IsNullOrWhiteSpace(e.Username) ? null : e.Username));
            }
        }

        foreach (var a in alertLog.Result.Value ?? [])
        {
            if (Sources.Utc(a.TimeLogged, options) is { } at && at >= since)
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

            if (down is { } wentDown && wentDown >= since)
            {
                entries.Add(new TimelineEntry(wentDown, "outage", "went_down", DurationMinutes: minutes));
                outageCount++;
            }

            if (up is { } cameBack && cameBack >= since)
            {
                entries.Add(new TimelineEntry(cameBack, "outage", "came_back", DurationMinutes: minutes));
            }
        }

        var ordered = entries.OrderByDescending(e => e.Time).ToList();
        var shown = ToolResult.Take(ordered, limit, out var omitted);

        return new Timeline(
            shown,
            omitted,
            entries.Count(e => e.Source == "eventlog"),
            entries.Count(e => e.Source == "alertlog"),
            outageCount,
            unavailable);
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
