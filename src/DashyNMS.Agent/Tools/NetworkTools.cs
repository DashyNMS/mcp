using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// The whole network at once: the dashboard's counts (#25), its Top widgets
/// (#26) and the fleet-wide logs (#29).
/// </summary>
[McpServerToolType]
public sealed class NetworkTools(
    ILibreNmsClient client,
    FleetCache fleet,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int DefaultTop = 10;
    public const int MaxTop = 50;
    public const int DefaultEvents = 50;
    public const int MaxEvents = 100;
    public const int MaxDownLocations = 10;

    [McpServerTool(Name = ToolText.NetworkOverview.Name, Title = ToolText.NetworkOverview.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.NetworkOverview.Description)]
    public Task<string> NetworkOverview(CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.NetworkOverview.Name, OverviewAsync, cancellationToken);

    [McpServerTool(Name = ToolText.TopInterfaces.Name, Title = ToolText.TopInterfaces.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.TopInterfaces.Description)]
    public Task<string> TopInterfaces(
        [Description(ToolText.TopInterfaces.Args.By)] string? by = null,
        [Description(ToolText.TopInterfaces.Args.Limit)] int limit = DefaultTop,
        [Description(ToolText.TopInterfaces.Args.Group)] string? group = null,
        [Description(ToolText.TopInterfaces.Args.Location)] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.TopInterfaces.Name, ct => TopPortsAsync(errors: false, by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = ToolText.TopErrors.Name, Title = ToolText.TopErrors.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.TopErrors.Description)]
    public Task<string> TopErrors(
        [Description(ToolText.TopErrors.Args.By)] string? by = null,
        [Description(ToolText.TopErrors.Args.Limit)] int limit = DefaultTop,
        [Description(ToolText.TopErrors.Args.Group)] string? group = null,
        [Description(ToolText.TopErrors.Args.Location)] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.TopErrors.Name, ct => TopPortsAsync(errors: true, by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = ToolText.TopDevices.Name, Title = ToolText.TopDevices.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.TopDevices.Description)]
    public Task<string> TopDevices(
        [Description(ToolText.TopDevices.Args.By)] string? by = null,
        [Description(ToolText.TopDevices.Args.Limit)] int limit = DefaultTop,
        [Description(ToolText.TopDevices.Args.Group)] string? group = null,
        [Description(ToolText.TopDevices.Args.Location)] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.TopDevices.Name, ct => TopDevicesAsync(by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = ToolText.NetworkEvents.Name, Title = ToolText.NetworkEvents.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.NetworkEvents.Description)]
    public Task<string> NetworkEvents(
        [Description(ToolText.NetworkEvents.Args.Log)] string? log = null,
        [Description(ToolText.NetworkEvents.Args.Hours)] int? hours = null,
        [Description(ToolText.NetworkEvents.Args.Since)] string? since = null,
        [Description(ToolText.NetworkEvents.Args.Until)] string? until = null,
        [Description(ToolText.NetworkEvents.Args.Type)] string? type = null,
        [Description(ToolText.NetworkEvents.Args.Severity)] string? severity = null,
        [Description(ToolText.NetworkEvents.Args.Device)] string? device = null,
        [Description(ToolText.NetworkEvents.Args.Group)] string? group = null,
        [Description(ToolText.NetworkEvents.Args.Location)] string? location = null,
        [Description(ToolText.NetworkEvents.Args.Offset)] int offset = 0,
        [Description(ToolText.NetworkEvents.Args.Limit)] int limit = DefaultEvents,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.NetworkEvents.Name, ct => EventsAsync(log, hours, since, until, type, severity, device, group, location, offset, limit, ct), cancellationToken);

    private async Task<JsonObject> OverviewAsync(CancellationToken cancellationToken)
    {
        var result = await new DashyNMS.Agent.Overview.NetworkOverview(client, directory, fleet.GetPortsAsync,
            options, new DashyNMS.Agent.Redaction.Redactor(), time).ReadAsync(cancellationToken).ConfigureAwait(false);
        var d = result.Devices;
        var a = result.Alerts;
        var p = result.Ports;
        var summary = $"{d.Total} devices visible to this account: {d.Up} up, {d.Down} down" +
            (d.Disabled > 0 ? $", {d.Disabled} disabled" : string.Empty) +
            (a is null ? "; open alerts couldn't be read." : $"; {a.Describe()} ({a.Critical} critical, {a.Warning} warning).");
        return ToolResult.Ok(summary, new
        {
            devices = new { total = d.Total, up = d.Up, down = d.Down, disabled = d.Disabled, ignored = d.Ignored },
            most_down_by_location = result.DownLocations.Select(l => new { location = l.Location, down = l.Down }),
            alerts = a is null ? null : new { open = a.AlertGroups, faults = a.Faults, critical = a.Critical, warning = a.Warning,
                acknowledged = a.Acknowledged, partly_acknowledged = a.PartlyAcknowledged, devices_alerting = a.Devices },
            ports = p is null ? null : new { total = p.Total, down_while_enabled = p.DownWhileEnabled, counting_errors = p.CountingErrors },
            librenms_version = result.LibreNmsVersion,
            unavailable = result.Unavailable.Count == 0 ? null : result.Unavailable.Select(u => new { source = u.Source, error = "This source is unavailable." }),
            down_devices = result.DownDevices,
            alert_groups = result.AlertGroups,
            down_devices_omitted = result.DownDevicesOmitted,
            alert_groups_omitted = result.AlertGroupsOmitted,
            down_locations_omitted = result.DownLocationsOmitted,
        });
    }

    private async Task<JsonObject> TopPortsAsync(bool errors, string? byText, int limit, string? group, string? location, CancellationToken cancellationToken)
    {
        var result = await new DashyNMS.Agent.Rankings.FleetRankings(fleet, directory, scopes, runner.Redactor)
            .ReadPortsAsync(new(errors, byText, limit, group, location), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private async Task<JsonObject> TopDevicesAsync(string? byText, int limit, string? group, string? location, CancellationToken cancellationToken)
    {
        var result = await new DashyNMS.Agent.Rankings.FleetRankings(fleet, directory, scopes, runner.Redactor)
            .ReadDevicesAsync(new(byText, limit, group, location), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private async Task<JsonObject> EventsAsync(
        string? logText, int? hours, string? since, string? until, string? type, string? severityText, string? deviceQuery,
        string? group, string? location, int offset, int limit, CancellationToken cancellationToken)
    {
        var log = logText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "events" or "event" or "eventlog" => "events",
            "alerts" or "alert" or "alertlog" => "alerts",
            "both" or "all" => "both",
            _ => throw new ToolException("Log must be events, alerts or both."),
        };
        var window = TimeWindow.Resolve(since, until, hours, EventTools.DefaultHours, time);
        Paging.Check(offset, limit, MaxEvents);
        var minimum = ParseEventSeverity(severityText);
        int? deviceId = string.IsNullOrWhiteSpace(deviceQuery) ? null : (await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false)).DeviceId;
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

        var lead = EventTools.Describe(window, time.GetUtcNow().UtcDateTime);
        var counts = log switch
        {
            "alerts" => DeviceTools.Count(ordered.Count, "alert change"),
            "both" => $"{DeviceTools.Count(ordered.Count(r => r.Source == "eventlog"), "event")} and {DeviceTools.Count(ordered.Count(r => r.Source == "alertlog"), "alert change")}",
            _ => DeviceTools.Count(ordered.Count, "event"),
        };
        var summary = $"{lead}: {counts} on {DeviceTools.Count(ordered.Select(r => r.DeviceId).Distinct().Count(), "device")}." +
            (complete ? string.Empty : $" LibreNMS's logs only reach back to {covered:yyyy-MM-dd HH:mm} UTC, so the start of the window isn't covered.") +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "entry", "entries") : string.Empty);

        return ToolResult.Ok(summary, new
        {
            window = new { since = window.Since, until = window.Until, window_complete = complete, covered_since = complete ? (DateTime?)null : covered },
            entries = page,
            page = info,
        });
    }

    /// <param name="Kind">The event log's type, or what an alert log row means for its alert: active, acknowledged, worse, better, changed or recovered - or, for one fault of several on LibreNMS 26.10, fault_added or fault_cleared (#77).</param>
    /// <param name="Text">The event log's own message - device text, so it's data, not ours.</param>
    private sealed record NetworkEvent(DateTime Time, string Source, int DeviceId, string? Hostname, string? Display, string? Kind, string? Severity, string? Rule, string? Text, string? User);

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
}
