using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>One port in detail.</summary>
[McpServerToolType]
public sealed class PortTools(ILibreNmsClient client, DeviceResolver resolver, ToolRunner runner, AgentOptions options)
{
    public const int MaxCandidates = 5;

    [McpServerTool(Name = ToolText.PortStatus.Name, Title = ToolText.PortStatus.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.PortStatus.Description)]
    public Task<string> PortStatus(
        [Description(ToolText.PortStatus.Args.Device)] string device,
        [Description(ToolText.PortStatus.Args.Port)] string port,
        CancellationToken cancellationToken)
        => runner.RunAsync(ToolText.PortStatus.Name, ct => StatusAsync(device, port, ct), cancellationToken);

    private async Task<JsonObject> StatusAsync(string deviceQuery, string portQuery, CancellationToken cancellationToken)
    {
        var text = portQuery?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ToolException("Say which port: its name, description, alias or port ID.");
        }

        var device = await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false);
        var operation = new DashyNMS.Agent.Ports.PortStatus(client, new DeviceDirectory(client, TimeProvider.System), options, runner.Redactor);
        var result = await operation.ReadAsync(device, text, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    internal static Port Resolve(IReadOnlyList<Port> ports, string text) => DashyNMS.Agent.Ports.PortStatus.Resolve(ports, text);
}
