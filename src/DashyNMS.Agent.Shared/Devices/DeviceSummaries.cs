using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>Typed arguments for one device's summary.</summary>
/// <param name="DeviceId">
/// The LibreNMS device id. Hosts resolve names themselves (MCP's resolver, mobile's device search),
/// so the operation never guesses which device a phrase meant.
/// </param>
public sealed record DeviceSummaryRequest(int DeviceId);

/// <summary>A device's LibreNMS location, with the two names a host needs kept apart.</summary>
/// <param name="Id">The location's id, for opening or filtering by it.</param>
/// <param name="Name">The name, trimmed and redacted: the one to show or give a model.</param>
/// <param name="FilterName">
/// LibreNMS's exact name, neither trimmed nor redacted, because a host's device filter matches on
/// it and a changed name would match nothing. For host-side filters only: never show it or give it
/// to a model.
/// </param>
public sealed record DeviceLocation(int Id, string Name, string FilterName);

/// <summary>One device now: who it is, its reported state, uptime and location.</summary>
/// <param name="Device">Redacted, with <see cref="DeviceSummary.State"/> set from the device itself.</param>
/// <param name="UptimeSeconds">Null when LibreNMS reports none or zero, so a missing value never reads as "just rebooted".</param>
/// <param name="Location">Null when the device has no location, or the location has no id or no name.</param>
public sealed record DeviceSummaryResult(DeviceSummary Device, long? UptimeSeconds, DeviceLocation? Location);

/// <summary>
/// One device's summary from <c>devices/{id}</c>, without MCP invocation or JSON. The device part
/// of <c>device_health</c> and the whole of mobile's <c>device_summary</c> are built on it, so both
/// read, redact and shape a device the same way.
/// </summary>
/// <remarks>
/// This executor does not log. Propagated exceptions may contain sensitive messages or details;
/// hosts without MCP's reviewed error policy use <see cref="HostDeviceSummary"/>, which maps them to
/// fixed safe categories. It holds no cache, so one instance per captured connection is enough; the
/// caller owns transport lifetime and cancellation.
/// </remarks>
public sealed class DeviceSummaries
{
    private readonly ILibreNmsClient client;
    private readonly AgentOptions options;
    private readonly Redactor redactor;

    private DeviceSummaries(ILibreNmsClient client, AgentOptions options, Redactor redactor)
    {
        this.client = client;
        this.options = options;
        this.redactor = redactor;
    }

    public static DeviceSummaries Create(ILibreNmsClient client, AgentOptions options, Redactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        return new(client, options, redactor ?? new Redactor());
    }

    /// <summary>
    /// Reads and shapes the device. A device the server doesn't return, or returns under another id,
    /// is not found: the summary never falls back to the device list or to another device's record.
    /// </summary>
    public async Task<DeviceSummaryResult> ReadAsync(DeviceSummaryRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ToolException("A device summary request is required.");
        }

        if (request.DeviceId <= 0)
        {
            throw new ToolException("A device summary needs a positive LibreNMS device id.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var device = await FetchAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        return device is not null && device.DeviceId == request.DeviceId ? Shape(device) : throw new DeviceNotFoundException();
    }

    /// <summary>
    /// The one read: <c>devices/{id}</c>. Internal so <c>device_health</c>, which tolerates a failed
    /// read by falling back to the device list it resolved from, can share it without the not-found rule.
    /// </summary>
    internal Task<Device?> FetchAsync(int deviceId, CancellationToken cancellationToken)
        => client.Devices.GetAsync(deviceId.ToString(CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>Shapes a device read from <c>devices/{id}</c> or the device list.</summary>
    public DeviceSummaryResult Shape(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new(DeviceSummary.Redacted(device, options, redactor),
            device.ReportedUptime is long uptime && uptime > 0 ? uptime : null,
            LocationOf(device));
    }

    /// <summary>
    /// The device list gives the location as its name, but <c>devices/{id}</c> can give the whole
    /// record as JSON (<c>{"id": 29, "location": "...", "lat": ...}</c>), which Core keeps as text.
    /// The id comes from <see cref="Device.LocationId"/>, or from the record when that is missing.
    /// </summary>
    private DeviceLocation? LocationOf(Device device)
    {
        var (exact, recordId) = ExactLocation(device.Location);
        var id = device.LocationId is int listed && listed > 0 ? listed : recordId;
        return id is int known && known > 0 && exact is not null && DeviceSummary.LocationName(device.Location) is { } name
            ? new DeviceLocation(known, redactor.Redact(name), exact)
            : null;
    }

    /// <summary>The location's name exactly as LibreNMS holds it, and the record's id when it is a record.</summary>
    private static (string? Name, int? Id) ExactLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return (null, null);
        }

        if (!location.TrimStart().StartsWith('{'))
        {
            return (location, null);
        }

        try
        {
            using var record = JsonDocument.Parse(location);
            var root = record.RootElement;
            var name = root.TryGetProperty("location", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()
                : null;
            int? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var parsed) ? parsed : null;
            return (name, id);
        }
        catch (JsonException)
        {
            // Not a record after all; DeviceSummary.LocationName keeps such text as the name too.
            return (location, null);
        }
    }
}

/// <summary>
/// The server returned no device for the id. Its own type so the host boundary can report it as
/// <see cref="HostFailure.NotFound"/>; it carries no server text.
/// </summary>
internal sealed class DeviceNotFoundException() : Exception("The device was not found.");
