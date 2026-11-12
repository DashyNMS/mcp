using DashyNMS.Agent.Alerts;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Overview;

public sealed record OverviewDeviceCounts(int Total, int Up, int Down, int Disabled, int Ignored);
public sealed record OverviewPortCounts(int Total, int DownWhileEnabled, int CountingErrors);
public sealed record DownLocation(string Location, int Down);
/// <summary>Fixed categories only; no server bodies, exception text or URLs.</summary>
public sealed record OverviewUnavailable(string Source, HostFailure Code);
/// <summary>Counts cover all visible rows; detail lists are bounded independently.</summary>
public sealed record NetworkOverviewResult(OverviewDeviceCounts Devices, OpenAlertTotals? Alerts,
    OverviewPortCounts? Ports, string? LibreNmsVersion, IReadOnlyList<DownLocation> DownLocations,
    int DownLocationsOmitted, IReadOnlyList<DeviceSummary> DownDevices, int DownDevicesOmitted,
    IReadOnlyList<OpenAlertGroup> AlertGroups, int AlertGroupsOmitted, IReadOnlyList<OverviewUnavailable> Unavailable);

/// <summary>
/// One maintained overview for MCP and in-process hosts. Devices are required; alerts, ports
/// and system are independent optional sources. Failed sources are null, never zero.
/// Hosts own the client, connection generation, transport and deadline.
/// </summary>
public sealed class NetworkOverview
{
    public const int DetailLimit = 20;
    public const int LocationLimit = 10;
    private readonly ILibreNmsClient client;
    private readonly DeviceDirectory directory;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Port>>> ports;
    private readonly AgentOptions options;
    private readonly Redactor redactor;
    private readonly OpenAlerts alerts;

    internal NetworkOverview(ILibreNmsClient client, DeviceDirectory directory,
        Func<CancellationToken, Task<IReadOnlyList<Port>>> ports, AgentOptions options, Redactor redactor, TimeProvider time)
    {
        this.client = client;
        this.directory = directory;
        this.ports = ports;
        this.options = options;
        this.redactor = redactor;
        alerts = OpenAlerts.Create(client, time, options, redactor);
    }

    public static NetworkOverview Create(ILibreNmsClient client, TimeProvider time, bool serverTimestampsAreUtc)
        => new(client, new DeviceDirectory(client, time), ct => client.Ports.ListAllStatusAsync(ct),
            new AgentOptions { ServerTimestampsAreUtc = serverTimestampsAreUtc }, new Redactor(), time);

    public async Task<NetworkOverviewResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var alertTask = ReadSource(ct => client.Alerts.ListAsync(AlertQuery.Open, ct), cancellationToken);
        var portTask = ReadSource(ports, cancellationToken);
        var systemTask = ReadSource(ct => client.System.GetAsync(ct), cancellationToken);
        await Task.WhenAll(devicesTask, alertTask, portTask, systemTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var devices = devicesTask.Result;
        var byId = devices.ToDictionary(d => d.DeviceId);
        var down = devices.Where(d => DeviceSummary.StatusOf(d) == "down").OrderBy(d => d.DeviceId).ToList();
        var locations = down.GroupBy(d => DeviceSummary.LocationName(d.Location) ?? "(no location)", StringComparer.OrdinalIgnoreCase)
            .Select(g => new DownLocation(redactor.Redact(g.Key), g.Count()))
            .OrderByDescending(g => g.Down).ThenBy(g => g.Location, StringComparer.Ordinal).ToList();
        var groups = alertTask.Result.Result is { } listed
            ? AlertGrouping.ByDeviceAndRule(listed.Where(a => a.State != AlertState.Recovered)).ToList() : null;
        var livePorts = portTask.Result.Result?.Where(p => !p.Deleted && !p.Disabled && !p.Ignore).ToList();
        var unavailable = new List<OverviewUnavailable>();
        foreach (var (source, failure) in new[] { ("alerts", alertTask.Result.Failure), ("ports", portTask.Result.Failure), ("system", systemTask.Result.Failure) })
            if (failure is { } code) unavailable.Add(new(source, code));
        return new(new(devices.Count, devices.Count(d => DeviceSummary.StatusOf(d) == "up"), down.Count,
                devices.Count(d => d.Disabled), devices.Count(d => d.Ignore)),
            groups is null ? null : OpenAlertTotals.From(groups),
            livePorts is null ? null : new(livePorts.Count,
                livePorts.Count(p => string.Equals(p.IfAdminStatus, "up", StringComparison.OrdinalIgnoreCase) && !p.IsUp),
                livePorts.Count(p => (p.IfInErrorsRate ?? 0) + (p.IfOutErrorsRate ?? 0) > 0)),
            systemTask.Result.Result?.LocalVersion is { } version ? redactor.Redact(version) : null,
            locations.Take(LocationLimit).ToList(), Math.Max(0, locations.Count - LocationLimit),
            down.Take(DetailLimit).Select(d => DeviceSummary.Redacted(d, options, redactor)).ToList(), Math.Max(0, down.Count - DetailLimit),
            groups?.Take(DetailLimit).Select(g => alerts.Shape(g, byId)).ToList() ?? [], Math.Max(0, (groups?.Count ?? 0) - DetailLimit), unavailable);
    }

    private static Task<(T? Result, HostFailure? Failure)> ReadSource<T>(Func<CancellationToken, Task<T>> read, CancellationToken token) where T : class
        => HostBoundary.RunAsync(read, token, CancellationToken.None);
}
