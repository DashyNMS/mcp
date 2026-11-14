using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
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

    [McpServerTool(Name = ToolText.FindDevice.Name, Title = ToolText.FindDevice.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.FindDevice.Description)]
    public Task<string> FindDevice(
        [Description(ToolText.FindDevice.Args.Query)] string? query = null,
        [Description(ToolText.FindDevice.Args.Status)] string? status = null,
        [Description(ToolText.FindDevice.Args.Group)] string? group = null,
        [Description(ToolText.FindDevice.Args.Location)] string? location = null,
        [Description(ToolText.FindDevice.Args.Offset)] int offset = 0,
        [Description(ToolText.FindDevice.Args.Limit)] int limit = DefaultMatches,
        [Description(ToolText.FindDevice.Args.Verbose)] bool verbose = false,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.FindDevice.Name, ct => FindAsync(query, status, group, location, offset, limit, verbose, ct), cancellationToken);

    [McpServerTool(Name = ToolText.DeviceHealth.Name, Title = ToolText.DeviceHealth.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.DeviceHealth.Description)]
    public Task<string> DeviceHealth(
        [Description(ToolText.DeviceHealth.Args.Device)] string device,
        CancellationToken cancellationToken)
        => runner.RunAsync(ToolText.DeviceHealth.Name, ct => HealthAsync(device, ct), cancellationToken);

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
        var shaped = summaries.Shape(device);
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
            device = shaped.Device,
            uptime_seconds = shaped.UptimeSeconds,
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
