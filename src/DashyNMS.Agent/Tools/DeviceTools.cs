using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>Finding a device, and how it is doing right now.</summary>
[McpServerToolType]
public sealed partial class DeviceTools(
    ILibreNmsClient client,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    ToolRunner runner,
    AgentOptions options)
{
    public const int DefaultMatches = 10;
    public const int MaxMatches = 50;
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
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var wanted = ParseStatus(status);
        if (text is null && wanted is null && string.IsNullOrWhiteSpace(group) && string.IsNullOrWhiteSpace(location))
        {
            throw new ToolException("Give part of a name, an IP address or a MAC address to search for, or a status, group or location to list.");
        }

        Paging.Check(offset, limit, MaxMatches);
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        bool Wanted(Device d) => scope.Includes(d) && (wanted is null || DeviceSummary.StatusOf(d) == wanted);

        if (text is not null && MacAddress().IsMatch(text))
        {
            return await FindByMacAsync(text, Wanted, offset, limit, verbose, cancellationToken).ConfigureAwait(false);
        }

        var devices = await directory.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var ranked = devices
            .Where(Wanted)
            .Select(d => (Device: d, Rank: text is null ? 0 : Rank(d, text)))
            .Where(r => r.Rank >= 0)
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Device.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(r => DeviceSummary.Shape(r.Device, options, verbose))
            .ToList();

        var matches = Paging.Apply(ranked, offset, limit, out var page);
        var summary = page.Total == 0 ? "No devices match." : Paging.Describe(page, "matching device", "matching devices");
        return ToolResult.Ok(summary, new { devices = matches, page });
    }

    private async Task<JsonObject> FindByMacAsync(string mac, Func<Device, bool> wanted, int offset, int limit, bool verbose, CancellationToken cancellationToken)
    {
        var entries = await client.Arp.FindByMacAsync(mac, cancellationToken).ConfigureAwait(false);
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);

        var seen = entries
            .GroupBy(e => e.DeviceId)
            .Where(g => devices.TryGetValue(g.Key, out var d) && wanted(d))
            .Select(g => new
            {
                device = DeviceSummary.Shape(devices[g.Key], options, verbose),
                seen_as = g.Select(e => new { ip = e.Ipv4Address, mac = e.MacAddress, port_id = e.PortId }).Distinct().ToList(),
            })
            .ToList();

        var matches = Paging.Apply(seen, offset, limit, out var page);
        var summary = page.Total == 0
            ? "No device in LibreNMS has this MAC address in its ARP table."
            : $"{page.Total} device{(page.Total == 1 ? " has" : "s have")} this MAC address in its ARP table." +
              (page.NextOffset is not null || page.Offset > 0 ? " " + Paging.Describe(page, "device", "devices") : string.Empty);

        return ToolResult.Ok(summary, new { arp_matches = matches, page });
    }

    private static string? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "any" or "all" => null,
        "up" => "up",
        "down" => "down",
        "disabled" => "disabled",
        _ => throw new ToolException("Status must be up, down or disabled."),
    };

    /// <summary>0 exact, 1 starts with, 2 contains, -1 no match - over the fields people call a device by.</summary>
    private static int Rank(Device device, string text)
    {
        string?[] fields = [device.Hostname, device.SysName, device.Display, device.Ip];
        if (fields.Any(f => string.Equals(f, text, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (fields.Any(f => f?.StartsWith(text, StringComparison.OrdinalIgnoreCase) == true))
        {
            return 1;
        }

        return fields.Any(f => f?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) ? 2 : -1;
    }

    private async Task<JsonObject> HealthAsync(string query, CancellationToken cancellationToken)
    {
        var listed = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var id = listed.DeviceId;

        var fresh = Sources.TryAsync(() => client.Devices.GetAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken), cancellationToken);
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

        var device = Use(fresh.Result, "device") ?? listed;
        var openAlerts = (Use(alerts.Result, "alerts") ?? [])
            .Where(a => a.DeviceId == id)
            .OrderByDescending(a => a.Timestamp)
            .Select(a => AlertSummary.From(a, options))
            .ToList();

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
            device = DeviceSummary.From(device, options),
            uptime_seconds = device.Uptime > 0 ? device.Uptime : (long?)null,
            under_maintenance = Use(maintenance.Result, "maintenance"),
            open_alerts = openAlerts,
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

        var summary = $"Device is {status}, {Count(openAlerts.Count, "open alert")}, {Count(downPorts.Count, "port")} down while enabled" +
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

    [GeneratedRegex(@"^(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$|^(?:[0-9A-Fa-f]{4}\.){2}[0-9A-Fa-f]{4}$|^[0-9A-Fa-f]{12}$")]
    private static partial Regex MacAddress();
}
