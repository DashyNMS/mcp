using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Health;
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

    [McpServerTool(Name = ToolText.DeviceSensors.Name, Title = ToolText.DeviceSensors.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.DeviceSensors.Description)]
    public Task<string> DeviceSensors(
        [Description(ToolText.DeviceSensors.Args.Device)] string device,
        [Description(ToolText.DeviceSensors.Args.Type)] string? type = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.DeviceSensors.Name, ct => DeviceAsync(device, type, ct), cancellationToken);

    [McpServerTool(Name = ToolText.HealthOverview.Name, Title = ToolText.HealthOverview.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.HealthOverview.Description)]
    public Task<string> HealthOverview(
        [Description(ToolText.HealthOverview.Args.Type)] string? type = null,
        [Description(ToolText.HealthOverview.Args.OnlyProblems)] bool only_problems = true,
        [Description(ToolText.HealthOverview.Args.Group)] string? group = null,
        [Description(ToolText.HealthOverview.Args.Location)] string? location = null,
        [Description(ToolText.HealthOverview.Args.Offset)] int offset = 0,
        [Description(ToolText.HealthOverview.Args.Limit)] int limit = DefaultReadings,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.HealthOverview.Name, ct => OverviewAsync(type, only_problems, group, location, offset, limit, ct), cancellationToken);

    /// <summary>
    /// One device's readings from the shared typed operation hosts such as mobile also run, so
    /// the assessment, problem-first order and device-by-device fallback are one implementation.
    /// </summary>
    private async Task<JsonObject> DeviceAsync(string query, string? typeText, CancellationToken cancellationToken)
    {
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await new DeviceSensors(sensorSource, directory, runner.Options, runner.Redactor)
            .ReadAsync(new DeviceSensorsRequest(device.DeviceId, typeText), cancellationToken)
            .ConfigureAwait(false);
        var readings = result.Readings;

        var shown = ToolResult.Take(readings, MaxReadings, out var omitted);
        var data = new
        {
            device = DeviceBrief.From(device),
            types = result.Types.Select(t => new { type = t.Type, count = t.Count }).ToList(),
            readings = shown,
        };

        var type = SensorReading.ParseType(typeText);
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
        var summary = $"{all.Count} {what} checked on devices visible to this account: {critical} critical, {warning} warning" +
            (onlyProblems ? "." : $", {all.Count - critical - warning} other.") +
            (deniedDevices > 0 ? $" {DeviceTools.Count(deniedDevices, "device")} couldn't be read: this account isn't allowed to see their sensors, so they aren't counted." : string.Empty) +
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
