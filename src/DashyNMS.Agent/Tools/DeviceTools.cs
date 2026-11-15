using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>Finding a device, and how it is doing right now.</summary>
[McpServerToolType]
public sealed class DeviceTools(
    ILibreNmsClient client,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    ToolRunner runner,
    AgentOptions options)
{
    public const int DefaultMatches = 10;
    public const int MaxMatches = DeviceSearch.MaxMatches;
    public const int MaxProblemPorts = 5;
    public const int MaxStorage = 3;

    [McpServerTool(Name = ToolText.FindDevice.Name, Title = ToolText.FindDevice.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.FindDevice.Description)]
    public Task<string> FindDevice(
        [Description(ToolText.FindDevice.Args.Query)] string? query = null,
        [Description(ToolText.FindDevice.Args.Status)] string? status = null,
        [Description(ToolText.FindDevice.Args.Group)] string? group = null,
        [Description(ToolText.FindDevice.Args.Location)] string? location = null,
        [Description(ToolText.FindDevice.Args.Offset)] int offset = 0,
        [Description(ToolText.FindDevice.Args.Limit)] int limit = DefaultMatches,
        [Description(ToolText.FindDevice.Args.Verbose)] bool verbose = false,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.FindDevice.Name, ct => FindAsync(query, status, group, location, offset, limit, verbose, ct), cancellationToken);

    [McpServerTool(Name = ToolText.DeviceHealth.Name, Title = ToolText.DeviceHealth.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.DeviceHealth.Description)]
    public Task<string> DeviceHealth(
        [Description(ToolText.DeviceHealth.Args.Device)] string device,
        CancellationToken cancellationToken)
        => runner.RunAsync(ToolText.DeviceHealth.Name, ct => HealthAsync(device, ct), cancellationToken);

    private async Task<JsonObject> FindAsync(string? query, string? status, string? group, string? location, int offset, int limit, bool verbose, CancellationToken cancellationToken)
    {
        var result = await new DeviceSearch(client, directory, scopes, options, runner.Redactor)
            .FindDevicesAsync(new(query, status, group, location, offset, limit), cancellationToken).ConfigureAwait(false);
        object Shape(DeviceSummary d) => verbose ? d : DeviceBrief.FromSummary(d);
        return result.IsMacSearch
            ? ToolResult.Ok(result.Summary, new { arp_matches = result.Matches.Select(m => new { device = Shape(m.Device), seen_as = m.SeenAs }), page = result.Page })
            : ToolResult.Ok(result.Summary, new { devices = result.Matches.Select(m => Shape(m.Device)), page = result.Page });
    }

    private async Task<JsonObject> HealthAsync(string query, CancellationToken cancellationToken)
    {
        var listed = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await new DeviceHealth(client, directory, options, runner.Redactor)
            .ReadAsync(listed, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    internal static bool IsDownWhileEnabled(Port port)
        => string.Equals(port.IfAdminStatus, "up", StringComparison.OrdinalIgnoreCase) && !port.IsUp;

    internal static double ErrorRate(Port port) => (port.IfInErrorsRate ?? 0) + (port.IfOutErrorsRate ?? 0);

    private static double? Max(IEnumerable<double?>? values)
        => values?.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max() is { } max && !double.IsNaN(max)
            ? Math.Round(max, 1, MidpointRounding.AwayFromZero)
            : null;

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

}
