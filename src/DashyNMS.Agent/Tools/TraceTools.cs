using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// "Where is this plugged in?" (#19): from a MAC or IP address to the switch
/// port it's on, with the evidence - the PoC brief's stretch goal.
/// </summary>
/// <remarks>
/// <para>
/// Every switch on the way learns the MAC, so the forwarding tables alone
/// give a list of ports, not an answer. The edge port is the one that isn't
/// a link to another switch: a port with an LLDP or CDP neighbour LibreNMS
/// knows, an aggregate, a VLAN interface or a port holding many MACs is
/// passed over. What's left, fewest MACs first, is the answer - and when
/// more than one port is left, or none, the result says it's ambiguous
/// rather than guessing.
/// </para>
/// <para>
/// LibreNMS looks a MAC up across every device's FDB with
/// <c>resources/fdb/{mac}</c>, which Core doesn't wrap; it only matches the
/// form LibreNMS stores (12 lowercase hex digits, checked live), so that's
/// what goes in the URL.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class TraceTools(
    ILibreNmsClient client,
    ILibreNmsTransport transport,
    FleetCache fleet,
    DeviceDirectory directory,
    ToolRunner runner,
    AgentOptions options)
{
    /// <summary>A port with more MACs than this is carrying other switches' traffic.</summary>
    public const int UplinkMacCount = 10;

    /// <summary>The switches looked at closely (ports and MAC counts), at most. A gateway's MAC can be on every switch.</summary>
    public const int MaxSwitches = 25;

    /// <summary>The MACs traced for one IP, at most: more means an ARP table full of stale or proxy entries.</summary>
    public const int MaxMacsPerIp = 3;

    public const int MaxSeenOn = 50;

    [McpServerTool(Name = "trace_endpoint", Title = "Trace endpoint", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Finds where a MAC or IPv4 address is plugged in: the switch and edge port, with its alias, VLAN and state. " +
        "An IP is turned into its MAC through LibreNMS's ARP tables, then every switch whose forwarding table has learned the MAC is checked, " +
        "and ports that are links to other switches (LLDP/CDP neighbours, aggregates, ports with many MACs) are passed over. " +
        "Also gives every port the MAC was seen on, with its role, and the path of switches from the edge upwards. " +
        "Says when the answer is ambiguous - several possible edge ports, or only uplinks - rather than guessing. " +
        "Only as current as LibreNMS's last FDB and ARP poll.")]
    public Task<string> TraceEndpoint(
        [Description("A MAC address (any common form: aa:bb:cc:dd:ee:ff, aabb.ccdd.eeff, AA-BB-...) or an IPv4 address.")] string address,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("trace_endpoint", ct => TraceAsync(address, ct), cancellationToken);

    private async Task<JsonObject> TraceAsync(string address, CancellationToken cancellationToken)
    {
        var text = address?.Trim() ?? string.Empty;
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
                return ToolResult.Ok(
                    "No device's ARP table has this IP, so there's no MAC to trace. It may be offline, on a network LibreNMS doesn't poll, or not yet polled.",
                    new { ip, found = false });
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
            var ips = ip is not null ? [ip] : (arpForMac?.Value ?? []).Select(a => a.Ipv4Address).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
            return ToolResult.Ok(only.Summary, new
            {
                query = text,
                mac = only.Mac,
                ips = ips.Count > 0 ? ips : null,
                found = only.Found,
                ambiguous = only.Found ? only.Ambiguous : (bool?)null,
                reason = only.Reason,
                edge = only.Edge,
                other_candidates = only.OtherCandidates.Count > 0 ? only.OtherCandidates : null,
                path = only.Path.Count > 1 ? only.Path : null,
                seen_on = only.SeenOn,
                unavailable = only.Unavailable.Count > 0 ? only.Unavailable : null,
            }, only.Omitted);
        }

        // One IP, several MACs: a VRRP pair, a moved address, or stale ARP. Each is traced and the result says so.
        var traces = traced;
        return ToolResult.Ok(
            $"{macs.Count} different MACs have this IP in LibreNMS's ARP tables, so it's ambiguous which device holds it; each MAC is traced." +
            (macs.Count > MaxMacsPerIp ? $" Only the first {MaxMacsPerIp} are shown." : string.Empty),
            new
            {
                query = text,
                ip,
                ambiguous = true,
                traces = traces.Select(t => new
                {
                    mac = t.Mac,
                    found = t.Found,
                    ambiguous = t.Found ? t.Ambiguous : (bool?)null,
                    reason = t.Reason,
                    edge = t.Edge,
                    other_candidates = t.OtherCandidates.Count > 0 ? t.OtherCandidates : null,
                    path = t.Path.Count > 1 ? t.Path : null,
                    seen_on = t.SeenOn,
                }).ToList(),
            },
            Math.Max(0, macs.Count - MaxMacsPerIp));
    }

    private async Task<MacTrace> TraceMacAsync(string mac, CancellationToken cancellationToken)
    {
        var stored = mac.Replace(":", string.Empty, StringComparison.Ordinal);
        var fdbTask = NotFoundIsEmpty(transport.GetCollectionAsync<FdbEntry>("resources/fdb/" + stored, "ports_fdb", cancellationToken));
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var linksTask = Sources.TryAsync(() => fleet.GetLinksAsync(cancellationToken), cancellationToken);
        var vlansTask = Sources.TryAsync(() => client.Vlans.ListAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(fdbTask, devicesTask, linksTask, vlansTask).ConfigureAwait(false);

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
            reason = $"Every port that learned it is either a link to another switch or holds many MACs; the quietest, with {DeviceTools.Count(best.MacsOnPort ?? 0, "MAC")}, is the best guess. " +
                "A host with a MAC per VLAN (a firewall, a hypervisor) or an unmanaged switch behind the port looks like this.";
        }
        else if (best is null)
        {
            reason = "Every port that learned this MAC looks like a link to another switch, so the edge switch is probably one LibreNMS doesn't poll the FDB of (or it's a wireless client, or a VM behind a host).";
        }
        else if (candidates.Count == 1)
        {
            reason = $"The only port that learned it without being a link to another switch{(best.MacsOnPort is { } n ? $", holding {DeviceTools.Count(n, "MAC")}" : string.Empty)}.";
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
                path.Add(Name(devices, current));
                var up = classified.FirstOrDefault(c => c.Role == Role.Uplink && c.NeighbourDeviceId == current && !visited.Contains(c.Row.DeviceId));
                if (up is null)
                {
                    break;
                }

                current = up.Row.DeviceId;
            }
        }

        object Describe(Classified c) => new
        {
            device = Name(devices, c.Row.DeviceId),
            device_id = c.Row.DeviceId,
            port = c.Port?.IfName ?? c.Port?.IfDescr,
            port_id = c.Row.PortId,
            alias = string.IsNullOrWhiteSpace(c.Port?.IfAlias) || c.Port?.IfAlias == c.Port?.IfName ? null : c.Port?.IfAlias,
            vlans = vlansOnPort.TryGetValue(c.Row.PortId, out var vlans) && vlans.Count > 0 ? vlans : null,
            oper_status = c.Port?.IfOperStatus,
            speed_mbps = c.Port?.IfSpeed is { } bps ? bps / 1_000_000 : (long?)null,
            role = c.Role switch
            {
                Role.Candidate => "edge",
                Role.Uplink => "uplink",
                Role.Aggregate => "aggregate",
                Role.Virtual => "vlan interface",
                _ => "busy port",
            },
            neighbour = c.Neighbour,
            macs_on_port = c.MacsOnPort,
            last_seen = Sources.Utc(c.Row.UpdatedAt, options),
        };

        var seenOn = classified
            .OrderBy(c => c.Role)
            .ThenBy(c => Name(devices, c.Row.DeviceId), StringComparer.OrdinalIgnoreCase)
            .Select(Describe)
            .ToList();
        var shown = ToolResult.Take(seenOn, MaxSeenOn, out var omitted);
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
            summary);
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

    private static string? Name(Dictionary<int, Device> devices, int id) => devices.TryGetValue(id, out var d) ? d.Hostname : null;

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

    private sealed record MacTrace(
        string Mac,
        bool Found,
        bool Ambiguous,
        string Reason,
        object? Edge,
        IReadOnlyList<object> OtherCandidates,
        IReadOnlyList<string?> Path,
        IReadOnlyList<object> SeenOn,
        int Omitted,
        IReadOnlyList<Unavailable> Unavailable,
        string Summary)
    {
        public static MacTrace NotFound(string mac, string reason) => new(mac, false, false, reason, null, [], [], [], 0, [], reason);
    }
}
