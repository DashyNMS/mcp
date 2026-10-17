using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>One port in detail.</summary>
[McpServerToolType]
public sealed class PortTools(ILibreNmsClient client, DeviceResolver resolver, ToolRunner runner, AgentOptions options)
{
    public const int MaxCandidates = 5;

    [McpServerTool(Name = "port_status", Title = "Port status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "One port on one device: admin and operational state, speed, duplex, VLAN, current traffic and error rates, its description (alias), " +
        "and the neighbour LibreNMS sees on it (LLDP/CDP). Use it for \"is port X up?\" or \"what's plugged into X?\". " +
        "Rates are LibreNMS's last polling interval, not history. For every problem port on a device at once, use device_health.")]
    public Task<string> PortStatus(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("The port: its name (ifName, e.g. 1/1/7 or Gi0/1), description (ifDescr), alias, or LibreNMS port ID.")] string port,
        CancellationToken cancellationToken)
        => runner.RunAsync("port_status", ct => StatusAsync(device, port, ct), cancellationToken);

    private async Task<JsonObject> StatusAsync(string deviceQuery, string portQuery, CancellationToken cancellationToken)
    {
        var text = portQuery?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ToolException("Say which port: its name, description, alias or port ID.");
        }

        var device = await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false);
        var ports = await client.Ports.ListForDeviceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        var port = Resolve(ports.Where(p => !p.Deleted).ToList(), text);

        var links = await Sources.TryAsync(() => client.Links.ListForDeviceAsync(device.DeviceId, cancellationToken), cancellationToken).ConfigureAwait(false);
        var neighbours = links.Value?
            .Where(l => l.LocalPortId == port.PortId)
            .Select(l => new
            {
                protocol = l.Protocol,
                remote_hostname = l.RemoteHostname,
                remote_port = l.RemotePort,
                remote_platform = l.RemotePlatform,
                remote_device_id = l.RemoteDeviceId,
            })
            .ToList();

        var data = new
        {
            device = DeviceSummary.From(device, options),
            port = new
            {
                port_id = port.PortId,
                if_name = port.IfName,
                if_descr = port.IfDescr,
                alias = port.IfAlias,
                admin_status = port.IfAdminStatus,
                oper_status = port.IfOperStatus,
                speed_mbps = port.IfSpeed is { } bps ? bps / 1_000_000 : (long?)null,
                duplex = port.IfDuplex,
                mtu = port.IfMtu,
                vlan = port.IfVlan,
                vlans = port.Vlans.Count > 0 ? port.Vlans.Select(v => new { vlan = v.Vlan, untagged = v.Untagged }).ToList() : null,
                in_mbps = Mbps(port.IfInOctetsRate),
                out_mbps = Mbps(port.IfOutOctetsRate),
                in_errors_per_sec = PortProblem.Round(port.IfInErrorsRate),
                out_errors_per_sec = PortProblem.Round(port.IfOutErrorsRate),
                in_errors_last_poll = port.IfInErrorsDelta,
                out_errors_last_poll = port.IfOutErrorsDelta,
                disabled_in_librenms = port.Disabled ? true : (bool?)null,
            },
            neighbours,
            unavailable = links.Error is { } error ? new[] { new Unavailable("neighbours", error) } : null,
        };

        var state = DeviceTools.IsDownWhileEnabled(port) ? "down while enabled"
            : port.IsUp ? "up"
            : string.Equals(port.IfAdminStatus, "down", StringComparison.OrdinalIgnoreCase) ? "administratively down"
            : "not up";
        var errors = DeviceTools.ErrorRate(port) > 0 ? ", counting errors" : string.Empty;
        return ToolResult.Ok($"Port is {state}{errors}.", data);
    }

    private static double? Mbps(double? octetsPerSecond) => octetsPerSecond is { } o ? Math.Round(o * 8 / 1_000_000, 2) : null;

    /// <summary>Port ID, then an exact name, description or alias, then part of one - the first tier with a single match wins.</summary>
    internal static Port Resolve(IReadOnlyList<Port> ports, string text)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && ports.FirstOrDefault(p => p.PortId == id) is { } byId)
        {
            return byId;
        }

        Func<Port, bool>[] tiers =
        [
            p => Same(p.IfName, text) || Same(p.IfDescr, text) || Same(p.IfAlias, text),
            p => Contains(p.IfName, text) || Contains(p.IfDescr, text) || Contains(p.IfAlias, text),
        ];

        foreach (var tier in tiers)
        {
            var matches = ports.Where(tier).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new ToolException(
                    $"{matches.Count} ports match. Ask which one is meant, or use its port ID.",
                    new
                    {
                        candidates = matches.Take(MaxCandidates).Select(p => new { port_id = p.PortId, if_name = p.IfName, alias = p.IfAlias }).ToList(),
                        omitted = Math.Max(0, matches.Count - MaxCandidates),
                    });
            }
        }

        throw new ToolException("No port on this device matches. Check the port name, or use device_health to see its problem ports.");
    }

    private static bool Same(string? value, string text) => string.Equals(value?.Trim(), text, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
}
