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
        var result = await new HealthOverview(sensorSource, directory, scopes, runner.Options, runner.Redactor)
            .ReadAsync(new(typeText, onlyProblems, group, location, offset, limit), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
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
