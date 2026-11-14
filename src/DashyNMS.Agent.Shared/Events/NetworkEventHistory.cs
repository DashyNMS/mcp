using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Events;

public sealed record NetworkEventsRequest(string? Log = null, int? Hours = null, string? Since = null, string? Until = null,
    string? Type = null, string? Severity = null, int? DeviceId = null, string? Group = null, string? Location = null, int Offset = 0, int Limit = 50);
public sealed record NetworkEventsData(EventWindowInfo Window, IReadOnlyList<NetworkEventHistory.NetworkEvent> Entries, PageInfo Page);
public sealed record NetworkEventsResult(string Summary, NetworkEventsData Data, bool RulesUnavailable);

/// <summary>Fleet event and alert logs, filtered and paged with explicit window coverage.</summary>
public sealed class NetworkEventHistory(ILibreNmsClient client, FleetCache fleet, DeviceDirectory directory, DeviceScope scopes,
    AgentOptions options, TimeProvider time, Redactor redactor)
{
    public const int MaxEvents = 100;
    public static NetworkEventHistory Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        return new(client, new FleetCache(client, time), directory, new DeviceScope(client, directory, time), options, time, new Redactor());
    }
    public async Task<NetworkEventsResult> ReadAsync(NetworkEventsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (logText, hours, since, until, type, severityText, deviceId, group, location, offset, limit) = request;
        if (deviceId is <= 0) throw new ToolException("A device ID must be positive.");
        var log = logText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "events" or "event" or "eventlog" => "events",
            "alerts" or "alert" or "alertlog" => "alerts",
            "both" or "all" => "both",
            _ => throw new ToolException("Log must be events, alerts or both."),
        };
        var window = TimeWindow.Resolve(since, until, hours, DeviceEventHistory.DefaultHours, time);
        Paging.Check(offset, limit, MaxEvents);
        var minimum = ParseEventSeverity(severityText);
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);

        bool Wanted(int id) => (deviceId is null || id == deviceId) && scope.Includes(id, devices);

        var events = log is "events" or "both"
            ? DeviceTimeline.ReadBackAsync(depth => client.Logs.ListEventLogAsync(deviceId, depth, cancellationToken), e => Sources.Utc(e.Timestamp, options), window, cancellationToken)
            : null;
        var alerts = log is "alerts" or "both"
            ? DeviceTimeline.ReadBackAsync(depth => client.Logs.ListAlertLogAsync(deviceId, depth, cancellationToken), a => Sources.Utc(a.TimeLogged, options), window, cancellationToken)
            : null;
        var rules = alerts is null ? null : Sources.TryAsync(() => fleet.GetRulesAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(new Task?[] { events, alerts, rules }.Where(t => t is not null)!).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var read in new[] { events?.Result.Error, alerts?.Result.Error })
        {
            if (read is not null)
            {
                throw new ToolException(read);
            }
        }

        var ruleNames = rules?.Result.Value?.ToDictionary(r => r.Id, r => r.Name) ?? [];
        var rows = new List<NetworkEvent>();

        foreach (var e in events?.Result.Value?.Items ?? [])
        {
            if (Sources.Utc(e.Timestamp, options) is { } at && window.Contains(at) && Wanted(e.DeviceId)
                && (type is null || e.Type?.Contains(type.Trim(), StringComparison.OrdinalIgnoreCase) == true)
                && (minimum is null || (e.Severity ?? 0) >= minimum))
            {
                rows.Add(new NetworkEvent(at, "eventlog", e.DeviceId, devices.GetValueOrDefault(e.DeviceId)?.Hostname ?? e.Hostname, DeviceSummary.DisplayOf(devices.GetValueOrDefault(e.DeviceId)), e.Type, EventSeverityName(e.Severity), null, e.Message,
                    string.IsNullOrWhiteSpace(e.Username) ? null : e.Username));
            }
        }

        // What each row means for its alert (#77): one fault of several clearing isn't the alert recovering.
        var alertKinds = AlertLogKinds.Describe(alerts?.Result.Value?.Items ?? []);
        foreach (var a in alerts?.Result.Value?.Items ?? [])
        {
            if (Sources.Utc(a.TimeLogged, options) is { } at && window.Contains(at) && Wanted(a.DeviceId) && type is null && minimum is null)
            {
                var rule = ruleNames.TryGetValue(a.RuleId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"Rule {a.RuleId}";
                rows.Add(new NetworkEvent(at, "alertlog", a.DeviceId, devices.GetValueOrDefault(a.DeviceId)?.Hostname ?? a.Hostname, DeviceSummary.DisplayOf(devices.GetValueOrDefault(a.DeviceId)), alertKinds.GetValueOrDefault(a, a.State.ToString().ToLowerInvariant()), null, rule, null, null));
            }
        }

        var ordered = rows.OrderByDescending(r => r.Time).ToList();
        var page = Paging.Apply(ordered, offset, limit, out var info);

        // The window is only as complete as the shortest log read.
        var covered = new[] { events?.Result.Value?.CoveredSince, alerts?.Result.Value?.CoveredSince }
            .Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(window.Since).Max();
        var complete = covered <= window.Since;

        var lead = EventWindowText.Describe(window, time.GetUtcNow().UtcDateTime);
        var counts = log switch
        {
            "alerts" => DeviceHealth.Count(ordered.Count, "alert change"),
            "both" => $"{DeviceHealth.Count(ordered.Count(r => r.Source == "eventlog"), "event")} and {DeviceHealth.Count(ordered.Count(r => r.Source == "alertlog"), "alert change")}",
            _ => DeviceHealth.Count(ordered.Count, "event"),
        };
        var summary = $"{lead}: {counts} on {DeviceHealth.Count(ordered.Select(r => r.DeviceId).Distinct().Count(), "device")}." +
            (complete ? string.Empty : $" LibreNMS's logs only reach back to {covered:yyyy-MM-dd HH:mm} UTC, so the start of the window isn't covered.") +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "entry", "entries") : string.Empty);

        return new(summary, new NetworkEventsData(new(window.Since, window.Until, complete, complete ? null : covered),
            page.Select(CleanEntry).ToList(), info), rules?.Result.Error is not null);
    }

    /// <param name="Kind">The event log's type, or what an alert log row means for its alert: active, acknowledged, worse, better, changed or recovered - or, for one fault of several on LibreNMS 26.10, fault_added or fault_cleared (#77).</param>
    /// <param name="Text">The event log's own message - device text, so it's data, not ours.</param>
    public sealed record NetworkEvent(DateTime Time, string Source, int DeviceId, string? Hostname, string? Display, string? Kind, string? Severity, string? Rule, string? Text, string? User);

    /// <summary>LibreNMS's event log severities, 1 (ok) to 5 (critical).</summary>
    private static int? ParseEventSeverity(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "any" or "all" => null,
        "ok" => 1,
        "info" => 2,
        "notice" => 3,
        "warning" => 4,
        "critical" => 5,
        _ => throw new ToolException("Severity must be ok, info, notice, warning or critical."),
    };

    private static string? EventSeverityName(int? severity) => severity switch
    {
        1 => "ok",
        2 => "info",
        3 => "notice",
        4 => "warning",
        5 => "critical",
        _ => null,
    };
    internal static TimeWindow Validate(NetworkEventsRequest request, TimeProvider time)
    {
        _ = request.Log?.Trim().ToLowerInvariant() switch
        {
            null or "" or "events" or "event" or "eventlog" => "events",
            "alerts" or "alert" or "alertlog" => "alerts",
            "both" or "all" => "both",
            _ => throw new ToolException("Log must be events, alerts or both."),
        };
        var window = TimeWindow.Resolve(request.Since, request.Until, request.Hours, DeviceEventHistory.DefaultHours, time);
        Paging.Check(request.Offset, request.Limit, MaxEvents);
        _ = ParseEventSeverity(request.Severity);
        return window;
    }
    private NetworkEvent CleanEntry(NetworkEvent entry)
    {
        string? Clean(string? value) => value is null ? null : redactor.Redact(value);
        return entry with { Hostname = Clean(entry.Hostname), Display = Clean(entry.Display), Kind = Clean(entry.Kind),
            Rule = Clean(entry.Rule), Text = Clean(entry.Text), User = Clean(entry.User) };
    }
}
