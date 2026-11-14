using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

public sealed record DeviceDetailsRequest(int DeviceId, string[]? Sections = null, int Offset = 0, int Limit = 50);
public sealed record InventoryDetail(string? Class, string? Name, string? Description, string? Model, string? Serial,
    string? Manufacturer, string? HardwareRev, string? FirmwareRev, string? SoftwareRev, bool? Replaceable);
public sealed record VlanDetail(int Vlan, string? Name, IReadOnlyList<string?> UntaggedPorts, IReadOnlyList<string?> TaggedPorts);
public sealed record FdbDetail(string? Port, int PortId, string? Mac, int? Vlan, DateTime? UpdatedAt);
public sealed record ArpDetail(string? Ip, string? Mac, string? Port);
public sealed record PoeDetailBudget(string? Label, double? UsedWatts, double? TotalWatts, double? RemainingWatts, double? UsedPercent);
public sealed record PoeDetails(IReadOnlyList<PoeDetailBudget>? Budgets, int? DevicesConnected, string? None);
public sealed record WirelessDetail(string? Type, string? Description, double? Value, string? State);
public sealed record BgpDetail(string Peer, long RemoteAs, string? RemoteAsName, string? Description, string? State,
    bool? AdminDown, long? EstablishedSeconds, string? LastError);
public sealed record OspfDetail(string? Address, string? RouterId, string? State, string? Port);
public sealed record VrfDetail(string? Name, string? RouteDistinguisher, string? Description);
public sealed record RoutingDetails(IReadOnlyList<BgpDetail> BgpSessions, IReadOnlyList<OspfDetail> OspfNeighbours, IReadOnlyList<VrfDetail> Vrfs);
public sealed record AvailabilityDetail(string Over, double Percent);
public sealed record AddressDetail(string Address, string Family, string? Port);
public sealed record DeviceDetailsData
{
    public required DeviceBrief Device { get; init; }
    public IReadOnlyList<InventoryDetail>? Inventory { get; init; }
    public IReadOnlyList<VlanDetail>? Vlans { get; init; }
    public IReadOnlyList<FdbDetail>? Fdb { get; init; }
    public IReadOnlyList<ArpDetail>? Arp { get; init; }
    public PoeDetails? Poe { get; init; }
    public IReadOnlyList<WirelessDetail>? Wireless { get; init; }
    public RoutingDetails? Routing { get; init; }
    public IReadOnlyList<AvailabilityDetail>? Availability { get; init; }
    public IReadOnlyList<AddressDetail>? Addresses { get; init; }
    public IReadOnlyDictionary<string, PageInfo>? Pages { get; init; }
    public IReadOnlyList<Unavailable>? Unavailable { get; init; }
}
public sealed record DeviceDetailsResult(string Summary, DeviceDetailsData Data, IReadOnlyList<string> ReturnedSections,
    IReadOnlyDictionary<string, int> SectionTotals);

/// <summary>Typed device detail sections with MCP's section selection and paging.</summary>
/// <remarks>Use HostDeviceDetails for fixed failures and connection retirement.</remarks>
public sealed class DeviceDetails(ILibreNmsClient client, SensorSource sensorSource, DeviceDirectory directory, Redactor redactor)
{
    public const int DefaultRows = 50;
    public const int MaxRows = 200;
    public static readonly IReadOnlyList<string> AllSections = ["inventory", "vlans", "fdb", "arp", "poe", "wireless", "routing", "availability", "addresses"];
    public static readonly IReadOnlyList<string> DefaultSections = ["inventory", "vlans", "poe", "wireless", "routing", "availability", "addresses"];

    public static DeviceDetails Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        var directory = new DeviceDirectory(client, time);
        var fleet = new FleetCache(client, time);
        return new(client, new SensorSource(fleet.GetSensorsAsync, transport, time, devices: directory), directory, new Redactor());
    }

    public async Task<DeviceDetailsResult> ReadAsync(DeviceDetailsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be positive.");
        Paging.Check(request.Offset, request.Limit, MaxRows);
        var sections = ParseSections(request.Sections);
        var device = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new DeviceNotFoundException();
        return await ReadAsync(device, sections, request.Offset, request.Limit, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DeviceDetailsResult> ReadAsync(Device device, HashSet<string> sections, int offset, int limit, CancellationToken cancellationToken)
    {
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

        cancellationToken.ThrowIfCancellationRequested();
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
        var totals = new Dictionary<string, int>();

        IReadOnlyList<T> Paged<T>(string section, IReadOnlyList<T> rows)
        {
            totals[section] = rows.Count;
            var page = Paging.Apply(rows, offset, limit, out var info);
            if (info.NextOffset is not null || info.Offset > 0)
            {
                pages[section] = info;
            }

            return page;
        }

        var data = new DeviceDetailsData { Device = DeviceBrief.FromSummary(DeviceSummary.Redacted(device, new AgentOptions(), redactor)) };
        if (Use(inventory, "inventory") is { } entries)
        {
            string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : Clean(value);
            data = data with { Inventory = Paged("inventory", entries.Where(e => !e.IsPort)
                .Where(e => !string.Equals(e.Class, "container", StringComparison.OrdinalIgnoreCase) || Blank(e.Model) is not null || Blank(e.Serial) is not null)
                .OrderBy(e => e.Index).Select(e => new InventoryDetail(Clean(e.Class), Clean(e.Name), e.Description == e.Name ? null : Blank(e.Description),
                    Blank(e.Model), Blank(e.Serial), Blank(e.Manufacturer), Blank(e.HardwareRev), Blank(e.FirmwareRev), Blank(e.SoftwareRev),
                    e.IsFieldReplaceable ? true : null)).ToList()) };
        }
        if (Wants("vlans") && vlans is not null)
            data = data with { Vlans = Paged("vlans", vlanList.OrderBy(v => v.VlanNumber).Select(v => new VlanDetail(v.VlanNumber, Clean(v.VlanName),
                portList.Where(p => p.Vlans.Any(m => m.Vlan == v.VlanNumber && m.Untagged) || (p.Vlans.Count == 0 && p.IfVlan == v.VlanNumber)).Select(p => Clean(portName[p.PortId])).ToList(),
                portList.Where(p => p.Vlans.Any(m => m.Vlan == v.VlanNumber && !m.Untagged)).Select(p => Clean(portName[p.PortId])).ToList())).ToList()) };
        if (Use(fdb, "fdb") is { } macs)
        {
            var vlanNumber = vlanList.ToDictionary(v => v.VlanId, v => v.VlanNumber);
            data = data with { Fdb = Paged("fdb", macs.OrderBy(m => portName.GetValueOrDefault(m.PortId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.MacAddress, StringComparer.OrdinalIgnoreCase).Select(m => new FdbDetail(Clean(portName.GetValueOrDefault(m.PortId)), m.PortId,
                    Clean(MacText(m.MacAddress)), m.VlanId is { } v && vlanNumber.TryGetValue(v, out var n) ? n : null, m.UpdatedAt)).ToList()) };
        }
        if (Use(arp, "arp") is { } arpEntries)
            data = data with { Arp = Paged("arp", arpEntries.OrderBy(a => a.Ipv4Address, StringComparer.Ordinal)
                .Select(a => new ArpDetail(Clean(a.Ipv4Address), Clean(MacText(a.MacAddress)), Clean(portName.GetValueOrDefault(a.PortId)))).ToList()) };
        if (Use(sensors, "poe") is { } allSensors)
        {
            var poe = PoeBudget.FromSensors(allSensors);
            data = data with { Poe = poe.HasAny
                ? new(poe.Budgets.Select(b => new PoeDetailBudget(Clean(b.Label), b.UsedWatts, b.TotalWatts, b.RemainingWatts,
                    b.Percent is { } p ? Math.Round(p, 1) : null)).ToList(), poe.DevicesConnected, null)
                : new(null, null, "LibreNMS has no PoE budget sensors for this device.") };
        }
        if (Use(wireless, "wireless") is { } radios)
            data = data with { Wireless = Paged("wireless", radios.Where(r => !r.Deleted)
                .OrderBy(r => r.SensorClass, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
                .Select(r => new WirelessDetail(Clean(r.SensorClass), Clean(r.Description), r.Current,
                    WirelessFleet.Evaluate(r).ToString().ToLowerInvariant() switch { "unknown" => null, var state => state })).ToList()) };
        if (Wants("routing") && bgp is not null)
        {
            var sessions = Use(bgp, "bgp") ?? [];
            var neighbours = Use(ospf, "ospf") ?? [];
            var neighbours3 = Use(ospf3, "ospfv3") ?? [];
            data = data with { Routing = new RoutingDetails(sessions.OrderBy(s => s.IsEstablished).ThenBy(s => s.PeerIdentifier, StringComparer.Ordinal)
                .Select(s => new BgpDetail(redactor.Redact(s.PeerAddressText), s.RemoteAs, Clean(s.AsText), Clean(s.Description), Clean(s.State),
                    s.IsAdminDown ? true : null, s.IsEstablished ? s.EstablishedSeconds : null, string.IsNullOrWhiteSpace(s.LastErrorText) ? null : Clean(s.LastErrorText))).ToList(),
                neighbours.Select(n => new OspfDetail(Clean(n.IpAddress), Clean(n.RouterId), Clean(n.State), n.PortId is { } p ? Clean(portName.GetValueOrDefault(p)) : null))
                    .Concat(neighbours3.Select(n => new OspfDetail(Clean(n.Address), Clean(n.RouterId), Clean(n.State), n.PortId is { } p ? Clean(portName.GetValueOrDefault(p)) : null))).ToList(),
                (Use(vrfs, "vrfs") ?? []).Select(v => new VrfDetail(Clean(v.Name), Clean(v.RouteDistinguisher), Clean(v.Description))).ToList()) };
        }
        if (Use(availability, "availability") is { } windows)
            data = data with { Availability = windows.OrderBy(w => w.DurationSeconds)
                .Select(w => new AvailabilityDetail(Period(w.DurationSeconds), Math.Round(w.Percent, 3, MidpointRounding.AwayFromZero))).ToList() };
        if (Use(addresses, "addresses") is { } ips)
            data = data with { Addresses = Paged("addresses", ips.Select(a => new AddressDetail(
                a.IsIpv6 ? $"{a.Ipv6Compressed ?? a.Ipv6Address}/{a.Ipv6PrefixLength}" : $"{a.Ipv4Address}/{a.Ipv4PrefixLength}",
                a.IsIpv6 ? "ipv6" : "ipv4", portName.GetValueOrDefault(a.PortId)))
                .OrderBy(a => a.Family).ThenBy(a => a.Address, StringComparer.Ordinal)
                .Select(a => a with { Address = redactor.Redact(a.Address), Port = Clean(a.Port) }).ToList()) };

        if (data.Poe is { } poeDetails) totals["poe"] = poeDetails.Budgets?.Count ?? 0;
        if (data.Routing is { } routingDetails) totals["routing"] = routingDetails.BgpSessions.Count + routingDetails.OspfNeighbours.Count + routingDetails.Vrfs.Count;
        if (data.Availability is { } availabilityDetails) totals["availability"] = availabilityDetails.Count;
        data = data with { Pages = pages.Count > 0 ? pages : null, Unavailable = unavailable.Count > 0 ? unavailable : null };
        bool Returned(string section) => section switch
        {
            "inventory" => data.Inventory is not null, "vlans" => data.Vlans is not null, "fdb" => data.Fdb is not null,
            "arp" => data.Arp is not null, "poe" => data.Poe is not null, "wireless" => data.Wireless is not null,
            "routing" => data.Routing is not null, "availability" => data.Availability is not null, "addresses" => data.Addresses is not null,
            _ => false,
        };
        var returned = sections.Where(Returned).ToList();
        var summary = $"{DeviceHealth.Count(returned.Count, "section")}: {string.Join(", ", returned)}" +
            (unavailable.Count > 0 ? $"; {DeviceHealth.Count(unavailable.Count, "part")} couldn't be read" : string.Empty) +
            (pages.Count > 0 ? $". Some lists continue: pass offset {pages.Values.Min(p => p.Offset + p.Returned)} for more." : ".");
        return new(summary, data, returned, totals);
    }

    internal static HashSet<string> ParseSections(string[]? requested)
    {
        if (requested?.Any(r => r is null) == true) throw new ToolException("Every section must be text.");
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
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
}
