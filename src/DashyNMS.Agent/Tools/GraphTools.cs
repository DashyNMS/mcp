using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// LibreNMS's graphs as images the client shows inline (#33) - desktop's
/// device and port graphs, for "show me the traffic on X this week".
/// </summary>
/// <remarks>
/// <para>
/// Core's graph calls return SVG, which desktop themes and draws itself, but
/// MCP clients only show raster images. LibreNMS renders PNG instead when
/// asked with <c>graph_type=png</c>, and <c>output=base64</c> wraps it in its
/// usual JSON envelope (checked live), so the image comes through Core's
/// transport - its certificate pinning and retries included - as text.
/// </para>
/// <para>
/// The image isn't redacted: it's pixels, though its legend shows port
/// names. Everything said alongside it is, like any other result.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class GraphTools(
    ILibreNmsClient client,
    ILibreNmsTransport transport,
    SensorSource sensors,
    DeviceResolver resolver,
    ToolRunner runner,
    TimeProvider time)
{
    public const int Width = 800;
    public const int Height = 250;

    /// <summary>The graph types listed with a result, at most - a big chassis has well over a hundred health graphs.</summary>
    public const int MaxTypesListed = 80;

    /// <summary>The port graphs LibreNMS draws for any port, by the short name a model is likely to use.</summary>
    public static readonly IReadOnlyDictionary<string, (string Type, string Description)> PortTypes = DashyNMS.Agent.Graphs.GraphLookup.PortTypes;

    /// <summary>
    /// RRDtool's PNG is transparent by default, with black text and axes, so
    /// on a dark chat theme only the coloured lines show. <c>bg</c> fills the
    /// plot area and <c>bbg</c> the border around it (both needed; checked live).
    /// </summary>
    internal const string WhiteBackground = "bg=FFFFFF&bbg=FFFFFF";

    [McpServerTool(Name = ToolText.Graph.Name, Title = ToolText.Graph.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.Graph.Description)]
    public async Task<CallToolResult> Graph(
        [Description(ToolText.Graph.Args.Device)] string device,
        [Description(ToolText.Graph.Args.Type)] string? type = null,
        [Description(ToolText.Graph.Args.Port)] string? port = null,
        [Description(ToolText.Graph.Args.Sensor)] string? sensor = null,
        [Description(ToolText.Graph.Args.Range)] string? range = null,
        [Description(ToolText.Graph.Args.Since)] string? since = null,
        [Description(ToolText.Graph.Args.Until)] string? until = null,
        [Description(ToolText.Graph.Args.Legend)] bool legend = true,
        CancellationToken cancellationToken = default)
    {
        byte[]? image = null;
        var text = await runner.RunAsync(
            ToolText.Graph.Name,
            async ct =>
            {
                var (result, png) = await GraphAsync(device, type, port, sensor, range, since, until, legend, ct).ConfigureAwait(false);
                image = png;
                return result;
            },
            cancellationToken).ConfigureAwait(false);

        var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
        if (image is not null)
        {
            content.Add(ImageContentBlock.FromBytes(image, "image/png"));
        }

        return new CallToolResult { Content = content };
    }

    private async Task<(JsonObject Result, byte[]? Png)> GraphAsync(string deviceQuery, string? typeText, string? portQuery,
        string? sensorQuery, string? rangeText, string? since, string? until, bool legend, CancellationToken cancellationToken)
    {
        var operation = new DashyNMS.Agent.Graphs.GraphLookup(client, transport, sensors, new DeviceDirectory(client, time), time, runner.Redactor);
        var span = operation.Span(rangeText, since, until);
        var device = await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false);
        var result = await operation.ReadAsync(device, span, typeText, portQuery, sensorQuery, legend, cancellationToken).ConfigureAwait(false);
        return (ToolResult.Ok(result.Summary, result.Data, result.Omitted), result.Image?.Data);
    }

    internal static (string Type, string Description) PortType(string? text) => DashyNMS.Agent.Graphs.GraphLookup.PortType(text);
}
