using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Ports;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Graphs;

public sealed record GraphLookupRequest(int DeviceId, string? Type = null, string? Port = null, string? Sensor = null,
    string? Range = null, string? Since = null, string? Until = null, bool Legend = true);
public sealed record GraphMetadata(string Type, DateTime? From, DateTime? To, string? Preset, string? Description = null);
public sealed record GraphSensorInfo(int SensorId, string? Description, string? Class, double? Current);
public sealed record GraphPortInfo(int PortId, string? IfName, string? Alias);
public sealed record GraphTypeInfo(string Name, string? Description);
public sealed record GraphLookupData(DeviceBrief Device, GraphMetadata? Graph = null, GraphSensorInfo? Sensor = null,
    GraphPortInfo? Port = null, IReadOnlyList<string>? OtherTypes = null, IReadOnlyList<GraphTypeInfo>? AvailableTypes = null);
public sealed record GraphLookupResult(string Summary, GraphLookupData Data, IntelligenceImage? Image, int Omitted,
    IReadOnlyList<string> Unavailable);
public sealed class GraphDeniedException : Exception
{
    public GraphDeniedException() : base("LibreNMS: this account isn't allowed to see this graph. LibreNMS 26.10 checks graph permissions per device and port.") { }
}

/// <summary>Typed device, port and sensor graphs over the host's own client and transport.</summary>
public sealed class GraphLookup(ILibreNmsClient client, ILibreNmsTransport transport, SensorSource sensors,
    DeviceDirectory directory, TimeProvider time, Redactor redactor)
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


    private sealed record GraphTypeListing(IReadOnlyList<GraphType> Types, IReadOnlyList<string> Unavailable);
    public static GraphLookup Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        var directory = new DeviceDirectory(client, time);
        var fleet = new FleetCache(client, time);
        return new(client, transport, new SensorSource(fleet.GetSensorsAsync, transport, time, devices: directory), directory, time, new Redactor());
    }
    public async Task<GraphLookupResult> ReadAsync(GraphLookupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be positive.");
        var span = Span(request.Range, request.Since, request.Until);
        var device = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new DeviceNotFoundException();
        return await ReadAsync(device, span, request.Type, request.Port, request.Sensor, request.Legend, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<GraphLookupResult> ReadAsync(Device device, GraphSpan span, string? typeText, string? portQuery,
        string? sensorQuery, bool legend, CancellationToken cancellationToken)
    {
        var id = device.DeviceId;
        var query = Query(span, legend);
        var brief = DeviceBrief.FromSummary(DeviceSummary.Redacted(device, new AgentOptions(), redactor));
        if (!string.IsNullOrWhiteSpace(sensorQuery))
        {
            if (!string.IsNullOrWhiteSpace(portQuery)) throw new ToolException("Give a port or a sensor, not both.");
            var all = await sensors.GetForDeviceAsync(id, cancellationToken).ConfigureAwait(false);
            var target = ResolveSensor(all, sensorQuery.Trim(), SensorClassOf(typeText));
            var graphName = "device_" + target.SensorClass;
            var png = await FetchAsync($"devices/{id}/graphs/health/{Uri.EscapeDataString(graphName)}/{target.SensorId.ToString(CultureInfo.InvariantCulture)}?{query}", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(redactor.Redact($"Graph of one {target.SensorClass} sensor over {span.Text}, shown as an image."),
                new GraphLookupData(brief, new GraphMetadata(redactor.Redact(graphName), span.From, span.To, span.Preset),
                    Sensor: new GraphSensorInfo(target.SensorId, Clean(target.Description), Clean(target.SensorClass), target.Current)),
                new IntelligenceImage(png, "image/png"), 0, []);
        }
        if (!string.IsNullOrWhiteSpace(portQuery))
        {
            var ports = await client.Ports.ListForDeviceAsync(id, cancellationToken).ConfigureAwait(false);
            var target = PortStatus.Resolve(ports.Where(p => !p.Deleted).ToList(), portQuery.Trim());
            var ifName = target.IfName ?? target.IfDescr;
            if (string.IsNullOrEmpty(ifName)) throw new ToolException("LibreNMS has no name for this port, and draws port graphs by name.");
            var (portType, description) = PortType(typeText);
            var png = await FetchAsync($"devices/{id}/ports/{Uri.EscapeDataString(ifName)}/{portType}?{query}", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new($"{description} graph for one port over {span.Text}, shown as an image.",
                new GraphLookupData(brief, new GraphMetadata(portType, span.From, span.To, span.Preset, description),
                    Port: new GraphPortInfo(target.PortId, Clean(target.IfName), Clean(target.IfAlias)), OtherTypes: PortTypes.Keys.ToList()),
                new IntelligenceImage(png, "image/png"), 0, []);
        }
        var listing = await ListTypesAsync(id, cancellationToken).ConfigureAwait(false);
        var available = listing.Types;
        var listed = available.Take(MaxTypesListed).Select(g => new GraphTypeInfo(redactor.Redact(g.Name), Clean(g.Description))).ToList();
        var omitted = Math.Max(0, available.Count - MaxTypesListed);
        GraphType? chosen;
        if (string.IsNullOrWhiteSpace(typeText))
        {
            chosen = available.FirstOrDefault(g => g.Name == DefaultDeviceGraph);
            if (chosen is null)
                return new(available.Count == 0 ? "LibreNMS has no graphs for this device." : $"This device has no traffic graph; it has {DeviceHealth.Count(available.Count, "graph type")}. Ask again with one of them as type.",
                    new GraphLookupData(brief, AvailableTypes: listed), null, omitted, listing.Unavailable);
        }
        else
            chosen = Find(available, typeText.Trim()) ?? throw new ToolException("This device has no graph of that type. Use one of the types listed under details.",
                new { available_types = listed, omitted = omitted > 0 ? omitted : (int?)null });
        var image = await FetchAsync($"devices/{id}/{Uri.EscapeDataString(chosen.Name)}?{query}", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var name = string.IsNullOrWhiteSpace(chosen.Description) ? chosen.Name : chosen.Description;
        return new(redactor.Redact($"{name} graph for the device over {span.Text}, shown as an image."),
            new GraphLookupData(brief, new GraphMetadata(redactor.Redact(chosen.Name), span.From, span.To, span.Preset, Clean(chosen.Description)),
                AvailableTypes: string.IsNullOrWhiteSpace(typeText) ? listed : null), new IntelligenceImage(image, "image/png"),
            string.IsNullOrWhiteSpace(typeText) ? omitted : 0, listing.Unavailable);
    }

    /// <summary>
    /// Every graph the device has, from LibreNMS's three listings: the
    /// device-wide graphs, the health graphs and the wireless ones (Core
    /// keeps them apart because LibreNMS does). A listing that fails costs
    /// only its own types.
    /// </summary>
    private async Task<GraphTypeListing> ListTypesAsync(int id, CancellationToken cancellationToken)
    {
        var lists = await Task.WhenAll(
            Sources.TryAsync(() => client.Graphs.ListAsync(id, cancellationToken), cancellationToken),
            Sources.TryAsync(() => client.Graphs.ListHealthAsync(id, cancellationToken), cancellationToken),
            Sources.TryAsync(() => client.Graphs.ListWirelessAsync(id, cancellationToken), cancellationToken)).ConfigureAwait(false);

        if (lists.All(l => !l.Ok))
        {
            throw new ToolException($"LibreNMS wouldn't list this device's graphs: {lists[0].Error}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(lists.SelectMany(l => l.Value ?? []).Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            lists.Select((list, index) => (list, index)).Where(x => !x.list.Ok)
                .Select(x => x.index switch { 0 => "device graphs", 1 => "health graphs", _ => "wireless graphs" }).ToList());
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
                        candidates = matches.Take(PortStatus.MaxCandidates).Select(s => new { sensor_id = s.SensorId, description = s.Description, @class = s.SensorClass }).ToList(),
                        omitted = Math.Max(0, matches.Count - PortStatus.MaxCandidates),
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
            if (data.GetString()!.Length > ((IntelligenceImage.MaxBytes + 2) / 3) * 4) return null;
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static GraphDeniedException Denied() => new();

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
            if (data.GetString()!.Length > ((IntelligenceImage.MaxBytes + 2) / 3) * 4)
                throw new ToolException("LibreNMS drew a graph larger than the supported image limit.");
            bytes = Convert.FromBase64String(data.GetString()!);
            if (bytes.Length > IntelligenceImage.MaxBytes)
                throw new ToolException("LibreNMS drew a graph larger than the supported image limit.");
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
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
}
