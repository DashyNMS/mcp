using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Ports;

public sealed record Neighbour(string? LocalPort, int? RemoteDeviceId, string? RemoteHostname, string? RemotePort,
    string? RemotePlatform, string? Protocol, string SeenFrom);
public sealed record DeviceNeighboursRequest(int DeviceId);
public sealed record DeviceNeighboursData(DeviceBrief Device, IReadOnlyList<Neighbour> Neighbours, IReadOnlyList<Unavailable>? Unavailable);
public sealed record DeviceNeighboursResult(string Summary, DeviceNeighboursData Data, int Omitted);

/// <summary>Both reported and reverse LLDP/CDP observations, with the same ordering and cap as MCP.</summary>
public sealed class DeviceNeighbours(ILibreNmsClient client, FleetCache fleet, DeviceDirectory directory, Redactor redactor)
{
    public const int MaxRows = 200;
    public static DeviceNeighbours Create(ILibreNmsClient client, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        return new(client, new FleetCache(client, time), new DeviceDirectory(client, time), new Redactor());
    }

    public async Task<DeviceNeighboursResult> ReadAsync(DeviceNeighboursRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be positive.");
        var device = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new DeviceNotFoundException();
        return await ReadAsync(device, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DeviceNeighboursResult> ReadAsync(Device device, CancellationToken cancellationToken)
    {
        var id = device.DeviceId;

        var portsTask = Sources.TryAsync(() => client.Ports.ListForDeviceAsync(id, cancellationToken), cancellationToken);
        var linksTask = fleet.GetLinksAsync(cancellationToken);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        await Task.WhenAll(portsTask, linksTask, devicesTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var portName = (portsTask.Result.Value ?? []).ToDictionary(p => p.PortId, p => p.IfName ?? p.IfDescr);
        var devices = devicesTask.Result.ToDictionary(d => d.DeviceId);
        var links = linksTask.Result;

        string? Name(int? deviceId) => deviceId is { } d && devices.TryGetValue(d, out var dev) ? DeviceSummary.NameOf(dev) : null;

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

        var shown = all.Take(MaxRows).Select(CleanNeighbour).ToList();
        var omitted = Math.Max(0, all.Count - MaxRows);
        var known = all.Count(n => n.RemoteDeviceId is not null);
        var summary = all.Count == 0
            ? "LibreNMS has no LLDP or CDP neighbours for this device."
            : $"{DeviceHealth.Count(all.Count, "neighbour")} ({known} known to LibreNMS)" +
              (reverse.Count > 0 ? $"; {reverse.Count} only reported from the other side." : ".");

        return new(summary, new DeviceNeighboursData(
            DeviceBrief.FromSummary(DeviceSummary.Redacted(device, new AgentOptions(), redactor)), shown,
            portsTask.Result.Error is { } error ? new[] { new Unavailable("ports", error) } : null), omitted);
    }

    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
    private Neighbour CleanNeighbour(Neighbour row) => row with
    {
        LocalPort = Clean(row.LocalPort), RemoteHostname = Clean(row.RemoteHostname), RemotePort = Clean(row.RemotePort),
        RemotePlatform = Clean(row.RemotePlatform), Protocol = Clean(row.Protocol),
    };
}
