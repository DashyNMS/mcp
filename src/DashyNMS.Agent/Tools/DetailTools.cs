using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// The rest of a device: inventory, VLANs, tables, PoE, wireless, routing,
/// availability, addresses (#23), and its neighbours (#32) - desktop's
/// Device Details sections, in two tools rather than one per section.
/// </summary>
[McpServerToolType]
public sealed class DetailTools(
    ILibreNmsClient client,
    FleetCache fleet,
    SensorSource sensorSource,
    DeviceDirectory directory,
    DeviceResolver resolver,
    ToolRunner runner)
{
    public const int DefaultRows = 50;
    public const int MaxRows = 200;

    public static readonly IReadOnlyList<string> AllSections = ["inventory", "vlans", "fdb", "arp", "poe", "wireless", "routing", "availability", "addresses"];

    /// <summary>The sections given when none are named: everything except the MAC and ARP tables, which can run to thousands of rows.</summary>
    public static readonly IReadOnlyList<string> DefaultSections = ["inventory", "vlans", "poe", "wireless", "routing", "availability", "addresses"];

    [McpServerTool(Name = ToolText.DeviceDetails.Name, Title = ToolText.DeviceDetails.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.DeviceDetails.Description)]
    public Task<string> DeviceDetails(
        [Description(ToolText.DeviceDetails.Args.Device)] string device,
        [Description(ToolText.DeviceDetails.Args.Sections)] string[]? sections = null,
        [Description(ToolText.DeviceDetails.Args.Offset)] int offset = 0,
        [Description(ToolText.DeviceDetails.Args.Limit)] int limit = DefaultRows,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.DeviceDetails.Name, ct => DetailsAsync(device, sections, offset, limit, ct), cancellationToken);

    [McpServerTool(Name = ToolText.DeviceNeighbours.Name, Title = ToolText.DeviceNeighbours.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.DeviceNeighbours.Description)]
    public Task<string> DeviceNeighbours(
        [Description(ToolText.DeviceNeighbours.Args.Device)] string device,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.DeviceNeighbours.Name, ct => NeighboursAsync(device, ct), cancellationToken);

    private async Task<JsonObject> DetailsAsync(string query, string[]? requested, int offset, int limit, CancellationToken cancellationToken)
    {
        Paging.Check(offset, limit, MaxRows);
        var sections = DashyNMS.Agent.Devices.DeviceDetails.ParseSections(requested);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await new DashyNMS.Agent.Devices.DeviceDetails(client, sensorSource, directory, runner.Redactor)
            .ReadAsync(device, sections, offset, limit, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private async Task<JsonObject> NeighboursAsync(string query, CancellationToken cancellationToken)
    {
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await new DashyNMS.Agent.Ports.DeviceNeighbours(client, fleet, directory, runner.Redactor)
            .ReadAsync(device, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data, result.Omitted);
    }

    internal static string? MacText(string? mac) => DashyNMS.Agent.Devices.DeviceDetails.MacText(mac);
}
