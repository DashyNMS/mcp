using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>Sensor readings for one device or the whole network, as desktop's Health tab (#24).</summary>
[McpServerToolType]
public sealed class HealthTools(
    SensorSource sensorSource,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    ToolRunner runner)
{
    public const int DefaultReadings = 50;
    public const int MaxReadings = 100;

    private const string TypeHelp =
        "Types are LibreNMS's sensor classes - temperature, fanspeed, dbm (optical power), signal, voltage, current, power, humidity and others - " +
        "and fan, optics and temp work too.";

    [McpServerTool(Name = "device_sensors", Title = "Device sensors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "One device's sensor readings: temperatures, fan speeds, optical power in dBm, signal, voltages, currents, power and more. Each reading has its " +
        "unit, its limits and a state (ok, warning, critical, or unknown when there's nothing to judge it by), judged the way DashyNMS's Health tab judges " +
        "it: the sensor's own LibreNMS limits where it has them, sensible defaults for dBm, signal, temperature and fans where it doesn't. Problems first. " +
        TypeHelp + " For the same across the whole network, use health_overview.")]
    public Task<string> DeviceSensors(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("Only this type of sensor, e.g. temperature, fanspeed, dbm or voltage. Leave out for all.")] string? type = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("device_sensors", ct => DeviceAsync(device, type, ct), cancellationToken);

    [McpServerTool(Name = "health_overview", Title = "Health overview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Sensor readings across the whole network, worst first, for questions like \"is anything running hot?\", \"which optics are low?\" or " +
        "\"any fans failing?\". By default only readings in warning or critical; set only_problems to false for every reading of a type. " +
        "Judged the same way as device_sensors. " + TypeHelp + " Filter by device group or location. Paged: 50 by default, up to 100.")]
    public Task<string> HealthOverview(
        [Description("Only this type of sensor, e.g. temperature, fanspeed, dbm or voltage. Leave out for every type.")] string? type = null,
        [Description("Only readings in warning or critical (the default). False lists every reading, which needs a type.")] bool only_problems = true,
        [Description("Only devices in this LibreNMS device group (by name; list_groups_and_locations shows them).")] string? group = null,
        [Description("Only devices at this location (by name; list_groups_and_locations shows them).")] string? location = null,
        [Description("How many readings to skip, for paging. Start at 0, then use page.next_offset.")] int offset = 0,
        [Description("How many readings to return, 1 to 100. Defaults to 50.")] int limit = DefaultReadings,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("health_overview", ct => OverviewAsync(type, only_problems, group, location, offset, limit, ct), cancellationToken);

    private async Task<JsonObject> DeviceAsync(string query, string? typeText, CancellationToken cancellationToken)
    {
        var type = SensorReading.ParseType(typeText);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var sensors = await sensorSource.GetForDeviceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);

        var readings = sensors
            .Where(s => s.DeviceId == device.DeviceId)
            .Where(s => type is null || s.HasClass(type))
            .Select(SensorReading.From)
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shown = ToolResult.Take(readings, MaxReadings, out var omitted);
        var data = new
        {
            device = DeviceBrief.From(device),
            types = readings.GroupBy(r => r.Type).Select(g => new { type = g.Key, count = g.Count() }).OrderBy(t => t.type).ToList(),
            readings = shown,
        };

        return ToolResult.Ok(Sentence(readings, readings.Count == 0 ? (type is null ? "No sensors on this device." : "No sensors of that type on this device.") : null), data, omitted);
    }

    private async Task<JsonObject> OverviewAsync(string? typeText, bool onlyProblems, string? group, string? location, int offset, int limit, CancellationToken cancellationToken)
    {
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
        if (sensors is null)
        {
            if (scope.IsEverything || inScope.Count > SensorSource.MaxDevicesOneByOne)
            {
                throw new ToolException(
                    $"LibreNMS only gives the network-wide sensor list to users with global read access, which this token's user doesn't have. " +
                    $"Narrow it with group or location to {SensorSource.MaxDevicesOneByOne} devices or fewer (this covers {inScope.Count}), " +
                    "or use device_sensors for one device.");
            }

            sensors = await sensorSource.GetForDevicesAsync(inScope, cancellationToken).ConfigureAwait(false);
        }

        var all = sensors
            .Where(s => type is null || s.HasClass(type))
            .Where(s => devices.TryGetValue(s.DeviceId, out var d) && scope.Includes(d) && !d.Disabled)
            .Select(s => (Sensor: s, Reading: SensorReading.From(s)))
            .ToList();

        var listed = all
            .Where(x => !onlyProblems || x.Reading.State is "warning" or "critical")
            .OrderBy(x => x.Reading.Rank)
            .ThenBy(x => x.Reading.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => devices[x.Sensor.DeviceId].Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { device = DeviceBrief.From(devices[x.Sensor.DeviceId]), reading = x.Reading })
            .ToList();

        var page = Paging.Apply(listed, offset, limit, out var info);
        var critical = all.Count(x => x.Reading.State == "critical");
        var warning = all.Count(x => x.Reading.State == "warning");
        var what = type is null ? "sensor readings" : $"{type} readings";
        var summary = $"{all.Count} {what} checked: {critical} critical, {warning} warning" +
            (onlyProblems ? "." : $", {all.Count - critical - warning} other.") +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "reading", "readings") : string.Empty);

        var data = new
        {
            types = all.GroupBy(x => x.Reading.Type).Select(g => new
            {
                type = g.Key,
                count = g.Count(),
                critical = g.Count(x => x.Reading.State == "critical"),
                warning = g.Count(x => x.Reading.State == "warning"),
            }).OrderBy(t => t.type).ToList(),
            readings = page,
            page = info,
        };

        return ToolResult.Ok(summary, data);
    }

    private static string Sentence(IReadOnlyList<SensorReading> readings, string? empty)
    {
        if (empty is not null)
        {
            return empty;
        }

        var critical = readings.Count(r => r.State == "critical");
        var warning = readings.Count(r => r.State == "warning");
        return $"{DeviceTools.Count(readings.Count, "sensor")}: {critical} critical, {warning} warning.";
    }
}
