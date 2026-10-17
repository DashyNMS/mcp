using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>The names the group and location filters accept (#34).</summary>
[McpServerToolType]
public sealed class ScopeTools(DeviceDirectory directory, DeviceScope scopes, ToolRunner runner)
{
    [McpServerTool(Name = "list_groups_and_locations", Title = "Device groups and locations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The device groups defined in LibreNMS and the locations devices are at, with how many devices are at each location and how many of those are down. " +
        "Use it to find the names the group and location filters of find_device and active_alerts accept, or for \"which sites are there?\". " +
        "To list a group's devices, call find_device with that group.")]
    public Task<string> ListGroupsAndLocations(CancellationToken cancellationToken = default)
        => runner.RunAsync("list_groups_and_locations", ListAsync, cancellationToken);

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
                name = g.Key,
                devices = g.Count(),
                down = g.Count(d => DeviceSummary.StatusOf(d) == "down"),
            })
            .OrderBy(l => l.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = groupsTask.Result.Value?
            .Select(g => new
            {
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
