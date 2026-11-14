using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Sensors;

namespace DashyNMS.Agent.Health;

public sealed record HealthOverviewRequest(string? Type = null, bool OnlyProblems = true, string? Group = null,
    string? Location = null, int Offset = 0, int Limit = 50);
public sealed record HealthOverviewRow(DeviceBrief Device, SensorReading Reading);
public sealed record HealthTypeTotals(string Type, int Count, int Critical, int Warning);
public sealed record HealthOverviewData(IReadOnlyList<HealthTypeTotals> Types, IReadOnlyList<HealthOverviewRow> Readings, PageInfo Page);
public sealed record HealthOverviewResult(string Summary, HealthOverviewData Data, SensorTotals Totals, int DeniedDevices);

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

    internal HealthOverview(SensorSource sensorSource, DeviceDirectory directory, DeviceScope scopes, AgentOptions options, Redactor redactor)
    {
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
        // by device, which only makes sense for a few devices (see SensorSource).
        var sensors = await sensorSource.TryGetFleetAsync(cancellationToken).ConfigureAwait(false);
        var deniedDevices = 0;
        if (sensors is null)
        {
            if (scope.IsEverything || inScope.Count > SensorSource.MaxDevicesOneByOne)
            {
                throw new ToolException(
                    $"LibreNMS only gives the network-wide sensor list to users with global read access, which this token's user doesn't have. " +
                    $"Narrow it with group or location to {SensorSource.MaxDevicesOneByOne} devices or fewer (this covers {inScope.Count}), " +
                    "or use device_sensors for one device.");
            }

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
            (deniedDevices > 0 ? $" {DeviceHealth.Count(deniedDevices, "device")} couldn't be read: this account isn't allowed to see their sensors, so they aren't counted." : string.Empty) +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "reading", "readings") : string.Empty);

        var data = new HealthOverviewData(all.GroupBy(x => x.Reading.Type).OrderBy(g => g.Key)
            .Select(g => new HealthTypeTotals(redactor.Redact(g.Key), g.Count(), g.Count(x => x.Reading.State == "critical"), g.Count(x => x.Reading.State == "warning"))).ToList(), page, info);
        return new(redactor.Redact(summary), data, DeviceSensors.Totals(all.Select(x => x.Reading).ToList()), deniedDevices);
    }

    private SensorReading CleanReading(SensorReading reading) => reading with
    {
        Type = redactor.Redact(reading.Type), Description = reading.Description is { } description ? redactor.Redact(description) : null,
        StateLabel = reading.StateLabel is { } label ? redactor.Redact(label) : null,
    };
}
