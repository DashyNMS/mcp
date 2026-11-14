using System.Globalization;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Ports;

public sealed record PortStatusRequest(int DeviceId, string Port);
public sealed record PortNeighbour(string? Protocol, string? RemoteHostname, string? RemotePort, string? RemotePlatform, int? RemoteDeviceId);
public sealed record PortVlan(int Vlan, bool Untagged);
public sealed record PortReading(int PortId, string? IfName, string? IfDescr, string? Alias, string? AdminStatus, string? OperStatus,
    long? SpeedMbps, string? Duplex, int? Mtu, int? Vlan, IReadOnlyList<PortVlan>? Vlans, double? InMbps, double? OutMbps,
    double? InErrorsPerSec, double? OutErrorsPerSec, long? InErrorsLastPoll, long? OutErrorsLastPoll, bool? DisabledInLibrenms);
public sealed record PortStatusData(DeviceSummary Device, PortReading Port, IReadOnlyList<PortNeighbour>? Neighbours,
    IReadOnlyList<Unavailable>? Unavailable);
public sealed record PortStatusResult(string Summary, PortStatusData Data);

/// <summary>Port identity, rates and neighbour observations, from the same execution MCP uses.</summary>
public sealed class PortStatus(ILibreNmsClient client, DeviceDirectory directory, AgentOptions options, Redactor redactor)
{
    public const int MaxCandidates = 5;
    public static PortStatus Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        return new(client, new DeviceDirectory(client, time), options, new Redactor());
    }

    public async Task<PortStatusResult> ReadAsync(PortStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId <= 0 || string.IsNullOrWhiteSpace(request.Port)) throw new ToolException("A visible device ID and port are required.");
        var device = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new DeviceNotFoundException();
        return await ReadAsync(device, request.Port, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PortStatusResult> ReadAsync(Device device, string portQuery, CancellationToken cancellationToken)
    {
        var ports = await client.Ports.ListForDeviceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        var port = Resolve(ports.Where(p => !p.Deleted).ToList(), portQuery.Trim());

        var links = await Sources.TryAsync(() => client.Links.ListForDeviceAsync(device.DeviceId, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var neighbours = links.Value?.Where(l => l.LocalPortId == port.PortId)
            .Select(l => new PortNeighbour(Clean(l.Protocol), Clean(l.RemoteHostname), Clean(l.RemotePort), Clean(l.RemotePlatform), l.RemoteDeviceId)).ToList();
        var data = new PortStatusData(DeviceSummary.Redacted(device, options, redactor),
            new PortReading(port.PortId, Clean(port.IfName), Clean(port.IfDescr), Clean(port.IfAlias), Clean(port.IfAdminStatus), Clean(port.IfOperStatus),
                port.IfSpeed is { } bps ? bps / 1_000_000 : null, Clean(port.IfDuplex), port.IfMtu, port.IfVlan,
                port.Vlans.Count > 0 ? port.Vlans.Select(v => new PortVlan(v.Vlan, v.Untagged)).ToList() : null,
                Mbps(port.IfInOctetsRate), Mbps(port.IfOutOctetsRate), PortProblem.Round(port.IfInErrorsRate), PortProblem.Round(port.IfOutErrorsRate),
                port.IfInErrorsDelta, port.IfOutErrorsDelta, port.Disabled ? true : null), neighbours,
            links.Error is { } error ? new[] { new Unavailable("neighbours", error) } : null);

        var state = DeviceHealth.IsDownWhileEnabled(port) ? "down while enabled"
            : port.IsUp ? "up"
            : string.Equals(port.IfAdminStatus, "down", StringComparison.OrdinalIgnoreCase) ? "administratively down"
            : "not up";
        var errors = DeviceHealth.ErrorRate(port) > 0 ? ", counting errors" : string.Empty;
        return new($"Port is {state}{errors}.", data);
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
    private string? Clean(string? text) => text is null ? null : redactor.Redact(text);
}
