using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>Finding a device, and how it is doing right now.</summary>
[McpServerToolType]
public sealed class DeviceTools(
    ILibreNmsClient client,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    ToolRunner runner,
    AgentOptions options)
{
    public const int DefaultMatches = 10;
    public const int MaxMatches = DeviceSearch.MaxMatches;
    public const int MaxProblemPorts = 5;
    public const int MaxStorage = 3;

    [McpServerTool(Name = "find_device", Title = "Find a device", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Search LibreNMS for devices by part of a hostname, display name or sysName, by IP address, or by MAC address, " +
        "and/or list devices by status, device group or location. Use it when the exact device isn't known yet, to get device IDs " +
        "for the other tools, or for \"which devices are down at site X?\". A MAC address is looked up in LibreNMS's ARP tables, so it finds the " +
        "routers or layer-3 switches that have seen it (with the IP it had), not the endpoint itself. Best matches first. " +
        "Paged: 10 by default, up to 50; when page.next_offset is set, call again with that offset to get every match rather than " +
        "making narrower searches. Records are short unless verbose is true. It doesn't show health or alerts.")]
    public Task<string> FindDevice(
        [Description("Part of a hostname, display name or sysName, an IP address, or a MAC address (any common format). Can be left out when status, group or location is given.")] string? query = null,
        [Description("Only devices with this status: up, down or disabled.")] string? status = null,
        [Description("Only devices in this LibreNMS device group (by name; list_groups_and_locations shows them).")] string? group = null,
        [Description("Only devices at this location (by name; list_groups_and_locations shows them).")] string? location = null,
        [Description("How many matches to skip, for paging. Start at 0, then use page.next_offset.")] int offset = 0,
        [Description("How many matches to return, 1 to 50. Defaults to 10.")] int limit = DefaultMatches,
        [Description("Return every field (sysName, OS, hardware, last polled) instead of the short record.")] bool verbose = false,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("find_device", ct => FindAsync(query, status, group, location, offset, limit, verbose, ct), cancellationToken);

    [McpServerTool(Name = "device_health", Title = "Device health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "How one device is doing now: up or down, uptime, whether it's in a maintenance window, its open alerts, " +
        "peak CPU, memory and fullest storage, and the ports that are down while enabled or are counting errors (top 5). " +
        "Use it for \"is X OK?\" questions. It doesn't cover history; use device_events or what_changed for that, and port_status for one port in detail.")]
    public Task<string> DeviceHealth(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        CancellationToken cancellationToken)
        => runner.RunAsync("device_health", ct => HealthAsync(device, ct), cancellationToken);

    private async Task<JsonObject> FindAsync(string? query, string? status, string? group, string? location, int offset, int limit, bool verbose, CancellationToken cancellationToken)
    {
        var result = await new DeviceSearch(client, directory, scopes, options, runner.Redactor)
            .FindDevicesAsync(new(query, status, group, location, offset, limit), cancellationToken).ConfigureAwait(false);
        object Shape(DeviceSummary d) => verbose ? d : DeviceBrief.FromSummary(d);
        return result.IsMacSearch
            ? ToolResult.Ok(result.Summary, new { arp_matches = result.Matches.Select(m => new { device = Shape(m.Device), seen_as = m.SeenAs }), page = result.Page })
            : ToolResult.Ok(result.Summary, new { devices = result.Matches.Select(m => Shape(m.Device)), page = result.Page });
    }

    private async Task<JsonObject> HealthAsync(string query, CancellationToken cancellationToken)
    {
        var listed = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var id = listed.DeviceId;
        // The device part is the shared summary, so MCP and in-process hosts read, redact and
        // shape a device the same way. It holds no cache, so one per call costs nothing.
        var summaries = DeviceSummaries.Create(client, options, runner.Redactor);

        var fresh = Sources.TryAsync(() => summaries.FetchAsync(id, cancellationToken), cancellationToken);
        var maintenance = Sources.TryAsync(() => client.Devices.IsUnderMaintenanceAsync(id, cancellationToken), cancellationToken);
        var alerts = Sources.TryAsync(() => client.Alerts.ListAsync(AlertQuery.Open, cancellationToken), cancellationToken);
        var processors = Sources.TryAsync(() => client.Health.ListProcessorsAsync(id, cancellationToken), cancellationToken);
        var memory = Sources.TryAsync(() => client.Health.ListMempoolsAsync(id, cancellationToken), cancellationToken);
        var storage = Sources.TryAsync(() => client.Health.ListStorageAsync(id, cancellationToken), cancellationToken);
        var ports = Sources.TryAsync(() => client.Ports.ListForDeviceAsync(id, cancellationToken), cancellationToken);
        await Task.WhenAll(fresh, maintenance, alerts, processors, memory, storage, ports).ConfigureAwait(false);

        var unavailable = new List<Unavailable>();
        T? Use<T>(Fetched<T> fetched, string source)
        {
            if (fetched.Error is { } error)
            {
                unavailable.Add(new Unavailable(source, error));
            }

            return fetched.Value;
        }

        // A failed read still answers from the device list the device was resolved from.
        var device = Use(fresh.Result, "device") ?? listed;
        var summary = summaries.Shape(device);
        // One per rule firing on the device, as active_alerts reports them (#77).
        var deviceGroups = AlertGrouping.ByDeviceAndRule((Use(alerts.Result, "alerts") ?? []).Where(a => a.DeviceId == id));
        var openAlerts = deviceGroups.Select(g => AlertGroupSummary.From(g, options)).ToList();
        var alertTotals = OpenAlertTotals.From(deviceGroups);

        var portList = (Use(ports.Result, "ports") ?? []).Where(p => !p.Deleted && !p.Disabled && !p.Ignore).ToList();
        var downPorts = portList.Where(IsDownWhileEnabled).ToList();
        var problemPorts = portList
            .Where(p => IsDownWhileEnabled(p) || ErrorRate(p) > 0)
            .OrderByDescending(ErrorRate)
            .ThenBy(p => p.IfName, StringComparer.OrdinalIgnoreCase)
            .Select(PortProblem.From);
        var topProblems = ToolResult.Take(problemPorts, MaxProblemPorts, out var otherProblems);

        var status = DeviceSummary.StatusOf(device);
        var data = new
        {
            device = summary.Device,
            uptime_seconds = summary.UptimeSeconds,
            under_maintenance = Use(maintenance.Result, "maintenance"),
            open_alerts = openAlerts,
            open_faults = alertTotals.Faults > alertTotals.AlertGroups ? alertTotals.Faults : (int?)null,
            cpu_max_percent = Max(Use(processors.Result, "processors")?.Select(p => p.UsagePercent)),
            memory_max_percent = Max(Use(memory.Result, "memory")?.Select(m => m.UsagePercent)),
            fullest_storage = Use(storage.Result, "storage")?
                .Where(s => s.UsagePercent is not null)
                .OrderByDescending(s => s.UsagePercent)
                .Take(MaxStorage)
                .Select(s => new { description = s.Description, usage_percent = Math.Round(s.UsagePercent!.Value, 1, MidpointRounding.AwayFromZero) })
                .ToList(),
            ports_total = portList.Count,
            ports_down_while_enabled = downPorts.Count,
            problem_ports = topProblems,
            problem_ports_omitted = otherProblems > 0 ? otherProblems : (int?)null,
            unavailable = unavailable.Count > 0 ? unavailable : null,
        };

        // An alert list that couldn't be read is unknown, never "0 open alerts" (#79).
        var alertsText = alerts.Result.Error is null ? Count(openAlerts.Count, "open alert") : "open alerts unknown";
        var summary = $"Device is {status}, {alertsText}, {Count(downPorts.Count, "port")} down while enabled" +
            (unavailable.Count > 0 ? $"; {Count(unavailable.Count, "part")} couldn't be fetched." : ".");

        return ToolResult.Ok(summary, data);
    }

    internal static bool IsDownWhileEnabled(Port port)
        => string.Equals(port.IfAdminStatus, "up", StringComparison.OrdinalIgnoreCase) && !port.IsUp;

    internal static double ErrorRate(Port port) => (port.IfInErrorsRate ?? 0) + (port.IfOutErrorsRate ?? 0);

    private static double? Max(IEnumerable<double?>? values)
        => values?.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max() is { } max && !double.IsNaN(max)
            ? Math.Round(max, 1, MidpointRounding.AwayFromZero)
            : null;

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

}
