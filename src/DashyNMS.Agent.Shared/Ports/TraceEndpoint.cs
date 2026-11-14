using System.Net;
using System.Net.Sockets;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Ports;

public sealed record TraceEndpointRequest(string Address);
public sealed record EndpointObservation(string? Device, int DeviceId, string? Port, int PortId, string? Alias,
    IReadOnlyList<int>? Vlans, string? OperStatus, long? SpeedMbps, string Role, string? Neighbour, int? MacsOnPort, DateTime? LastSeen);
public sealed record TraceBranch(string Mac, bool Found, bool? Ambiguous, string Reason, EndpointObservation? Edge,
    IReadOnlyList<EndpointObservation>? OtherCandidates, IReadOnlyList<string?>? Path, IReadOnlyList<EndpointObservation> SeenOn);
public sealed record TraceEndpointData
{
    public string? Query { get; init; }
    public string? Ip { get; init; }
    public string? Mac { get; init; }
    public IReadOnlyList<string>? Ips { get; init; }
    public bool? Found { get; init; }
    public bool? Ambiguous { get; init; }
    public string? Reason { get; init; }
    public EndpointObservation? Edge { get; init; }
    public IReadOnlyList<EndpointObservation>? OtherCandidates { get; init; }
    public IReadOnlyList<string?>? Path { get; init; }
    public IReadOnlyList<EndpointObservation>? SeenOn { get; init; }
    public IReadOnlyList<TraceBranch>? Traces { get; init; }
    public IReadOnlyList<Unavailable>? Unavailable { get; init; }
}
/// <param name="Observations">Per-MAC omissions and source failures, including for multi-MAC replies.</param>
public sealed record TraceEndpointResult(string Summary, TraceEndpointData Data, int Omitted, IReadOnlyList<TraceEndpoint.MacTrace> Observations);

/// <summary>Stored ARP and forwarding-table observations, never proof of endpoint ownership.</summary>
public sealed class TraceEndpoint(ILibreNmsClient client, ILibreNmsTransport transport, FleetCache fleet,
    DeviceDirectory directory, AgentOptions options, Redactor redactor)
{
    public const int UplinkMacCount = 10;
    public const int MaxSwitches = 25;
    public const int MaxMacsPerIp = 3;
    public const int MaxSeenOn = 50;

    public static TraceEndpoint Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        return new(client, transport, new FleetCache(client, time), new DeviceDirectory(client, time), options, new Redactor());
    }

    public async Task<TraceEndpointResult> ReadAsync(TraceEndpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var text = request.Address?.Trim() ?? string.Empty;
        string? ip = null;
        List<string> macs;
        Fetched<IReadOnlyList<ArpEntry>>? arpForMac = null;

        if (Mac(text) is { } mac)
        {
            macs = [mac];
            arpForMac = await Sources.TryAsync(() => client.Arp.FindByMacAsync(mac, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        else if (IPAddress.TryParse(text, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') == 3)
        {
            ip = parsed.ToString();
            var arp = await NotFoundIsEmpty(transport.GetCollectionAsync<ArpEntry>("resources/ip/arp/" + ip, "arp", cancellationToken)).ConfigureAwait(false);
            macs = arp.Select(a => Mac(a.MacAddress ?? string.Empty)).OfType<string>().Distinct().ToList();
            if (macs.Count == 0)
            {
                return new("No device's ARP table has this IP, so there's no MAC to trace. It may be offline, on a network LibreNMS doesn't poll, or not yet polled.", new TraceEndpointData { Ip = ip, Found = false }, 0, []);
            }
        }
        else if (IPAddress.TryParse(text, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6)
        {
            throw new ToolException("Only IPv4 addresses can be traced: LibreNMS's ARP tables don't hold IPv6 neighbours. Give the MAC instead.");
        }
        else
        {
            throw new ToolException("Give a MAC address (such as aa:bb:cc:dd:ee:ff) or an IPv4 address.");
        }

        var traced = new List<MacTrace>();
        foreach (var each in macs.Take(MaxMacsPerIp))
        {
            traced.Add(await TraceMacAsync(each, cancellationToken).ConfigureAwait(false));
        }

        if (traced.Count == 1)
        {
            var only = traced[0];
            var ips = ip is not null ? [ip] : (arpForMac?.Value ?? []).Select(a => a.Ipv4Address).OfType<string>().Distinct().Order(StringComparer.Ordinal).Select(redactor.Redact).ToList();
            return new(only.Summary, new TraceEndpointData
            {
                Query = text, Mac = only.Mac, Ips = ips.Count > 0 ? ips : null, Found = only.Found,
                Ambiguous = only.Found ? only.Ambiguous : null, Reason = only.Reason, Edge = only.Edge,
                OtherCandidates = only.OtherCandidates.Count > 0 ? only.OtherCandidates : null,
                Path = only.Path.Count > 1 ? only.Path : null, SeenOn = only.SeenOn,
                Unavailable = only.Unavailable.Count > 0 ? only.Unavailable : null,
            }, only.Omitted, traced);
        }

        var summary = $"{macs.Count} different MACs have this IP in LibreNMS's ARP tables, so it's ambiguous which device holds it; each MAC is traced." +
            (macs.Count > MaxMacsPerIp ? $" Only the first {MaxMacsPerIp} are shown." : string.Empty);
        return new(summary, new TraceEndpointData
        {
            Query = text, Ip = ip, Ambiguous = true,
            Traces = traced.Select(t => new TraceBranch(t.Mac, t.Found, t.Found ? t.Ambiguous : null, t.Reason, t.Edge,
                t.OtherCandidates.Count > 0 ? t.OtherCandidates : null, t.Path.Count > 1 ? t.Path : null, t.SeenOn)).ToList(),
        }, Math.Max(0, macs.Count - MaxMacsPerIp), traced);
    }

    private async Task<MacTrace> TraceMacAsync(string mac, CancellationToken cancellationToken)
    {
        var stored = mac.Replace(":", string.Empty, StringComparison.Ordinal);
        var fdbTask = NotFoundIsEmpty(transport.GetCollectionAsync<FdbEntry>("resources/fdb/" + stored, "ports_fdb", cancellationToken));
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var linksTask = Sources.TryAsync(() => fleet.GetLinksAsync(cancellationToken), cancellationToken);
        var vlansTask = Sources.TryAsync(() => client.Vlans.ListAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(fdbTask, devicesTask, linksTask, vlansTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var rows = fdbTask.Result;
        if (rows.Count == 0)
        {
            return MacTrace.NotFound(mac,
                "No switch's forwarding table has this MAC. It may be offline, behind a switch LibreNMS doesn't poll the FDB of, or not yet polled.");
        }

        var unavailable = new List<Unavailable>();
        if (linksTask.Result.Error is { } linksError)
        {
            unavailable.Add(new Unavailable("links", linksError));
        }

        var devices = devicesTask.Result.ToDictionary(d => d.DeviceId);
        var links = linksTask.Result.Value ?? [];
        var vlanNumber = (vlansTask.Result.Value ?? []).ToDictionary(v => v.VlanId, v => v.VlanNumber);

        // The most recently updated switches first, so a cap drops the stalest.
        var switches = rows
            .GroupBy(r => r.DeviceId)
            .OrderByDescending(g => g.Max(r => r.UpdatedAt))
            .Select(g => g.Key)
            .ToList();
        var looked = switches.Take(MaxSwitches).ToList();

        var portsTasks = looked.ToDictionary(id => id, id => Sources.TryAsync(() => client.Ports.ListForDeviceAsync(id, cancellationToken), cancellationToken));
        await Task.WhenAll(portsTasks.Values).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var ports = new Dictionary<int, Port>();
        foreach (var (id, task) in portsTasks)
        {
            if (task.Result.Error is { } error)
            {
                unavailable.Add(new Unavailable($"ports on device {id}", error));
            }

            foreach (var p in task.Result.Value ?? [])
            {
                ports[p.PortId] = p;
            }
        }

        // A port learns the MAC once per VLAN (a firewall or server on a trunk): one row per port, with its VLANs.
        var vlansOnPort = rows
            .GroupBy(r => r.PortId)
            .ToDictionary(g => g.Key, g => g
                .Select(r => r.VlanId is { } v && vlanNumber.TryGetValue(v, out var number) ? number : (int?)null)
                .OfType<int>()
                .Distinct()
                .Order()
                .ToList());

        // Uplinks first, from what's known without another call.
        var classified = rows
            .Where(r => looked.Contains(r.DeviceId))
            .GroupBy(r => r.PortId)
            .Select(g => g.MaxBy(r => r.UpdatedAt)!)
            .Select(r => Classify(r, ports.GetValueOrDefault(r.PortId), links, devices))
            .ToList();

        // Then how many MACs each remaining port holds, from its switch's FDB.
        var countDevices = classified.Where(c => c.Role == Role.Candidate).Select(c => c.Row.DeviceId).Distinct().ToList();
        var fdbTasks = countDevices.ToDictionary(id => id, id => Sources.TryAsync(() => client.Fdb.ListForDeviceAsync(id, cancellationToken), cancellationToken));
        await Task.WhenAll(fdbTasks.Values).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var macsOnPort = new Dictionary<int, int>();
        foreach (var (id, task) in fdbTasks)
        {
            if (task.Result.Error is { } error)
            {
                unavailable.Add(new Unavailable($"MAC table on device {id}", error));
            }

            foreach (var group in (task.Result.Value ?? []).GroupBy(f => f.PortId))
            {
                macsOnPort[group.Key] = group.Select(f => f.MacAddress).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            }
        }

        classified = classified
            .Select(c => c with { MacsOnPort = macsOnPort.TryGetValue(c.Row.PortId, out var n) ? n : null })
            .Select(c => c is { Role: Role.Candidate, MacsOnPort: > UplinkMacCount } ? c with { Role = Role.Busy } : c)
            .ToList();

        var candidates = classified
            .Where(c => c.Role == Role.Candidate)
            .OrderBy(c => c.MacsOnPort ?? int.MaxValue)
            .ThenByDescending(c => c.Row.UpdatedAt)
            .ToList();

        // Only busy ports: a host with many MACs (a firewall with a MAC per VLAN, a hypervisor) or an
        // unmanaged switch looks like this. The quietest is still the best guess, said to be one.
        var busyOnly = candidates.Count == 0;
        if (busyOnly)
        {
            candidates = classified
                .Where(c => c.Role == Role.Busy)
                .OrderBy(c => c.MacsOnPort ?? int.MaxValue)
                .ThenByDescending(c => c.Row.UpdatedAt)
                .ToList();
        }

        var best = candidates.FirstOrDefault();
        var ambiguous = busyOnly || candidates.Count != 1;
        string reason;
        if (best is not null && busyOnly)
        {
            reason = $"Every port that learned it is either a link to another switch or holds many MACs; the quietest, with {DeviceHealth.Count(best.MacsOnPort ?? 0, "MAC")}, is the best guess. " +
                "A host with a MAC per VLAN (a firewall, a hypervisor) or an unmanaged switch behind the port looks like this.";
        }
        else if (best is null)
        {
            reason = "Every port that learned this MAC looks like a link to another switch, so the edge switch is probably one LibreNMS doesn't poll the FDB of (or it's a wireless client, or a VM behind a host).";
        }
        else if (candidates.Count == 1)
        {
            reason = $"The only port that learned it without being a link to another switch{(best.MacsOnPort is { } n ? $", holding {DeviceHealth.Count(n, "MAC")}" : string.Empty)}.";
        }
        else
        {
            reason = $"{candidates.Count} ports learned it without being links to other switches; the one with the fewest MACs is shown first. An unmanaged switch, a phone with a PC behind it, or a missing LLDP neighbour can do this.";
        }

        // The path: from the edge switch, each switch whose port toward the previous one learned the MAC.
        var path = new List<string?>();
        if (best is not null)
        {
            var current = best.Row.DeviceId;
            var visited = new HashSet<int>();
            while (visited.Add(current))
            {
                path.Add(Clean(Name(devices, current)));
                var up = classified.FirstOrDefault(c => c.Role == Role.Uplink && c.NeighbourDeviceId == current && !visited.Contains(c.Row.DeviceId));
                if (up is null)
                {
                    break;
                }

                current = up.Row.DeviceId;
            }
        }

        EndpointObservation Describe(Classified c) => new(
            Clean(Name(devices, c.Row.DeviceId)), c.Row.DeviceId, Clean(c.Port?.IfName ?? c.Port?.IfDescr), c.Row.PortId,
            string.IsNullOrWhiteSpace(c.Port?.IfAlias) || c.Port?.IfAlias == c.Port?.IfName ? null : Clean(c.Port?.IfAlias),
            vlansOnPort.TryGetValue(c.Row.PortId, out var vlans) && vlans.Count > 0 ? vlans : null,
            Clean(c.Port?.IfOperStatus), c.Port?.IfSpeed is { } bps ? bps / 1_000_000 : null,
            c.Role switch { Role.Candidate => "edge", Role.Uplink => "uplink", Role.Aggregate => "aggregate", Role.Virtual => "vlan interface", _ => "busy port" },
            Clean(c.Neighbour), c.MacsOnPort, Sources.Utc(c.Row.UpdatedAt, options));

        var seenOn = classified
            .OrderBy(c => c.Role)
            .ThenBy(c => Name(devices, c.Row.DeviceId), StringComparer.OrdinalIgnoreCase)
            .Select(Describe)
            .ToList();
        var shown = seenOn.Take(MaxSeenOn).ToList();
        var omitted = Math.Max(0, seenOn.Count - MaxSeenOn);
        if (switches.Count > MaxSwitches)
        {
            unavailable.Add(new Unavailable("switches", $"The MAC is on {switches.Count} switches; only the {MaxSwitches} most recently updated were checked."));
        }

        var summary = best is null
            ? $"Seen on {(switches.Count == 1 ? "1 switch" : $"{switches.Count} switches")}, but only on links between switches: no clear edge port."
            : $"{(busyOnly ? "Probably" : "Most likely")} on {Name(devices, best.Row.DeviceId) ?? "a switch"}, port {best.Port?.IfName ?? best.Row.PortId.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
              (busyOnly ? ", though that port holds many MACs, so it is a guess." : candidates.Count > 1 ? $", but {candidates.Count - 1} other port{(candidates.Count == 2 ? " is" : "s are")} possible too." : ".");

        return new MacTrace(
            mac,
            Found: true,
            ambiguous,
            reason,
            best is null ? null : Describe(best),
            candidates.Skip(1).Select(Describe).ToList(),
            path,
            shown,
            omitted,
            unavailable,
            redactor.Redact(summary));
    }

    private static Classified Classify(FdbEntry row, Port? port, IReadOnlyList<NetworkLink> links, Dictionary<int, Device> devices)
    {
        var link = links.FirstOrDefault(l => l.Active && l.LocalPortId == row.PortId && l.RemoteDeviceId is > 0 && l.RemoteDeviceId != row.DeviceId);
        if (link is not null)
        {
            return new Classified(row, port, Role.Uplink, link.RemoteDeviceId, Name(devices, link.RemoteDeviceId!.Value) ?? link.RemoteHostname, null);
        }

        var type = port?.IfType ?? string.Empty;
        if (type.Contains("Lag", StringComparison.OrdinalIgnoreCase) || type.Contains("aggregat", StringComparison.OrdinalIgnoreCase))
        {
            return new Classified(row, port, Role.Aggregate, null, null, null);
        }

        if (type is "l3ipvlan" or "propVirtual" or "l2vlan" or "bridge")
        {
            return new Classified(row, port, Role.Virtual, null, null, null);
        }

        // A neighbour LibreNMS doesn't know (an AP or phone, often the endpoint itself) doesn't make it an uplink.
        var other = links.FirstOrDefault(l => l.Active && l.LocalPortId == row.PortId);
        return new Classified(row, port, Role.Candidate, null, other?.RemoteHostname, null);
    }

    private static string? Name(Dictionary<int, Device> devices, int id) => devices.TryGetValue(id, out var d) ? DeviceSummary.NameOf(d) : null;

    /// <summary>"aa:bb:cc:dd:ee:ff" from any common form, or null when it isn't a MAC.</summary>
    internal static string? Mac(string text)
    {
        if (text.Length is < 12 or > 17 || text.Any(c => !Uri.IsHexDigit(c) && c is not (':' or '-' or '.')))
        {
            return null;
        }

        var hex = new string(text.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null;
    }

    /// <summary>LibreNMS answers a lookup with no match with a 404 ("Fdb entry does not exist"); that's an empty answer, not a failure.</summary>
    private static async Task<IReadOnlyList<T>> NotFoundIsEmpty<T>(Task<IReadOnlyList<T>> request)
    {
        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (LibreNmsApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    /// <summary>In the order seen_on lists them: the answer first, then why the rest were passed over.</summary>
    private enum Role
    {
        Candidate,
        Busy,
        Uplink,
        Aggregate,
        Virtual,
    }

    private sealed record Classified(FdbEntry Row, Port? Port, Role Role, int? NeighbourDeviceId, string? Neighbour, int? MacsOnPort);

    public sealed record MacTrace(
        string Mac,
        bool Found,
        bool Ambiguous,
        string Reason,
        EndpointObservation? Edge,
        IReadOnlyList<EndpointObservation> OtherCandidates,
        IReadOnlyList<string?> Path,
        IReadOnlyList<EndpointObservation> SeenOn,
        int Omitted,
        IReadOnlyList<Unavailable> Unavailable,
        string Summary)
    {
        public static MacTrace NotFound(string mac, string reason) => new(mac, false, false, reason, null, [], [], [], 0, [], reason);
    }
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
}
