using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>The names the group and location filters accept (#34).</summary>
[McpServerToolType]
public sealed class ScopeTools(DeviceDirectory directory, DeviceScope scopes, ToolRunner runner)
{
    [McpServerTool(Name = ToolText.ListGroupsAndLocations.Name, Title = ToolText.ListGroupsAndLocations.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.ListGroupsAndLocations.Description)]
    public Task<string> ListGroupsAndLocations(CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.ListGroupsAndLocations.Name, ListAsync, cancellationToken);

    private async Task<JsonObject> ListAsync(CancellationToken cancellationToken)
    {
        var devicesTask = directory.GetAllAsync(cancellationToken);
        var groupsTask = Sources.TryAsync(() => scopes.ListGroupsAsync(cancellationToken), cancellationToken);
        await Task.WhenAll(devicesTask, groupsTask).ConfigureAwait(false);

        var devices = devicesTask.Result;
        var locations = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.Location))
            .GroupBy(d => d.Location!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                location_id = g.Select(d => d.LocationId).Distinct().ToArray() is [int id] && id > 0 ? (int?)id : null,
                name = g.Key,
                devices = g.Count(),
                down = g.Count(d => DeviceSummary.StatusOf(d) == "down"),
            })
            .OrderBy(l => l.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = groupsTask.Result.Value?
            .Select(g => new
            {
                group_id = g.Id,
                name = g.Name,
                description = string.IsNullOrWhiteSpace(g.Description) ? null : g.Description,
                type = g.Type,
            })
            .OrderBy(g => g.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var withoutLocation = devices.Count(d => string.IsNullOrWhiteSpace(d.Location));
        var data = new
        {
            groups,
            locations,
            devices_without_location = withoutLocation > 0 ? withoutLocation : (int?)null,
            unavailable = groupsTask.Result.Error is { } error ? new[] { new Unavailable("device groups", error) } : null,
        };

        var groupPart = groups is null ? "device groups couldn't be read" : DeviceTools.Count(groups.Count, "device group");
        return ToolResult.Ok($"{groupPart[..1].ToUpperInvariant()}{groupPart[1..]} and {DeviceTools.Count(locations.Count, "location")}.", data);
    }
}
