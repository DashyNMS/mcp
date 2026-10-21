using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public static readonly IReadOnlyDictionary<string, (string Type, string Description)> PortTypes = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["bits"] = ("port_bits", "Traffic"),
        ["upkts"] = ("port_upkts", "Unicast packets"),
        ["nupkts"] = ("port_nupkts", "Broadcast and multicast packets"),
        ["errors"] = ("port_errors", "Errors"),
    };

    /// <summary>Everyday words for the commonest device graphs.</summary>
    private static readonly Dictionary<string, string> DeviceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["traffic"] = "device_bits",
        ["bandwidth"] = "device_bits",
        ["cpu"] = "device_processor",
        ["processor"] = "device_processor",
        ["memory"] = "device_mempool",
        ["ram"] = "device_mempool",
        ["ping"] = "device_icmp_perf",
        ["latency"] = "device_icmp_perf",
        ["temperature"] = "device_temperature",
        ["storage"] = "device_storage",
        ["disk"] = "device_storage",
    };

    private static readonly Dictionary<string, string> PortAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["traffic"] = "bits",
        ["bandwidth"] = "bits",
        ["packets"] = "upkts",
        ["broadcast"] = "nupkts",
        ["multicast"] = "nupkts",
    };

    private const string DefaultDeviceGraph = "device_bits";

    /// <summary>
    /// RRDtool's PNG is transparent by default, with black text and axes, so
    /// on a dark chat theme only the coloured lines show. <c>bg</c> fills the
    /// plot area and <c>bbg</c> the border around it (both needed; checked live).
    /// </summary>
    internal const string WhiteBackground = "bg=FFFFFF&bbg=FFFFFF";

    [McpServerTool(Name = "graph", Title = "Graph", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "A LibreNMS graph as an image, shown in the chat: a device's graph (traffic, processor, memory, ping, temperature and the rest of its health graphs) " +
        "or one port's (traffic, packets, errors). Use it when a picture of a trend helps: \"show me the traffic on X this week\". " +
        "Without type, a device gives its traffic graph and the list of graph types it has; a port gives its traffic. " +
        "With sensor, one sensor alone on its own scale (one temperature, one optic's power) rather than every sensor of its kind together; device_sensors lists them with their IDs. " +
        "The time range is range (hour, day, week, month or year; a day by default), or since and until for a custom span up to 31 days. " +
        "The legend lists every line with its figures; turn it off (legend: false) for graphs with many lines, where it would dwarf the graph. " +
        "The image has its own white background with dark text: show it as it is, on a light background, and never recolour it or place it on a dark one, where it can't be read. " +
        "For numbers rather than a picture, use port_status, device_health or device_sensors.")]
    public async Task<CallToolResult> Graph(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("The graph type. For a device: a name from its list (device_bits, device_processor, device_mempool, device_icmp_perf, ...) or traffic, cpu, memory, ping, temperature, storage. For a port: bits (traffic), upkts, nupkts or errors. Leave out for traffic.")] string? type = null,
        [Description("A port on the device, for that port's graph: its name (ifName), description, alias or port ID. Leave out for a device graph.")] string? port = null,
        [Description("One sensor on the device, for that sensor's own graph: its sensor_id from device_sensors, or its description (e.g. \"Chassis Temperature\"). Type, if given, narrows it to that kind of sensor. Leave out for a device or port graph.")] string? sensor = null,
        [Description("How far back: hour, day, week, month or year. Defaults to day. Ignored when since is given.")] string? range = null,
        [Description("Start of a custom span: relative (\"6h\", \"3d\") or ISO 8601 (UTC unless a zone is given). Up to 31 days before until.")] string? since = null,
        [Description("End of a custom span, the same forms as since. Defaults to now.")] string? until = null,
        [Description("Draw the legend under the graph (each line's current, average and maximum). Defaults to true. " +
            "Turn it off for a graph with many lines - a processor graph with a line per core, a device's traffic across dozens of ports - where the legend would be taller than the graph; " +
            "keep it when the figures matter to the question.")] bool legend = true,
        CancellationToken cancellationToken = default)
    {
        byte[]? image = null;
        var text = await runner.RunAsync(
            "graph",
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

    private async Task<(JsonObject Result, byte[]? Png)> GraphAsync(
        string deviceQuery, string? typeText, string? portQuery, string? sensorQuery, string? rangeText, string? since, string? until, bool legend, CancellationToken cancellationToken)
    {
        var span = Span(rangeText, since, until);
        var device = await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false);
        var id = device.DeviceId;
        var query = Query(span, legend);

        if (!string.IsNullOrWhiteSpace(sensorQuery))
        {
            if (!string.IsNullOrWhiteSpace(portQuery))
            {
                throw new ToolException("Give a port or a sensor, not both.");
            }

            var all = await sensors.GetForDeviceAsync(id, cancellationToken).ConfigureAwait(false);
            var target = ResolveSensor(all, sensorQuery.Trim(), SensorClassOf(typeText));
            var graphName = "device_" + target.SensorClass;
            var png = await FetchAsync(
                $"devices/{id}/graphs/health/{Uri.EscapeDataString(graphName)}/{target.SensorId.ToString(CultureInfo.InvariantCulture)}?{query}",
                cancellationToken).ConfigureAwait(false);
            return (ToolResult.Ok(
                $"Graph of one {target.SensorClass} sensor over {span.Text}, shown as an image.",
                new
                {
                    device = DeviceBrief.From(device),
                    sensor = new { sensor_id = target.SensorId, description = target.Description, @class = target.SensorClass, current = target.Current },
                    graph = new { type = graphName, span.From, span.To, span.Preset },
                }), png);
        }

        if (!string.IsNullOrWhiteSpace(portQuery))
        {
            var ports = await client.Ports.ListForDeviceAsync(id, cancellationToken).ConfigureAwait(false);
            var target = PortTools.Resolve(ports.Where(p => !p.Deleted).ToList(), portQuery.Trim());
            var ifName = target.IfName ?? target.IfDescr;
            if (string.IsNullOrEmpty(ifName))
            {
                throw new ToolException("LibreNMS has no name for this port, and draws port graphs by name.");
            }

            var (portType, description) = PortType(typeText);
            var png = await FetchAsync($"devices/{id}/ports/{Uri.EscapeDataString(ifName)}/{portType}?{query}", cancellationToken).ConfigureAwait(false);
            return (ToolResult.Ok(
                $"{description} graph for one port over {span.Text}, shown as an image.",
                new
                {
                    device = DeviceBrief.From(device),
                    port = new { port_id = target.PortId, if_name = target.IfName, alias = target.IfAlias },
                    graph = new { type = portType, description, span.From, span.To, span.Preset },
                    other_types = PortTypes.Keys.ToList(),
                }), png);
        }

        var available = await ListTypesAsync(id, cancellationToken).ConfigureAwait(false);
        var listed = available.Take(MaxTypesListed).Select(g => new { name = g.Name, description = g.Description }).ToList();
        var omitted = Math.Max(0, available.Count - MaxTypesListed);

        GraphType? chosen;
        if (string.IsNullOrWhiteSpace(typeText))
        {
            chosen = available.FirstOrDefault(g => g.Name == DefaultDeviceGraph);
            if (chosen is null)
            {
                return (ToolResult.Ok(
                    available.Count == 0
                        ? "LibreNMS has no graphs for this device."
                        : $"This device has no traffic graph; it has {DeviceTools.Count(available.Count, "graph type")}. Ask again with one of them as type.",
                    new { device = DeviceBrief.From(device), available_types = listed },
                    omitted), null);
            }
        }
        else
        {
            chosen = Find(available, typeText.Trim())
                ?? throw new ToolException(
                    "This device has no graph of that type. Use one of the types listed under details.",
                    new { available_types = listed, omitted = omitted > 0 ? omitted : (int?)null });
        }

        var image = await FetchAsync($"devices/{id}/{Uri.EscapeDataString(chosen.Name)}?{query}", cancellationToken).ConfigureAwait(false);
        var name = string.IsNullOrWhiteSpace(chosen.Description) ? chosen.Name : chosen.Description;
        return (ToolResult.Ok(
            $"{name} graph for the device over {span.Text}, shown as an image.",
            new
            {
                device = DeviceBrief.From(device),
                graph = new { type = chosen.Name, description = chosen.Description, span.From, span.To, span.Preset },
                available_types = string.IsNullOrWhiteSpace(typeText) ? listed : null,
            },
            string.IsNullOrWhiteSpace(typeText) ? omitted : 0), image);
    }

    /// <summary>
    /// Every graph the device has, from LibreNMS's three listings: the
    /// device-wide graphs, the health graphs and the wireless ones (Core
    /// keeps them apart because LibreNMS does). A listing that fails costs
    /// only its own types.
    /// </summary>
    private async Task<IReadOnlyList<GraphType>> ListTypesAsync(int id, CancellationToken cancellationToken)
    {
        var lists = await Task.WhenAll(
            Sources.TryAsync(() => client.Graphs.ListAsync(id, cancellationToken), cancellationToken),
            Sources.TryAsync(() => client.Graphs.ListHealthAsync(id, cancellationToken), cancellationToken),
            Sources.TryAsync(() => client.Graphs.ListWirelessAsync(id, cancellationToken), cancellationToken)).ConfigureAwait(false);

        if (lists.All(l => !l.Ok))
        {
            throw new ToolException($"LibreNMS wouldn't list this device's graphs: {lists[0].Error}");
        }

        return lists
            .SelectMany(l => l.Value ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An exact name, an everyday word, or a name without its "device_" prefix.</summary>
    internal static GraphType? Find(IReadOnlyList<GraphType> available, string text)
    {
        var wanted = DeviceAliases.GetValueOrDefault(text, text);
        return available.FirstOrDefault(g => string.Equals(g.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(g => string.Equals(g.Name, "device_" + wanted, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(g => string.Equals(g.Description, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The sensor class a type names - "device_temperature" or "temperature" both give "temperature" - or null for no type.</summary>
    internal static string? SensorClassOf(string? typeText)
    {
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return null;
        }

        var text = typeText.Trim();
        return text.StartsWith("device_", StringComparison.OrdinalIgnoreCase) ? text["device_".Length..] : text;
    }

    /// <summary>
    /// Sensor ID, then an exact description, then part of one - the first
    /// tier with a single match wins, as ports are matched. Only sensors of
    /// <paramref name="sensorClass"/> count when it's given, so "Chassis"
    /// with type temperature skips the chassis fan.
    /// </summary>
    internal static Sensor ResolveSensor(IReadOnlyList<Sensor> all, string text, string? sensorClass)
    {
        var sensors = all
            .Where(s => !string.IsNullOrWhiteSpace(s.SensorClass))
            .Where(s => sensorClass is null || string.Equals(s.SensorClass, sensorClass, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var sensorId) && sensors.FirstOrDefault(s => s.SensorId == sensorId) is { } byId)
        {
            return byId;
        }

        Func<Sensor, bool>[] tiers =
        [
            s => string.Equals(s.Description?.Trim(), text, StringComparison.OrdinalIgnoreCase),
            s => s.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) == true,
        ];

        foreach (var tier in tiers)
        {
            var matches = sensors.Where(tier).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new ToolException(
                    $"{matches.Count} sensors match. Ask which one is meant, or use its sensor_id.",
                    new
                    {
                        candidates = matches.Take(PortTools.MaxCandidates).Select(s => new { sensor_id = s.SensorId, description = s.Description, @class = s.SensorClass }).ToList(),
                        omitted = Math.Max(0, matches.Count - PortTools.MaxCandidates),
                    });
            }
        }

        throw new ToolException(sensorClass is null
            ? "No sensor on this device matches. Use device_sensors to list them with their IDs."
            : $"No {sensorClass} sensor on this device matches. Use device_sensors to list them with their IDs.");
    }

    internal static (string Type, string Description) PortType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PortTypes["bits"];
        }

        var key = text.Trim();
        if (key.StartsWith("port_", StringComparison.OrdinalIgnoreCase))
        {
            key = key["port_".Length..];
        }

        key = PortAliases.GetValueOrDefault(key, key);
        return PortTypes.TryGetValue(key, out var type)
            ? type
            : throw new ToolException($"Port graphs come in these types: {string.Join(", ", PortTypes.Keys)}.");
    }

    /// <summary>
    /// Smaller than this, a PNG may be LibreNMS's error image rather than a
    /// graph - a real one is tens of kilobytes - so it's checked (#79).
    /// </summary>
    internal const int SmallImageBytes = 2048;

    private async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var document = await transport.SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);

        // LibreNMS answers a graph this account may not see (26.10 checks per
        // device and port) with HTTP 200 and an image reading "No Authorization",
        // drawn in the server's own format - not a 403.
        byte[] png;
        try
        {
            png = Decode(document.RootElement);
        }
        catch (ToolException) when (GraphImages.IsDenied(ImageText(document.RootElement)))
        {
            throw Denied();
        }

        if (png.Length < SmallImageBytes && await IsDeniedAsync(url, cancellationToken).ConfigureAwait(false))
        {
            throw Denied();
        }

        return png;
    }

    /// <summary>Asks again for the SVG, whose error image carries its text, to tell a denied graph from a small one.</summary>
    private async Task<bool> IsDeniedAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var svg = await transport.SendAsync(HttpMethod.Get, url.Replace("graph_type=png", "graph_type=svg", StringComparison.Ordinal), null, cancellationToken).ConfigureAwait(false);
            return GraphImages.IsDenied(ImageText(svg.RootElement));
        }
        catch (LibreNmsApiException)
        {
            return false;
        }
    }

    /// <summary>The image inside LibreNMS's base64 envelope, as text - for an SVG error image; null when there's none.</summary>
    internal static string? ImageText(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object
            || !image.TryGetProperty("image", out var data) || data.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ToolException Denied()
        => new("LibreNMS: this account isn't allowed to see this graph. LibreNMS 26.10 checks graph permissions per device and port.");

    /// <summary>The PNG out of LibreNMS's <c>{"image":{"image":"base64…","content-type":"image/png"}}</c>.</summary>
    internal static byte[] Decode(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object
            || !image.TryGetProperty("image", out var data) || data.ValueKind != JsonValueKind.String)
        {
            throw new ToolException("LibreNMS answered without a graph image.");
        }

        var contentType = image.TryGetProperty("content-type", out var ct) ? ct.GetString() : null;
        if (contentType is not null && !string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException($"LibreNMS drew the graph as {contentType} rather than PNG, which chat clients can't show. It may be too old to honour graph_type=png.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data.GetString()!);
        }
        catch (FormatException)
        {
            throw new ToolException("LibreNMS sent a graph image that couldn't be read.");
        }

        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        if (!bytes.AsSpan().StartsWith(signature))
        {
            throw new ToolException("LibreNMS sent a graph image that isn't a PNG.");
        }

        return bytes;
    }

    /// <param name="Preset">hour, day, week, month or year; null for a custom span.</param>
    /// <param name="Text">"the last day", or the custom span in words, for the summary.</param>
    internal sealed record GraphSpan(GraphTimeRange Range, string? Preset, DateTime? From, DateTime? To, string Text);

    internal GraphSpan Span(string? rangeText, string? since, string? until)
    {
        if (!string.IsNullOrWhiteSpace(since))
        {
            var window = TimeWindow.Resolve(since, until, null, 24, time);
            return new GraphSpan(GraphTimeRange.Custom(window.Since, window.Until), null, window.Since, window.Until,
                string.Create(CultureInfo.InvariantCulture, $"{window.Since:yyyy-MM-dd HH:mm} to {window.Until:yyyy-MM-dd HH:mm} UTC"));
        }

        if (!string.IsNullOrWhiteSpace(until))
        {
            throw new ToolException("Until needs since with it. For the most recent span, use range instead.");
        }

        var preset = (rangeText ?? "day").Trim().ToLowerInvariant() switch
        {
            "hour" or "1h" => GraphTimeRangePreset.Hour,
            "day" or "24h" or "1d" or "today" => GraphTimeRangePreset.Day,
            "week" or "7d" or "1w" => GraphTimeRangePreset.Week,
            "month" or "30d" => GraphTimeRangePreset.Month,
            "year" or "1y" => GraphTimeRangePreset.Year,
            _ => throw new ToolException("Range must be hour, day, week, month or year. For another span, use since and until."),
        };

        var name = preset.ToString().ToLowerInvariant();
        return new GraphSpan(new GraphTimeRange(preset), name, null, null, $"the last {name}");
    }

    /// <summary>The query string, as Core builds it for desktop, plus the two parameters that make LibreNMS send a PNG inside JSON.</summary>
    private static string Query(GraphSpan span, bool legend)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"from={Uri.EscapeDataString(span.Range.ToFromParameter())}&width={Width}&height={Height}");
        if (span.Range.ToToParameter() is { } to)
        {
            query += "&to=" + Uri.EscapeDataString(to);
        }

        if (!legend)
        {
            query += "&legend=no";
        }

        return query + "&graph_type=png&output=base64&" + WhiteBackground;
    }
}
