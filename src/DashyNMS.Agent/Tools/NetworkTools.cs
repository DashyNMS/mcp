using System.ComponentModel;
using System.Text.Json.Nodes;
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

    [McpServerTool(Name = "network_overview", Title = "Network overview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The whole network in one call, as the dashboard's counts: how many devices are up, down, disabled or ignored, which locations have the most " +
        "devices down, open alerts by severity and how many are acknowledged, how many ports are down while enabled or counting errors, and the LibreNMS " +
        "version. A cheap first call for \"how's the network?\" before drilling in with find_device, active_alerts or the top_ tools.")]
    public Task<string> NetworkOverview(CancellationToken cancellationToken = default)
        => runner.RunAsync("network_overview", OverviewAsync, cancellationToken);

    [McpServerTool(Name = "top_interfaces", Title = "Top interfaces", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The busiest ports across the network right now, by LibreNMS's last polling interval: traffic in and out in Mbps and as a share of the port's speed. " +
        "Rank by total, in or out. Filter by device group or location. As desktop's Top interfaces widget. For traffic history, use the device's graphs.")]
    public Task<string> TopInterfaces(
        [Description("Rank by total (the default), in or out.")] string? by = null,
        [Description("How many ports, 1 to 50. Defaults to 10.")] int limit = DefaultTop,
        [Description("Only devices in this LibreNMS device group (by name).")] string? group = null,
        [Description("Only devices at this location (by name).")] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("top_interfaces", ct => TopPortsAsync(errors: false, by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = "top_errors", Title = "Top errors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The ports counting the most errors across the network right now, in errors per second in and out, by LibreNMS's last polling interval. " +
        "An empty list means no port is counting errors. Rank by total, in or out; filter by device group or location. As desktop's Top errors widget.")]
    public Task<string> TopErrors(
        [Description("Rank by total (the default), in or out.")] string? by = null,
        [Description("How many ports, 1 to 50. Defaults to 10.")] int limit = DefaultTop,
        [Description("Only devices in this LibreNMS device group (by name).")] string? group = null,
        [Description("Only devices at this location (by name).")] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("top_errors", ct => TopPortsAsync(errors: true, by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = "top_devices", Title = "Top devices", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The devices that stand out across the network: by traffic (the default, all their ports' traffic summed, as desktop's Top devices widget) or by " +
        "problems (most ports down while enabled or counting errors). Filter by device group or location.")]
    public Task<string> TopDevices(
        [Description("Rank by traffic (the default), in, out, or problems.")] string? by = null,
        [Description("How many devices, 1 to 50. Defaults to 10.")] int limit = DefaultTop,
        [Description("Only devices in this LibreNMS device group (by name).")] string? group = null,
        [Description("Only devices at this location (by name).")] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("top_devices", ct => TopDevicesAsync(by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = "network_events", Title = "Network events", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The event log and/or alert log for the whole network over a window of time, newest first, as desktop's Logs tab - for \"what happened overnight?\" " +
        "or \"anything rebooted today?\". The event log has interface changes, reboots, discovery and user actions; the alert log has every alert state change. " +
        EventTools.WindowHelp + " Filter by event type, severity, device, device group or location. Paged: 50 by default, up to 100. " +
        "If LibreNMS's logs don't reach back to the window's start, window_complete is false. For one device, device_events also adds its outages.")]
    public Task<string> NetworkEvents(
        [Description("Which log: events (the default), alerts, or both.")] string? log = null,
        [Description("How many hours to look back from until, 1 to 168. Leave out when using since.")] int? hours = null,
        [Description("Start of the window: a duration before now (90m, 24h, 7d) or an ISO 8601 time.")] string? since = null,
        [Description("End of the window: a duration before now or an ISO 8601 time. Defaults to now.")] string? until = null,
        [Description("Only event-log entries of this type, e.g. interface, reboot, discovery, system. Part of the type is enough.")] string? type = null,
        [Description("Only event-log entries of this severity or worse: ok, info, notice, warning or critical.")] string? severity = null,
        [Description("Only this device: hostname, display name, IP address or LibreNMS device ID.")] string? device = null,
        [Description("Only devices in this LibreNMS device group (by name).")] string? group = null,
        [Description("Only devices at this location (by name).")] string? location = null,
        [Description("How many entries to skip, for paging. Start at 0, then use page.next_offset.")] int offset = 0,
        [Description("How many entries to return, 1 to 100. Defaults to 50.")] int limit = DefaultEvents,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("network_events", ct => EventsAsync(log, hours, since, until, type, severity, device, group, location, offset, limit, ct), cancellationToken);

    private async Task<JsonObject> OverviewAsync(CancellationToken cancellationToken)
    {
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var alerts = Sources.TryAsync(() => client.Alerts.ListAsync(AlertQuery.Open, cancellationToken), cancellationToken);
        var ports = Sources.TryAsync(() => fleet.GetPortsAsync(cancellationToken), cancellationToken);
        var system = Sources.TryAsync(() => client.System.GetAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(devicesTask, alerts, ports, system).ConfigureAwait(false);

        var devices = devicesTask.Result;
        var unavailable = new List<Unavailable>();
        foreach (var (source, error) in new[] { ("alerts", alerts.Result.Error), ("ports", ports.Result.Error), ("system", system.Result.Error) })
        {
            if (error is not null)
            {
                unavailable.Add(new Unavailable(source, error));
            }
        }

        var status = devices.GroupBy(DeviceSummary.StatusOf).ToDictionary(g => g.Key, g => g.Count());
        var down = devices.Where(d => DeviceSummary.StatusOf(d) == "down").ToList();
        var openAlerts = alerts.Result.Value;
        var portList = ports.Result.Value?.Where(p => !p.Deleted && !p.Disabled && !p.Ignore).ToList();

        var data = new
        {
            devices = new
            {
                total = devices.Count,
                up = status.GetValueOrDefault("up"),
                down = status.GetValueOrDefault("down"),
                disabled = status.GetValueOrDefault("disabled"),
                ignored = devices.Count(d => d.Ignore),
            },
            most_down_by_location = down
                .GroupBy(d => string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new { location = g.Key, down = g.Count() })
                .OrderByDescending(g => g.down)
                .Take(MaxDownLocations)
                .ToList(),
            alerts = openAlerts is null ? null : new
            {
                open = openAlerts.Count,
                critical = openAlerts.Count(a => a.Severity == AlertSeverity.Critical),
                warning = openAlerts.Count(a => a.Severity == AlertSeverity.Warning),
                acknowledged = openAlerts.Count(a => a.IsAcknowledged),
                devices_alerting = openAlerts.Select(a => a.DeviceId).Distinct().Count(),
            },
            ports = portList is null ? null : new
            {
                total = portList.Count,
                down_while_enabled = portList.Count(DeviceTools.IsDownWhileEnabled),
                counting_errors = portList.Count(p => DeviceTools.ErrorRate(p) > 0),
            },
            librenms_version = system.Result.Value?.LocalVersion,
            unavailable = unavailable.Count > 0 ? unavailable : null,
        };

        var summary = $"{devices.Count} devices: {status.GetValueOrDefault("up")} up, {status.GetValueOrDefault("down")} down" +
            (status.GetValueOrDefault("disabled") > 0 ? $", {status["disabled"]} disabled" : string.Empty) +
            (openAlerts is null ? "." : $"; {DeviceTools.Count(openAlerts.Count, "open alert")} ({data.alerts!.critical} critical, {data.alerts.warning} warning).");

        return ToolResult.Ok(summary, data);
    }

    private async Task<JsonObject> TopPortsAsync(bool errors, string? byText, int limit, string? group, string? location, CancellationToken cancellationToken)
    {
        var by = ParseRank(byText);
        Paging.Check(0, limit, MaxTop);
        var (devices, ports) = await ScopedPortsAsync(group, location, cancellationToken).ConfigureAwait(false);

        var ranked = errors ? PortRankings.TopErrors(ports, by, limit) : PortRankings.TopTraffic(ports, by, limit);
        var rows = ranked.Select(r =>
        {
            var speedMbps = r.Port.IfSpeed is > 0 ? r.Port.IfSpeed.Value / 1_000_000d : (double?)null;
            return new
            {
                device = devices.TryGetValue(r.Port.DeviceId, out var d) ? DeviceBrief.From(d) : null,
                port_id = r.Port.PortId,
                port = r.Port.IfName ?? r.Port.IfDescr,
                alias = string.IsNullOrWhiteSpace(r.Port.IfAlias) || r.Port.IfAlias == r.Port.IfName ? null : r.Port.IfAlias,
                in_mbps = errors ? (double?)null : Math.Round(r.In / 1_000_000, 2),
                out_mbps = errors ? (double?)null : Math.Round(r.Out / 1_000_000, 2),
                in_errors_per_sec = errors ? Math.Round(r.In, 2) : (double?)null,
                out_errors_per_sec = errors ? Math.Round(r.Out, 2) : (double?)null,
                speed_mbps = speedMbps,
                busiest_direction_percent = !errors && speedMbps is > 0 ? Math.Round(Math.Max(r.In, r.Out) / 1_000_000 / speedMbps.Value * 100, 1) : (double?)null,
            };
        }).ToList();

        var summary = rows.Count == 0
            ? errors ? "No port is counting errors." : "No port is carrying traffic."
            : $"The {DeviceTools.Count(rows.Count, "port")} {(errors ? "counting the most errors" : "carrying the most traffic")}, ranked by {by.ToString().ToLowerInvariant()}, out of {ports.Count} checked.";
        return ToolResult.Ok(summary, new { ports = rows });
    }

    private async Task<JsonObject> TopDevicesAsync(string? byText, int limit, string? group, string? location, CancellationToken cancellationToken)
    {
        var problems = string.Equals(byText?.Trim(), "problems", StringComparison.OrdinalIgnoreCase);
        var by = problems ? RankBy.Total : ParseRank(byText is null || string.Equals(byText.Trim(), "traffic", StringComparison.OrdinalIgnoreCase) ? null : byText);
        Paging.Check(0, limit, MaxTop);
        var (devices, ports) = await ScopedPortsAsync(group, location, cancellationToken).ConfigureAwait(false);

        if (problems)
        {
            var rows = ports
                .GroupBy(p => p.DeviceId)
                .Select(g => (DeviceId: g.Key, Down: g.Count(DeviceTools.IsDownWhileEnabled), Errors: g.Count(p => DeviceTools.ErrorRate(p) > 0)))
                .Where(x => x.Down + x.Errors > 0)
                .OrderByDescending(x => x.Down + x.Errors)
                .ThenByDescending(x => x.Down)
                .Take(limit)
                .Select(x => new
                {
                    device = devices.TryGetValue(x.DeviceId, out var d) ? DeviceBrief.From(d) : null,
                    ports_down_while_enabled = x.Down,
                    ports_counting_errors = x.Errors,
                })
                .ToList();

            return ToolResult.Ok(
                rows.Count == 0 ? "No device has ports down while enabled or counting errors." : $"The {DeviceTools.Count(rows.Count, "device")} with the most port problems.",
                new { devices = rows });
        }

        var ranked = PortRankings.TopDevices(ports, by, limit)
            .Select(r => new
            {
                device = devices.TryGetValue(r.DeviceId, out var d) ? DeviceBrief.From(d) : null,
                in_mbps = Math.Round(r.In / 1_000_000, 2),
                out_mbps = Math.Round(r.Out / 1_000_000, 2),
                active_ports = r.ActivePorts,
            })
            .ToList();

        return ToolResult.Ok(
            ranked.Count == 0 ? "No device is carrying traffic." : $"The {DeviceTools.Count(ranked.Count, "device")} carrying the most traffic, ranked by {by.ToString().ToLowerInvariant()}. Traffic is every port's summed, so traffic passing through counts twice.",
            new { devices = ranked });
    }

    /// <summary>The fleet port snapshot, live ports only, cut to the group or location asked for.</summary>
    private async Task<(IReadOnlyDictionary<int, Device> Devices, IReadOnlyList<Port> Ports)> ScopedPortsAsync(string? group, string? location, CancellationToken cancellationToken)
    {
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var portsTask = fleet.GetPortsAsync(cancellationToken);
        await Task.WhenAll(devicesTask, portsTask).ConfigureAwait(false);

        var devices = devicesTask.Result.ToDictionary(d => d.DeviceId);
        var ports = portsTask.Result
            .Where(p => !p.Deleted && !p.Disabled && !p.Ignore)
            .Where(p => devices.TryGetValue(p.DeviceId, out var d) && scope.Includes(d) && !d.Disabled)
            .ToList();
        return (devices, ports);
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
                rows.Add(new NetworkEvent(at, "eventlog", e.DeviceId, devices.GetValueOrDefault(e.DeviceId)?.Hostname ?? e.Hostname, e.Type, EventSeverityName(e.Severity), null, e.Message,
                    string.IsNullOrWhiteSpace(e.Username) ? null : e.Username));
            }
        }

        foreach (var a in alerts?.Result.Value?.Items ?? [])
        {
            if (Sources.Utc(a.TimeLogged, options) is { } at && window.Contains(at) && Wanted(a.DeviceId) && type is null && minimum is null)
            {
                var rule = ruleNames.TryGetValue(a.RuleId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"Rule {a.RuleId}";
                rows.Add(new NetworkEvent(at, "alertlog", a.DeviceId, devices.GetValueOrDefault(a.DeviceId)?.Hostname ?? a.Hostname, a.State.ToString().ToLowerInvariant(), null, rule, null, null));
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

    /// <param name="Kind">The event log's type, or the alert's new state.</param>
    /// <param name="Text">The event log's own message - device text, so it's data, not ours.</param>
    private sealed record NetworkEvent(DateTime Time, string Source, int DeviceId, string? Hostname, string? Kind, string? Severity, string? Rule, string? Text, string? User);

    private static RankBy ParseRank(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "total" or "both" => RankBy.Total,
        "in" or "rx" or "inbound" => RankBy.In,
        "out" or "tx" or "outbound" => RankBy.Out,
        _ => throw new ToolException("By must be total, in or out."),
    };

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
