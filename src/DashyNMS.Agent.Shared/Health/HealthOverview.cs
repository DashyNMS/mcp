using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Sensors;

namespace DashyNMS.Agent.Health;

public sealed record HealthOverviewRequest(string? Type = null, bool OnlyProblems = true, string? Group = null,
    string? Location = null, int Offset = 0, int Limit = 50);
public sealed record HealthOverviewRow(DeviceBrief Device, SensorReading Reading);
public sealed record HealthTypeTotals(string Type, int Count, int Critical, int Warning);
public sealed record HealthOverviewData(IReadOnlyList<HealthTypeTotals> Types, IReadOnlyList<HealthOverviewRow> Readings, PageInfo Page)
{
    /// <summary>Which devices the answer covers, when they were read one by one over a limited time (#71); null when it covers them all.</summary>
    public SensorCoverage? Coverage { get; init; }
}

/// <summary>Which devices a sensor sweep covered (#71). Every device in scope is in exactly one count.</summary>
/// <param name="Down">Not read: their last readings are from before they went down.</param>
/// <param name="Denied">This account isn't allowed to see their sensors.</param>
/// <param name="Failed">LibreNMS couldn't answer for them.</param>
/// <param name="NotReached">There wasn't time for them.</param>
/// <param name="Complete">Every device that's up was read.</param>
/// <param name="OldestReadAt">When the oldest of these readings was read from LibreNMS.</param>
/// <param name="ReadEverything">The setting that lets one call cover everything, when it's incomplete.</param>
public sealed record SensorCoverage(int Devices, int Read, int Down, int Denied, int Failed, int NotReached, bool Complete,
    DateTime? OldestReadAt, string? ReadEverything);
public sealed record HealthOverviewResult(string Summary, HealthOverviewData Data, SensorTotals Totals, int DeniedDevices);

/// <summary>Global sensor access was refused and the requested scope cannot use the bounded fallback.</summary>
public sealed class FleetSensorsRefusedException : Exception
{
    /// <summary>The one setting that answers for a whole network at once. LibreNMS's policy for the list: global read, or sensor.view in a role.</summary>
    internal const string Permission =
        "To read it all at once, a LibreNMS admin can give the token's user the global read role, or on versions with custom roles one with the sensor.view permission.";

    public FleetSensorsRefusedException(int devices) : base(
        "LibreNMS didn't give this token the network-wide sensor list. " + Permission + " " +
        $"Or narrow it with group or location to {SensorSource.MaxDevicesOneByOne} devices or fewer (this covers {devices}), " +
        "or use device_sensors for one device.") { }
}

/// <summary>Network sensor assessment, bounded fallback, filtering and paging shared with MCP.</summary>
public sealed class HealthOverview
{
    public const int MaxReadings = 100;
    private readonly SensorSource sensorSource;
    private readonly DeviceDirectory directory;
    private readonly DeviceScope scopes;
    private readonly AgentOptions options;
    private readonly Redactor redactor;
    private readonly AppSettings settings;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Alert>>>? openAlerts;

    /// <param name="openAlerts">What's alerting, to read those devices first when there may not be time for every one.</param>
    internal HealthOverview(SensorSource sensorSource, DeviceDirectory directory, DeviceScope scopes, AgentOptions options, Redactor redactor,
        Func<CancellationToken, Task<IReadOnlyList<Alert>>>? openAlerts = null)
    {
        this.openAlerts = openAlerts;
        this.sensorSource = sensorSource;
        this.directory = directory;
        this.scopes = scopes;
        this.options = options;
        this.redactor = redactor;
        settings = options.SensorSettings();
    }

    public static HealthOverview Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time,
        AgentOptions options, SensorDefinitionStore? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        var fleet = new FleetCache(client, time);
        return new(new SensorSource(fleet.GetSensorsAsync, transport, time, definitions, directory), directory,
            new DeviceScope(client, directory, time), options, new Redactor());
    }

    public async Task<HealthOverviewResult> ReadAsync(HealthOverviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (typeText, onlyProblems, group, location, offset, limit) = request;
        var type = SensorReading.ParseType(typeText);
        if (!onlyProblems && type is null)
        {
            throw new ToolException("Listing every reading needs a type (temperature, dbm and so on); leave only_problems on to see problems of every type.");
        }

        Paging.Check(offset, limit, MaxReadings);
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);
        var inScope = devices.Values.Where(d => scope.Includes(d) && !d.Disabled).Select(d => d.DeviceId).ToList();

        // The network-wide list when the token may read it; otherwise device
        // by device: a few devices in full, and more than that for as long as
        // the host allows, saying how far it got (see SensorSource, #71).
        var sensors = await sensorSource.TryGetFleetAsync(cancellationToken).ConfigureAwait(false);
        var deniedDevices = 0;
        SensorCoverage? coverage = null;
        if (sensors is null && (scope.IsEverything || inScope.Count > SensorSource.MaxDevicesOneByOne))
        {
            if (options.SensorSweepBudget is not { } budget)
            {
                throw new FleetSensorsRefusedException(inScope.Count);
            }

            (sensors, coverage) = await SweepAsync(inScope.Select(id => devices[id]).ToList(), type, budget, cancellationToken).ConfigureAwait(false);
        }

        if (sensors is null)
        {
            var read = await sensorSource.GetForDevicesAsync(inScope, cancellationToken).ConfigureAwait(false);
            sensors = read.Sensors;
            deniedDevices = read.DeniedDevices;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var all = sensors
            .Where(s => type is null || s.HasClass(type))
            .Where(s => devices.TryGetValue(s.DeviceId, out var d) && scope.Includes(d) && !d.Disabled)
            .Select(s => (Sensor: s, Reading: SensorReading.From(s, settings)))
            .ToList();

        var listed = all
            .Where(x => !onlyProblems || x.Reading.State is "warning" or "critical")
            .OrderBy(x => x.Reading.Rank)
            .ThenBy(x => x.Reading.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => devices[x.Sensor.DeviceId].Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(x => new HealthOverviewRow(DeviceBrief.FromSummary(DeviceSummary.Redacted(devices[x.Sensor.DeviceId], options, redactor)), CleanReading(x.Reading)))
            .ToList();

        var page = Paging.Apply(listed, offset, limit, out var info);
        var critical = all.Count(x => x.Reading.State == "critical");
        var warning = all.Count(x => x.Reading.State == "warning");
        var what = type is null ? "sensor readings" : $"{type} readings";
        var summary = $"{all.Count} {what} checked on devices visible to this account: {critical} critical, {warning} warning" +
            (onlyProblems ? "." : $", {all.Count - critical - warning} other.") +
            (coverage is not null ? " " + Describe(coverage) : string.Empty) +
            (deniedDevices > 0 ? $" {DeviceHealth.Count(deniedDevices, "device")} couldn't be read: this account isn't allowed to see their sensors, so they aren't counted." : string.Empty) +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "reading", "readings") : string.Empty);

        var data = new HealthOverviewData(all.GroupBy(x => x.Reading.Type).OrderBy(g => g.Key)
            .Select(g => new HealthTypeTotals(redactor.Redact(g.Key), g.Count(), g.Count(x => x.Reading.State == "critical"), g.Count(x => x.Reading.State == "warning"))).ToList(), page, info)
        {
            Coverage = coverage,
        };
        return new(redactor.Redact(summary), data, DeviceSensors.Totals(all.Select(x => x.Reading).ToList()), deniedDevices);
    }

    /// <summary>
    /// The devices that are up, alerting ones first: what's hot is most likely
    /// already alerting, and may be all there's time for. Half the requests the
    /// host allows, so other questions still get through meanwhile.
    /// </summary>
    private async Task<(IReadOnlyList<Sensor> Sensors, SensorCoverage Coverage)> SweepAsync(IReadOnlyList<Device> inScope, string? type, TimeSpan budget, CancellationToken cancellationToken)
    {
        var up = inScope.Where(d => d.ReportedState != DeviceState.Down).Select(d => d.DeviceId).ToList();
        var open = openAlerts is null ? null : (await Sources.TryAsync(() => openAlerts(cancellationToken), cancellationToken).ConfigureAwait(false)).Value;
        var alerting = open?.Select(a => a.DeviceId).ToHashSet() ?? [];
        var order = up.OrderBy(id => !alerting.Contains(id)).ThenBy(id => id).ToList();

        var swept = await sensorSource.SweepAsync(order, type, Math.Max(1, options.MaxConcurrentRequests / 2), budget, cancellationToken).ConfigureAwait(false);
        var complete = swept.NotReached == 0 && swept.Denied == 0 && swept.Failed == 0;
        if (swept.NotReached > 0 || swept.Failed > 0)
        {
            SourceOutcomes.Current?.Partial();
        }

        return (swept.Sensors, new SensorCoverage(inScope.Count, swept.Read, inScope.Count - up.Count, swept.Denied, swept.Failed, swept.NotReached,
            complete, swept.OldestReadAt?.UtcDateTime, complete ? null : FleetSensorsRefusedException.Permission));
    }

    private static string Describe(SensorCoverage coverage)
    {
        var rest = new List<string>();
        void Add(int count, string what)
        {
            if (count > 0)
            {
                rest.Add($"{count} {what}");
            }
        }

        Add(coverage.Down, "down");
        Add(coverage.Denied, "not allowed for this account");
        Add(coverage.Failed, "LibreNMS couldn't answer for");
        Add(coverage.NotReached, "not reached yet");
        return $"Read device by device: {coverage.Read} of {DeviceHealth.Count(coverage.Devices, "device")}" +
            (rest.Count > 0 ? $" ({string.Join(", ", rest)})." : ".") +
            (coverage.NotReached > 0 ? " Ask again within a few minutes to carry on from here." : string.Empty);
    }

    private SensorReading CleanReading(SensorReading reading) => reading with
    {
        Type = redactor.Redact(reading.Type), Description = reading.Description is { } description ? redactor.Redact(description) : null,
        StateLabel = reading.StateLabel is { } label ? redactor.Redact(label) : null,
    };
}
