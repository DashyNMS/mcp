using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// The rest of a device: inventory, VLANs, tables, PoE, wireless, routing,
/// availability, addresses (#23), and its neighbours (#32) - desktop's
/// Device Details sections, in two tools rather than one per section.
/// </summary>
[McpServerToolType]
public sealed class DetailTools(
    ILibreNmsClient client,
    FleetCache fleet,
    SensorSource sensorSource,
    DeviceDirectory directory,
    DeviceResolver resolver,
    ToolRunner runner)
{
    public const int DefaultRows = 50;
    public const int MaxRows = 200;

    public static readonly IReadOnlyList<string> AllSections = ["inventory", "vlans", "fdb", "arp", "poe", "wireless", "routing", "availability", "addresses"];

    /// <summary>The sections given when none are named: everything except the MAC and ARP tables, which can run to thousands of rows.</summary>
    public static readonly IReadOnlyList<string> DefaultSections = ["inventory", "vlans", "poe", "wireless", "routing", "availability", "addresses"];

    [McpServerTool(Name = "device_details", Title = "Device details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "One device's details beyond its health, in sections - ask only for the ones the question needs: " +
        "inventory (chassis, modules, transceivers, power supplies, fans with models and serials); vlans (each VLAN with its untagged and tagged ports); " +
        "fdb (MAC addresses learned per port - \"what's connected to port X?\"); arp (IP to MAC entries); poe (PoE power budget used and left); " +
        "wireless (access points, clients, channel and signal sensors); routing (BGP sessions, OSPF neighbours, VRFs); " +
        "availability (uptime percentage over the last day, week, month and year); addresses (the device's own IP addresses and which port each is on). " +
        "Without sections, everything except fdb and arp. Lists are paged together with offset and limit (50 rows by default, up to 200); " +
        "a section that couldn't be read is listed under unavailable. For health, use device_health; for sensors, device_sensors; for neighbours, device_neighbours.")]
    public Task<string> DeviceDetails(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("Which sections to return: any of inventory, vlans, fdb, arp, poe, wireless, routing, availability, addresses. Leave out for all but fdb and arp.")] string[]? sections = null,
        [Description("How many rows to skip in each list, for paging. Start at 0, then use the next_offset a section's page gives.")] int offset = 0,
        [Description("How many rows to return per list, 1 to 200. Defaults to 50.")] int limit = DefaultRows,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("device_details", ct => DetailsAsync(device, sections, offset, limit, ct), cancellationToken);

    [McpServerTool(Name = "device_neighbours", Title = "Device neighbours", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Every LLDP and CDP neighbour of one device: the local port, the device on the other end (with its LibreNMS ID when LibreNMS knows it), " +
        "its port, platform and protocol. Includes neighbours that report this device but that it doesn't report itself (seen_from: neighbour), " +
        "which often means a discovery problem on one side. Use it for \"what's connected to this switch?\" or topology questions. " +
        "For one port, port_status shows its neighbour too.")]
    public Task<string> DeviceNeighbours(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("device_neighbours", ct => NeighboursAsync(device, ct), cancellationToken);

    private async Task<JsonObject> DetailsAsync(string query, string[]? requested, int offset, int limit, CancellationToken cancellationToken)
    {
        Paging.Check(offset, limit, MaxRows);
        var sections = ParseSections(requested);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var id = device.DeviceId;

        bool Wants(string s) => sections.Contains(s);
        var needsPorts = Wants("vlans") || Wants("fdb") || Wants("arp") || Wants("addresses");

        var ports = needsPorts ? Sources.TryAsync(() => client.Ports.ListForDeviceAsync(id, cancellationToken), cancellationToken) : null;
        var vlans = Wants("vlans") || Wants("fdb") ? Sources.TryAsync(() => client.Vlans.ListAsync(cancellationToken), cancellationToken) : null;
        var inventory = Wants("inventory") ? Sources.TryAsync(() => client.Devices.GetInventoryAsync(id, cancellationToken), cancellationToken) : null;
        var fdb = Wants("fdb") ? Sources.TryAsync(() => client.Fdb.ListForDeviceAsync(id, cancellationToken), cancellationToken) : null;
        var arp = Wants("arp") ? Sources.TryAsync(() => client.Arp.ListForDeviceAsync(id, cancellationToken), cancellationToken) : null;
        var sensors = Wants("poe") ? Sources.TryAsync(() => sensorSource.GetForDeviceAsync(id, cancellationToken), cancellationToken) : null;
        var wireless = Wants("wireless") ? Sources.TryAsync(() => client.Devices.GetWirelessSensorsAsync(id, cancellationToken), cancellationToken) : null;
        var bgp = Wants("routing") ? Sources.TryAsync(() => client.Routing.ListBgpSessionsAsync(id, cancellationToken), cancellationToken) : null;
        var ospf = Wants("routing") ? Sources.TryAsync(() => client.Routing.ListOspfNeighboursAsync(id, cancellationToken), cancellationToken) : null;
        var ospf3 = Wants("routing") ? Sources.TryAsync(() => client.Routing.ListOspfv3NeighboursAsync(id, cancellationToken), cancellationToken) : null;
        var vrfs = Wants("routing") ? Sources.TryAsync(() => client.Routing.ListVrfsAsync(id, cancellationToken), cancellationToken) : null;
        var availability = Wants("availability") ? Sources.TryAsync(() => client.Devices.GetAvailabilityAsync(id, cancellationToken), cancellationToken) : null;
        var addresses = Wants("addresses") ? Sources.TryAsync(() => client.Ports.ListIpAddressesAsync(id, cancellationToken), cancellationToken) : null;

        await Task.WhenAll(new Task?[] { ports, vlans, inventory, fdb, arp, sensors, wireless, bgp, ospf, ospf3, vrfs, availability, addresses }.Where(t => t is not null)!).ConfigureAwait(false);

        var unavailable = new List<Unavailable>();
        T? Use<T>(Task<Fetched<T>>? task, string source)
        {
            if (task is null)
            {
                return default;
            }

            if (task.Result.Error is { } error)
            {
                unavailable.Add(new Unavailable(source, error));
            }

            return task.Result.Value;
        }

        var portList = Use(ports, "ports") ?? [];
        var portName = portList.ToDictionary(p => p.PortId, p => p.IfName ?? p.IfDescr);
        var vlanList = (Use(vlans, "vlans") ?? []).Where(v => v.DeviceId == id).ToList();
        var pages = new Dictionary<string, PageInfo>();

        IReadOnlyList<T> Paged<T>(string section, IReadOnlyList<T> rows)
        {
            var page = Paging.Apply(rows, offset, limit, out var info);
            if (info.NextOffset is not null || info.Offset > 0)
            {
                pages[section] = info;
            }

            return page;
        }

        var data = new JsonObject { ["device"] = System.Text.Json.JsonSerializer.SerializeToNode(DeviceBrief.From(device), ToolResult.Json) };
        void Put(string section, object? value) => data[section] = System.Text.Json.JsonSerializer.SerializeToNode(value, ToolResult.Json);

        if (Use(inventory, "inventory") is { } entries)
        {
            Put("inventory", Paged("inventory", entries
                .Where(e => !e.IsPort)
                .OrderBy(e => e.Index)
                .Select(e => new
                {
                    @class = e.Class,
                    name = e.Name,
                    description = e.Description == e.Name ? null : e.Description,
                    model = e.Model,
                    serial = e.Serial,
                    manufacturer = e.Manufacturer,
                    hardware_rev = e.HardwareRev,
                    firmware_rev = e.FirmwareRev,
                    software_rev = e.SoftwareRev,
                    replaceable = e.IsFieldReplaceable ? true : (bool?)null,
                })
                .ToList()));
        }

        if (Wants("vlans") && vlans is not null)
        {
            Put("vlans", Paged("vlans", vlanList
                .OrderBy(v => v.VlanNumber)
                .Select(v => new
                {
                    vlan = v.VlanNumber,
                    name = v.VlanName,
                    untagged_ports = portList.Where(p => p.Vlans.Any(m => m.Vlan == v.VlanNumber && m.Untagged) || (p.Vlans.Count == 0 && p.IfVlan == v.VlanNumber)).Select(p => portName[p.PortId]).ToList(),
                    tagged_ports = portList.Where(p => p.Vlans.Any(m => m.Vlan == v.VlanNumber && !m.Untagged)).Select(p => portName[p.PortId]).ToList(),
                })
                .ToList()));
        }

        if (Use(fdb, "fdb") is { } macs)
        {
            var vlanNumber = vlanList.ToDictionary(v => v.VlanId, v => v.VlanNumber);
            Put("fdb", Paged("fdb", macs
                .OrderBy(m => portName.GetValueOrDefault(m.PortId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.MacAddress, StringComparer.OrdinalIgnoreCase)
                .Select(m => new
                {
                    port = portName.GetValueOrDefault(m.PortId),
                    port_id = m.PortId,
                    mac = MacText(m.MacAddress),
                    vlan = m.VlanId is { } v && vlanNumber.TryGetValue(v, out var n) ? n : (int?)null,
                    updated_at = m.UpdatedAt,
                })
                .ToList()));
        }

        if (Use(arp, "arp") is { } arpEntries)
        {
            Put("arp", Paged("arp", arpEntries
                .OrderBy(a => a.Ipv4Address, StringComparer.Ordinal)
                .Select(a => new { ip = a.Ipv4Address, mac = MacText(a.MacAddress), port = portName.GetValueOrDefault(a.PortId) })
                .ToList()));
        }

        if (Use(sensors, "poe") is { } allSensors)
        {
            var poe = PoeBudget.FromSensors(allSensors);
            Put("poe", poe.HasAny
                ? new
                {
                    budgets = poe.Budgets.Select(b => new
                    {
                        label = b.Label,
                        used_watts = b.UsedWatts,
                        total_watts = b.TotalWatts,
                        remaining_watts = b.RemainingWatts,
                        used_percent = b.Percent is { } p ? Math.Round(p, 1) : (double?)null,
                    }).ToList(),
                    devices_connected = poe.DevicesConnected,
                }
                : new { none = "LibreNMS has no PoE budget sensors for this device." });
        }

        if (Use(wireless, "wireless") is { } radios)
        {
            Put("wireless", Paged("wireless", radios
                .Where(r => !r.Deleted)
                .OrderBy(r => r.SensorClass, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    type = r.SensorClass,
                    description = r.Description,
                    value = r.Current,
                    state = WirelessFleet.Evaluate(r).ToString().ToLowerInvariant() switch { "unknown" => null, var s => s },
                })
                .ToList()));
        }

        if (Wants("routing") && bgp is not null)
        {
            var sessions = Use(bgp, "bgp") ?? [];
            var neighbours = Use(ospf, "ospf") ?? [];
            var neighbours3 = Use(ospf3, "ospfv3") ?? [];
            Put("routing", new
            {
                bgp_sessions = sessions
                    .OrderBy(s => s.IsEstablished)
                    .ThenBy(s => s.PeerIdentifier, StringComparer.Ordinal)
                    .Select(s => new
                    {
                        peer = s.PeerAddressText,
                        remote_as = s.RemoteAs,
                        remote_as_name = s.AsText,
                        description = s.Description,
                        state = s.State,
                        admin_down = s.IsAdminDown ? true : (bool?)null,
                        established_seconds = s.IsEstablished ? s.EstablishedSeconds : (long?)null,
                        last_error = string.IsNullOrWhiteSpace(s.LastErrorText) ? null : s.LastErrorText,
                    })
                    .ToList(),
                ospf_neighbours = neighbours.Select(n => new { address = n.IpAddress, router_id = n.RouterId, state = n.State, port = n.PortId is { } p ? portName.GetValueOrDefault(p) : null })
                    .Concat(neighbours3.Select(n => new { address = n.Address, router_id = n.RouterId, state = n.State, port = n.PortId is { } p ? portName.GetValueOrDefault(p) : null }))
                    .ToList(),
                vrfs = (Use(vrfs, "vrfs") ?? []).Select(v => new { name = v.Name, route_distinguisher = v.RouteDistinguisher, description = v.Description }).ToList(),
            });
        }

        if (Use(availability, "availability") is { } windows)
        {
            Put("availability", windows
                .OrderBy(w => w.DurationSeconds)
                .Select(w => new { over = Period(w.DurationSeconds), percent = Math.Round(w.Percent, 3, MidpointRounding.AwayFromZero) })
                .ToList());
        }

        if (Use(addresses, "addresses") is { } ips)
        {
            Put("addresses", Paged("addresses", ips
                .Select(a => new
                {
                    address = a.IsIpv6 ? $"{a.Ipv6Compressed ?? a.Ipv6Address}/{a.Ipv6PrefixLength}" : $"{a.Ipv4Address}/{a.Ipv4PrefixLength}",
                    family = a.IsIpv6 ? "ipv6" : "ipv4",
                    port = portName.GetValueOrDefault(a.PortId),
                })
                .OrderBy(a => a.family)
                .ThenBy(a => a.address, StringComparer.Ordinal)
                .ToList()));
        }

        if (pages.Count > 0)
        {
            Put("pages", pages);
        }

        if (unavailable.Count > 0)
        {
            Put("unavailable", unavailable);
        }

        var returned = sections.Where(s => data.ContainsKey(s)).ToList();
        var summary = $"{DeviceTools.Count(returned.Count, "section")}: {string.Join(", ", returned)}" +
            (unavailable.Count > 0 ? $"; {DeviceTools.Count(unavailable.Count, "part")} couldn't be read" : string.Empty) +
            (pages.Count > 0 ? $". Some lists continue: pass offset {pages.Values.Min(p => p.Offset + p.Returned)} for more." : ".");

        return ToolResult.Ok(summary, data);
    }

    private async Task<JsonObject> NeighboursAsync(string query, CancellationToken cancellationToken)
    {
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var id = device.DeviceId;

        var portsTask = Sources.TryAsync(() => client.Ports.ListForDeviceAsync(id, cancellationToken), cancellationToken);
        var linksTask = fleet.GetLinksAsync(cancellationToken);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        await Task.WhenAll(portsTask, linksTask, devicesTask).ConfigureAwait(false);

        var portName = (portsTask.Result.Value ?? []).ToDictionary(p => p.PortId, p => p.IfName ?? p.IfDescr);
        var devices = devicesTask.Result.ToDictionary(d => d.DeviceId);
        var links = linksTask.Result;

        string? Name(int? deviceId) => deviceId is { } d && devices.TryGetValue(d, out var dev) ? dev.Hostname : null;

        // What this device reports about its own ports.
        var reported = links
            .Where(l => l.LocalDeviceId == id && l.Active)
            .Select(l => new Neighbour(
                portName.GetValueOrDefault(l.LocalPortId),
                l.RemoteDeviceId is > 0 ? l.RemoteDeviceId : null,
                l.RemoteDeviceId is > 0 ? Name(l.RemoteDeviceId) ?? l.RemoteHostname : l.RemoteHostname,
                l.RemotePort,
                l.RemotePlatform,
                l.Protocol,
                "this device"))
            .ToList();

        // What other devices report about this one, where this one says nothing back.
        var reverse = links
            .Where(l => l.RemoteDeviceId == id && l.LocalDeviceId != id && l.Active)
            .Where(l => !reported.Any(r => r.RemoteDeviceId == l.LocalDeviceId))
            .Select(l => new Neighbour(
                l.RemotePort,
                l.LocalDeviceId,
                Name(l.LocalDeviceId),
                null,
                null,
                l.Protocol,
                "neighbour"))
            .ToList();

        var all = reported.Concat(reverse)
            .OrderBy(n => n.LocalPort, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.RemoteHostname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shown = ToolResult.Take(all, MaxRows, out var omitted);
        var known = all.Count(n => n.RemoteDeviceId is not null);
        var summary = all.Count == 0
            ? "LibreNMS has no LLDP or CDP neighbours for this device."
            : $"{DeviceTools.Count(all.Count, "neighbour")} ({known} known to LibreNMS)" +
              (reverse.Count > 0 ? $"; {reverse.Count} only reported from the other side." : ".");

        return ToolResult.Ok(summary, new
        {
            device = DeviceBrief.From(device),
            neighbours = shown,
            unavailable = portsTask.Result.Error is { } error ? new[] { new Unavailable("ports", error) } : null,
        }, omitted);
    }

    /// <param name="SeenFrom">"this device" when it reports the link itself; "neighbour" when only the other end does.</param>
    private sealed record Neighbour(string? LocalPort, int? RemoteDeviceId, string? RemoteHostname, string? RemotePort, string? RemotePlatform, string? Protocol, string SeenFrom);

    private static HashSet<string> ParseSections(string[]? requested)
    {
        if (requested is not { Length: > 0 })
        {
            return DefaultSections.ToHashSet();
        }

        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested.SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var name = raw.ToLowerInvariant() switch
            {
                "all" => null,
                "mac" or "macs" or "mac_table" or "cam" => "fdb",
                "vlan" => "vlans",
                "bgp" or "ospf" or "vrf" or "vrfs" => "routing",
                "address" or "ip" or "ips" => "addresses",
                "uptime" => "availability",
                var other => other,
            };

            if (name is null)
            {
                sections.UnionWith(AllSections);
            }
            else if (AllSections.Contains(name))
            {
                sections.Add(name);
            }
            else
            {
                throw new ToolException($"There's no section called that. The sections are: {string.Join(", ", AllSections)}.");
            }
        }

        return sections;
    }

    /// <summary>"aa:bb:cc:dd:ee:ff", whatever form LibreNMS stored it in.</summary>
    internal static string? MacText(string? mac)
    {
        if (mac is null)
        {
            return null;
        }

        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : mac;
    }

    private static string Period(long seconds) => seconds switch
    {
        86_400 => "day",
        604_800 => "week",
        2_592_000 or 2_678_400 => "month",
        31_536_000 or 31_622_400 => "year",
        _ => $"{seconds / 86_400} days",
    };
}
