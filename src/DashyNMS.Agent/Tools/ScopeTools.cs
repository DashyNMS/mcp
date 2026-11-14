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
        var result = await new GroupsAndLocations(directory, scopes, runner.Redactor).ReadAsync(cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }
}
