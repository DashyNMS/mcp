using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Sensors;

namespace DashyNMS.Agent.Health;

/// <summary>Typed arguments for one device's sensor readings.</summary>
/// <param name="DeviceId">
/// The LibreNMS device. Hosts resolve names themselves (MCP's resolver, mobile's device search),
/// so the operation never guesses which device a phrase meant.
/// </param>
/// <param name="Type">
/// Only this sensor class (LibreNMS's own names, or fan, optics, temp and the like); null, empty,
/// "all" or "any" for every class. See <see cref="SensorReading.ParseType"/>.
/// </param>
public sealed record DeviceSensorsRequest(int DeviceId, string? Type = null);

/// <summary>Counts over every reading that matched, not only those a host shows.</summary>
/// <param name="Unknown">
/// Readings with no state: no reading, nothing to judge it by, or a "nothing plugged in" value.
/// Never counted as ok.
/// </param>
public sealed record SensorTotals(int Readings, int Critical, int Warning, int Ok, int Unknown);

/// <summary>How many readings of one sensor class matched.</summary>
public sealed record SensorTypeCount(string Type, int Count);

/// <summary>One device's readings, judged and in the order a problem list wants.</summary>
/// <param name="Device">The device as the device list has it, redacted.</param>
/// <param name="Readings">
/// Every matching reading: critical, then warning, then ok, then unknown; within each by class,
/// description and sensor ID. Hosts take as many as fit and count the rest from <paramref name="Totals"/>.
/// </param>
/// <param name="Types">Each matching class and its count, by class name.</param>
/// <param name="DeviceByDevice">
/// True when LibreNMS refused this account its network-wide sensor list, so the readings were
/// read through the device's own health routes instead. They are the same readings; a host may
/// say so when the route matters to the person (it is slower, and some servers limit it).
/// </param>
public sealed record DeviceSensorsResult(
    DeviceSummary Device,
    IReadOnlyList<SensorReading> Readings,
    SensorTotals Totals,
    IReadOnlyList<SensorTypeCount> Types,
    bool DeviceByDevice);

/// <summary>
/// One device's sensor readings, judged the way desktop's Health tab and MCP's
/// <c>device_sensors</c> judge them (<see cref="SensorReading.From"/>), without MCP invocation or
/// JSON. The network-wide list is used when the account may read it, otherwise the device's own
/// health routes (<see cref="SensorSource"/>).
/// </summary>
/// <remarks>
/// This executor does not log. Propagated exceptions may contain sensitive messages or details;
/// hosts without MCP's reviewed error policy use <see cref="HostDeviceSensors"/>, which maps them to
/// fixed safe categories. Create one per captured connection; the caller owns transport lifetime
/// and cancellation.
/// </remarks>
public sealed class DeviceSensors
{
    private readonly SensorSource source;
    private readonly DeviceDirectory directory;
    private readonly AgentOptions options;
    private readonly Redactor redactor;

    /// <summary>Reuses MCP's connection-bound caches; other hosts use <see cref="Create"/>.</summary>
    internal DeviceSensors(SensorSource source, DeviceDirectory directory, AgentOptions options, Redactor redactor)
    {
        this.source = source;
        this.directory = directory;
        this.options = options;
        this.redactor = redactor;
    }

    /// <summary>
    /// Creates isolated caches around a host-owned client and transport, both bound to the same
    /// connection, without registering transports. Definitions, when given, should be a read-only
    /// store (as MCP's is), so a question never downloads a catalogue.
    /// </summary>
    public static DeviceSensors Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time, AgentOptions options,
        SensorDefinitionStore? definitions = null, Redactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        // No fleet cache here: one host instance answers one device at a time, and a fresh list
        // keeps readings current. The refusal of the network-wide list is still remembered.
        var source = new SensorSource(ct => client.Sensors.ListAsync(ct), transport, time, definitions, directory);
        return new(source, directory, options, redactor ?? new Redactor());
    }

    public async Task<DeviceSensorsResult> ReadAsync(DeviceSensorsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId <= 0)
        {
            throw new ToolException("A device ID must be a positive number.");
        }

        var type = SensorReading.ParseType(request.Type);
        var device = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new ToolException("No device with that ID is visible to this account.");
        var (sensors, deviceByDevice) = await source.ReadForDeviceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var readings = Order(sensors
                .Where(s => s.DeviceId == device.DeviceId)
                .Where(s => type is null || s.HasClass(type))
                .Select(SensorReading.From))
            // The class is filtered and judged before redaction; only what a host is given is redacted.
            .Select(r => r with { Type = redactor.Redact(r.Type), Description = Clean(r.Description), StateLabel = Clean(r.StateLabel) })
            .ToList();

        return new(
            DeviceSummary.Redacted(device, options, redactor),
            readings,
            Totals(readings),
            readings.GroupBy(r => r.Type).Select(g => new SensorTypeCount(g.Key, g.Count())).OrderBy(t => t.Type, StringComparer.Ordinal).ToList(),
            deviceByDevice);
    }

    /// <summary>
    /// Problems first, the order MCP's device_sensors has always used. The sensor ID breaks ties,
    /// so the network-wide list and the device-by-device route give the same order.
    /// </summary>
    internal static IEnumerable<SensorReading> Order(IEnumerable<SensorReading> readings) => readings
        .OrderBy(r => r.Rank)
        .ThenBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.SensorId);

    internal static SensorTotals Totals(IReadOnlyCollection<SensorReading> readings) => new(
        readings.Count,
        readings.Count(r => r.State == "critical"),
        readings.Count(r => r.State == "warning"),
        readings.Count(r => r.State == "ok"),
        readings.Count(r => r.State is not ("critical" or "warning" or "ok")));

    private string? Clean(string? text) => text is null ? null : redactor.Redact(text);
}
