using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Rankings;

public sealed record TopPortsRequest(bool Errors = false, string? By = null, int Limit = 10, string? Group = null, string? Location = null);
public sealed record TopDevicesRequest(string? By = null, int Limit = 10, string? Group = null, string? Location = null);
public sealed record TopPortRow(DeviceBrief? Device, int PortId, string? Port, string? Alias, double? InMbps, double? OutMbps,
    double? InErrorsPerSec, double? OutErrorsPerSec, double? SpeedMbps, double? BusiestDirectionPercent);
public sealed record TopDeviceRow(DeviceBrief? Device, double? InMbps, double? OutMbps, int? ActivePorts,
    int? PortsDownWhileEnabled, int? PortsCountingErrors);
public sealed record TopPortsData(IReadOnlyList<TopPortRow> Ports);
public sealed record TopDevicesData(IReadOnlyList<TopDeviceRow> Devices);
public sealed record TopPortsResult(string Summary, TopPortsData Data, int CandidatesTotal, int PortsChecked);
public sealed record TopDevicesResult(string Summary, TopDevicesData Data, int CandidatesTotal, int PortsChecked);

/// <summary>Core's fleet rankings, scoped and shaped identically for MCP and host adapters.</summary>
public sealed class FleetRankings(FleetCache fleet, DeviceDirectory directory, DeviceScope scopes, Redactor redactor)
{
    public const int MaxTop = 50;
    public static FleetRankings Create(ILibreNmsClient client, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        var directory = new DeviceDirectory(client, time);
        return new(new FleetCache(client, time), directory, new DeviceScope(client, directory, time), new Redactor());
    }

    public async Task<TopPortsResult> ReadPortsAsync(TopPortsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (errors, byText, limit, group, location) = request;
        var by = ParseRank(byText);
        Paging.Check(0, limit, MaxTop);
        var (devices, ports) = await ScopedPortsAsync(group, location, cancellationToken).ConfigureAwait(false);

        var ranked = errors ? PortRankings.TopErrors(ports, by, int.MaxValue) : PortRankings.TopTraffic(ports, by, int.MaxValue);
        var rows = ranked.Take(limit).Select(r =>
        {
            var speedMbps = r.Port.IfSpeed is > 0 ? r.Port.IfSpeed.Value / 1_000_000d : (double?)null;
            return new TopPortRow(devices.TryGetValue(r.Port.DeviceId, out var d) ? Brief(d) : null,
                r.Port.PortId, Clean(r.Port.IfName ?? r.Port.IfDescr),
                string.IsNullOrWhiteSpace(r.Port.IfAlias) || r.Port.IfAlias == r.Port.IfName ? null : Clean(r.Port.IfAlias),
                errors ? null : Math.Round(r.In / 1_000_000, 2), errors ? null : Math.Round(r.Out / 1_000_000, 2),
                errors ? Math.Round(r.In, 2) : null, errors ? Math.Round(r.Out, 2) : null, speedMbps,
                !errors && speedMbps is > 0 ? Math.Round(Math.Max(r.In, r.Out) / 1_000_000 / speedMbps.Value * 100, 1) : null);
        }).ToList();

        var summary = rows.Count == 0
            ? errors ? "No port is counting errors." : "No port is carrying traffic."
            : $"The {DeviceHealth.Count(rows.Count, "port")} {(errors ? "counting the most errors" : "carrying the most traffic")}, ranked by {by.ToString().ToLowerInvariant()}, out of {ports.Count} checked.";
        return new(summary, new TopPortsData(rows), ranked.Count, ports.Count);
    }

    public async Task<TopDevicesResult> ReadDevicesAsync(TopDevicesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (byText, limit, group, location) = request;
        var problems = string.Equals(byText?.Trim(), "problems", StringComparison.OrdinalIgnoreCase);
        var by = problems ? RankBy.Total : ParseRank(byText is null || string.Equals(byText.Trim(), "traffic", StringComparison.OrdinalIgnoreCase) ? null : byText);
        Paging.Check(0, limit, MaxTop);
        var (devices, ports) = await ScopedPortsAsync(group, location, cancellationToken).ConfigureAwait(false);

        if (problems)
        {
            var candidates = ports.GroupBy(p => p.DeviceId)
                .Select(g => (DeviceId: g.Key, Down: g.Count(DeviceHealth.IsDownWhileEnabled), Errors: g.Count(p => DeviceHealth.ErrorRate(p) > 0)))
                .Where(x => x.Down + x.Errors > 0).OrderByDescending(x => x.Down + x.Errors).ThenByDescending(x => x.Down).ToList();
            var rows = candidates.Take(limit).Select(x => new TopDeviceRow(
                devices.TryGetValue(x.DeviceId, out var d) ? Brief(d) : null, null, null, null, x.Down, x.Errors)).ToList();
            return new(rows.Count == 0 ? "No device has ports down while enabled or counting errors." : $"The {DeviceHealth.Count(rows.Count, "device")} with the most port problems.",
                new TopDevicesData(rows), candidates.Count, ports.Count);
        }

        var traffic = PortRankings.TopDevices(ports, by, int.MaxValue);
        var ranked = traffic.Take(limit).Select(r => new TopDeviceRow(devices.TryGetValue(r.DeviceId, out var d) ? Brief(d) : null,
            Math.Round(r.In / 1_000_000, 2), Math.Round(r.Out / 1_000_000, 2), r.ActivePorts, null, null)).ToList();
        return new(ranked.Count == 0 ? "No device is carrying traffic." : $"The {DeviceHealth.Count(ranked.Count, "device")} carrying the most traffic, ranked by {by.ToString().ToLowerInvariant()}. Traffic is every port's summed, so traffic passing through counts twice.",
            new TopDevicesData(ranked), traffic.Count, ports.Count);
    }

    /// <summary>The fleet port snapshot, live ports only, cut to the group or location asked for.</summary>
    private async Task<(IReadOnlyDictionary<int, Device> Devices, IReadOnlyList<Port> Ports)> ScopedPortsAsync(string? group, string? location, CancellationToken cancellationToken)
    {
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var portsTask = fleet.GetPortsAsync(cancellationToken);
        await Task.WhenAll(devicesTask, portsTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var devices = devicesTask.Result.ToDictionary(d => d.DeviceId);
        var ports = portsTask.Result
            .Where(p => !p.Deleted && !p.Disabled && !p.Ignore)
            .Where(p => devices.TryGetValue(p.DeviceId, out var d) && scope.Includes(d) && !d.Disabled)
            .ToList();
        return (devices, ports);
    }

    private static RankBy ParseRank(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "total" or "both" => RankBy.Total,
        "in" or "rx" or "inbound" => RankBy.In,
        "out" or "tx" or "outbound" => RankBy.Out,
        _ => throw new ToolException("By must be total, in or out."),
    };

    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
    private DeviceBrief Brief(Device device) => DeviceBrief.FromSummary(DeviceSummary.Redacted(device, new AgentOptions(), redactor));
}
