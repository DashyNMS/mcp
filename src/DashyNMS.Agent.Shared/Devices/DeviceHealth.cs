using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Alerts;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

public sealed record DeviceHealthRequest(int DeviceId);
public sealed record StorageUsage(string? Description, double UsagePercent);
public sealed record DeviceHealthData
{
    public required DeviceSummary Device { get; init; }
    public long? UptimeSeconds { get; init; }
    public bool? UnderMaintenance { get; init; }
    public required IReadOnlyList<AlertGroupSummary> OpenAlerts { get; init; }
    public int? OpenFaults { get; init; }
    public double? CpuMaxPercent { get; init; }
    public double? MemoryMaxPercent { get; init; }
    public IReadOnlyList<StorageUsage>? FullestStorage { get; init; }
    public int PortsTotal { get; init; }
    public int PortsDownWhileEnabled { get; init; }
    public required IReadOnlyList<PortProblem> ProblemPorts { get; init; }
    public int? ProblemPortsOmitted { get; init; }
    public IReadOnlyList<Unavailable>? Unavailable { get; init; }
}
public sealed record DeviceHealthResult(string Summary, DeviceHealthData Data, DeviceLocation? Location, IReadOnlyList<OpenAlertGroup> AlertGroups);

/// <summary>Shared device health with the same independent sources and fallback as MCP.</summary>
/// <remarks>Use HostDeviceHealth for fixed failures. The caller owns the client and cancellation.</remarks>
public sealed class DeviceHealth
{
    public const int MaxProblemPorts = 5;
    public const int MaxStorage = 3;
    private readonly ILibreNmsClient client;
    private readonly DeviceDirectory directory;
    private readonly AgentOptions options;
    private readonly Redactor redactor;

    internal DeviceHealth(ILibreNmsClient client, DeviceDirectory directory, AgentOptions options, Redactor redactor)
    {
        this.client = client;
        this.directory = directory;
        this.options = options;
        this.redactor = redactor;
    }

    public static DeviceHealth Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        return new(client, new DeviceDirectory(client, time), options, new Redactor());
    }

    public async Task<DeviceHealthResult> ReadAsync(DeviceHealthRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be a positive number.");
        var listed = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new DeviceNotFoundException();
        return await ReadAsync(listed, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DeviceHealthResult> ReadAsync(Device listed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = listed.DeviceId;
        // The device part is the shared summary, so MCP and in-process hosts read, redact and
        // shape a device the same way. It holds no cache, so one per call costs nothing.
        var summaries = DeviceSummaries.Create(client, options, redactor);

        var fresh = Sources.TryAsync(() => summaries.FetchAsync(id, cancellationToken), cancellationToken);
        var maintenance = Sources.TryAsync(() => client.Devices.IsUnderMaintenanceAsync(id, cancellationToken), cancellationToken);
        var alerts = Sources.TryAsync(() => client.Alerts.ListAsync(AlertQuery.Open, cancellationToken), cancellationToken);
        var processors = Sources.TryAsync(() => client.Health.ListProcessorsAsync(id, cancellationToken), cancellationToken);
        var memory = Sources.TryAsync(() => client.Health.ListMempoolsAsync(id, cancellationToken), cancellationToken);
        var storage = Sources.TryAsync(() => client.Health.ListStorageAsync(id, cancellationToken), cancellationToken);
        var ports = Sources.TryAsync(() => client.Ports.ListForDeviceAsync(id, cancellationToken), cancellationToken);
        await Task.WhenAll(fresh, maintenance, alerts, processors, memory, storage, ports).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var unavailable = new List<Unavailable>();
        T? Use<T>(Fetched<T> fetched, string source)
        {
            if (fetched.Error is { } error)
            {
                unavailable.Add(new Unavailable(source, error));
            }

            return fetched.Value;
        }

        // A failed read still answers from the device list the device was resolved from.
        var device = Use(fresh.Result, "device") ?? listed;
        var shaped = summaries.Shape(device);
        // One per rule firing on the device, as active_alerts reports them (#77).
        var deviceGroups = AlertGrouping.ByDeviceAndRule((Use(alerts.Result, "alerts") ?? []).Where(a => a.DeviceId == id));
        var openAlerts = deviceGroups.Select(g => CleanAlert(AlertGroupSummary.From(g, options))).ToList();
        var alertTotals = OpenAlertTotals.From(deviceGroups);

        var portList = (Use(ports.Result, "ports") ?? []).Where(p => !p.Deleted && !p.Disabled && !p.Ignore).ToList();
        var downPorts = portList.Where(IsDownWhileEnabled).ToList();
        var problemPorts = portList
            .Where(p => IsDownWhileEnabled(p) || ErrorRate(p) > 0)
            .OrderByDescending(ErrorRate)
            .ThenBy(p => p.IfName, StringComparer.OrdinalIgnoreCase)
            .Select(p => { var row = PortProblem.From(p); return row with { IfName = Clean(row.IfName), Alias = Clean(row.Alias), OperStatus = Clean(row.OperStatus) }; });
        var topProblems = TakeProblems(problemPorts, out var otherProblems);

        var status = DeviceSummary.StatusOf(device);
        var data = new DeviceHealthData
        {
            Device = shaped.Device,
            UptimeSeconds = shaped.UptimeSeconds,
            UnderMaintenance = Use(maintenance.Result, "maintenance"),
            OpenAlerts = openAlerts,
            OpenFaults = alertTotals.Faults > alertTotals.AlertGroups ? alertTotals.Faults : (int?)null,
            CpuMaxPercent = Max(Use(processors.Result, "processors")?.Select(p => p.UsagePercent)),
            MemoryMaxPercent = Max(Use(memory.Result, "memory")?.Select(m => m.UsagePercent)),
            FullestStorage = Use(storage.Result, "storage")?
                .Where(s => s.UsagePercent is not null)
                .OrderByDescending(s => s.UsagePercent)
                .Take(MaxStorage)
                .Select(s => new StorageUsage(Clean(s.Description), Math.Round(s.UsagePercent!.Value, 1, MidpointRounding.AwayFromZero)))
                .ToList(),
            PortsTotal = portList.Count,
            PortsDownWhileEnabled = downPorts.Count,
            ProblemPorts = topProblems,
            ProblemPortsOmitted = otherProblems > 0 ? otherProblems : (int?)null,
            Unavailable = unavailable.Count > 0 ? unavailable : null,
        };

        // An alert list that couldn't be read is unknown, never "0 open alerts" (#79).
        var alertsText = alerts.Result.Error is null ? Count(openAlerts.Count, "open alert") : "open alerts unknown";
        var summary = $"Device is {status}, {alertsText}, {Count(downPorts.Count, "port")} down while enabled" +
            (unavailable.Count > 0 ? $"; {Count(unavailable.Count, "part")} couldn't be fetched." : ".");

        var hostAlerts = OpenAlerts.Create(client, TimeProvider.System, options, redactor);
        var knownDevices = new Dictionary<int, Device> { [id] = device };
        return new DeviceHealthResult(summary, data, shaped.Location, deviceGroups.Select(g => hostAlerts.Shape(g, knownDevices)).ToList());
    }

    public static bool IsDownWhileEnabled(Port port)
        => string.Equals(port.IfAdminStatus, "up", StringComparison.OrdinalIgnoreCase) && !port.IsUp;

    public static double ErrorRate(Port port) => (port.IfInErrorsRate ?? 0) + (port.IfOutErrorsRate ?? 0);

    private static double? Max(IEnumerable<double?>? values)
        => values?.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max() is { } max && !double.IsNaN(max)
            ? Math.Round(max, 1, MidpointRounding.AwayFromZero)
            : null;

    public static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static IReadOnlyList<PortProblem> TakeProblems(IEnumerable<PortProblem> rows, out int omitted)
    {
        var all = rows.ToList();
        omitted = Math.Max(0, all.Count - MaxProblemPorts);
        return all.Take(MaxProblemPorts).ToList();
    }

    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
    private AlertGroupSummary CleanAlert(AlertGroupSummary row) => row with
    {
        Rule = redactor.Redact(row.Rule), Entity = Clean(row.Entity), Note = Clean(row.Note),
        Faults = row.Faults?.Select(f => f with { Entity = Clean(f.Entity), EntityType = Clean(f.EntityType) }).ToList(),
    };
}
