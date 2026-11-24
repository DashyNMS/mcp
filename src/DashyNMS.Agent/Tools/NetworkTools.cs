using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Outputs;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// The whole network at once: the dashboard's counts (#25), its Top widgets
/// (#26) and the fleet-wide logs (#29).
/// </summary>
[McpServerToolType]
public sealed class NetworkTools(
    ILibreNmsClient client,
    FleetCache fleet,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int DefaultTop = 10;
    public const int MaxTop = 50;
    public const int DefaultEvents = 50;
    public const int MaxEvents = 100;
    public const int MaxDownLocations = 10;

    [McpServerTool(Name = ToolText.NetworkOverview.Name, Title = ToolText.NetworkOverview.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.NetworkOverview.Description)]
    public Task<string> NetworkOverview(CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.NetworkOverview.Name, OverviewAsync, cancellationToken);

    [McpServerTool(Name = ToolText.TopInterfaces.Name, Title = ToolText.TopInterfaces.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.TopInterfaces.Description)]
    public Task<string> TopInterfaces(
        [Description(ToolText.TopInterfaces.Args.By)] string? by = null,
        [Description(ToolText.TopInterfaces.Args.Limit)] int limit = DefaultTop,
        [Description(ToolText.TopInterfaces.Args.Group)] string? group = null,
        [Description(ToolText.TopInterfaces.Args.Location)] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.TopInterfaces.Name, ct => TopPortsAsync(errors: false, by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = ToolText.TopErrors.Name, Title = ToolText.TopErrors.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.TopErrors.Description)]
    public Task<string> TopErrors(
        [Description(ToolText.TopErrors.Args.By)] string? by = null,
        [Description(ToolText.TopErrors.Args.Limit)] int limit = DefaultTop,
        [Description(ToolText.TopErrors.Args.Group)] string? group = null,
        [Description(ToolText.TopErrors.Args.Location)] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.TopErrors.Name, ct => TopPortsAsync(errors: true, by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = ToolText.TopDevices.Name, Title = ToolText.TopDevices.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.TopDevices.Description)]
    public Task<string> TopDevices(
        [Description(ToolText.TopDevices.Args.By)] string? by = null,
        [Description(ToolText.TopDevices.Args.Limit)] int limit = DefaultTop,
        [Description(ToolText.TopDevices.Args.Group)] string? group = null,
        [Description(ToolText.TopDevices.Args.Location)] string? location = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.TopDevices.Name, ct => TopDevicesAsync(by, limit, group, location, ct), cancellationToken);

    [McpServerTool(Name = ToolText.NetworkEvents.Name, Title = ToolText.NetworkEvents.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.NetworkEvents.Description)]
    public Task<string> NetworkEvents(
        [Description(ToolText.NetworkEvents.Args.Log)] string? log = null,
        [Description(ToolText.NetworkEvents.Args.Hours)] int? hours = null,
        [Description(ToolText.NetworkEvents.Args.Since)] string? since = null,
        [Description(ToolText.NetworkEvents.Args.Until)] string? until = null,
        [Description(ToolText.NetworkEvents.Args.Type)] string? type = null,
        [Description(ToolText.NetworkEvents.Args.Severity)] string? severity = null,
        [Description(ToolText.NetworkEvents.Args.Device)] string? device = null,
        [Description(ToolText.NetworkEvents.Args.Group)] string? group = null,
        [Description(ToolText.NetworkEvents.Args.Location)] string? location = null,
        [Description(ToolText.NetworkEvents.Args.Offset)] int offset = 0,
        [Description(ToolText.NetworkEvents.Args.Limit)] int limit = DefaultEvents,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.NetworkEvents.Name, ct => EventsAsync(log, hours, since, until, type, severity, device, group, location, offset, limit, ct), cancellationToken);

    private async Task<JsonObject> OverviewAsync(CancellationToken cancellationToken)
    {
        var result = await new DashyNMS.Agent.Overview.NetworkOverview(client, directory, fleet.GetPortsAsync,
            options, new DashyNMS.Agent.Redaction.Redactor(), time).ReadAsync(cancellationToken).ConfigureAwait(false);
        var d = result.Devices;
        var a = result.Alerts;
        var p = result.Ports;
        var summary = $"{d.Total} devices visible to this account: {d.Up} up, {d.Down} down" +
            (d.Disabled > 0 ? $", {d.Disabled} disabled" : string.Empty) +
            (d.Ignored > 0 ? $", {d.Ignored} ignored" : string.Empty) +
            // LibreNMS left these devices' state out: they're neither up nor down (#121).
            (d.Unknown > 0 ? $", {d.Unknown} with no reported state" : string.Empty) +
            (a is null ? "; open alerts couldn't be read." : $"; {a.Describe()} ({a.Critical} critical, {a.Warning} warning).");
        return ToolResult.Ok(summary, new NetworkOverviewData(
            d,
            result.DownLocations,
            a is null ? null : new OverviewAlerts(a.AlertGroups, a.Faults, a.Critical, a.Warning, a.Acknowledged, a.PartlyAcknowledged, a.Devices),
            p,
            result.LibreNmsVersion,
            result.Unavailable.Count == 0 ? null : result.Unavailable.Select(u => new OverviewSource(u.Source, "This source is unavailable.")).ToList(),
            result.DownDevices,
            result.AlertGroups,
            result.DownDevicesOmitted,
            result.AlertGroupsOmitted,
            result.DownLocationsOmitted));
    }

    private async Task<JsonObject> TopPortsAsync(bool errors, string? byText, int limit, string? group, string? location, CancellationToken cancellationToken)
    {
        var result = await new DashyNMS.Agent.Rankings.FleetRankings(fleet, directory, scopes, runner.Redactor)
            .ReadPortsAsync(new(errors, byText, limit, group, location), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private async Task<JsonObject> TopDevicesAsync(string? byText, int limit, string? group, string? location, CancellationToken cancellationToken)
    {
        var result = await new DashyNMS.Agent.Rankings.FleetRankings(fleet, directory, scopes, runner.Redactor)
            .ReadDevicesAsync(new(byText, limit, group, location), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private async Task<JsonObject> EventsAsync(
        string? logText, int? hours, string? since, string? until, string? type, string? severityText, string? deviceQuery,
        string? group, string? location, int offset, int limit, CancellationToken cancellationToken)
    {
        var request = new DashyNMS.Agent.Events.NetworkEventsRequest(logText, hours, since, until, type, severityText,
            null, group, location, offset, limit);
        var window = DashyNMS.Agent.Events.NetworkEventHistory.Validate(request, time);
        int? deviceId = string.IsNullOrWhiteSpace(deviceQuery) ? null : (await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false)).DeviceId;
        var result = await new DashyNMS.Agent.Events.NetworkEventHistory(client, fleet, directory, scopes, options, time, runner.Redactor)
            .ReadAsync(request with { DeviceId = deviceId, Hours = null, Since = window.Since.ToString("O", System.Globalization.CultureInfo.InvariantCulture), Until = window.Until.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }
}
