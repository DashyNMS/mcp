using System.Globalization;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Sensors;

namespace DashyNMS.Agent.Devices;

/// <summary>
/// Sensor readings for one device or a handful, whether or not the token may
/// read LibreNMS's network-wide sensor list.
/// </summary>
/// <remarks>
/// <para>
/// /resources/sensors returns every sensor in one call, but LibreNMS only
/// gives it to users with global read access: an ordinary user's token, even
/// one that can see every device, gets a 403. Desktop's Health tab reads the
/// same list, so it has the same gap.
/// </para>
/// <para>
/// The per-device route works for any token that can see the device:
/// /devices/{id}/health lists its sensor types, each type lists its sensor
/// IDs, and each ID gives the full sensor row. That's one request per sensor,
/// so it's fine for a device or a small group and wrong for a whole network.
/// Once the network-wide list is refused it isn't asked for again for an
/// hour, so every call doesn't pay for a 403 first. This belongs in Core
/// alongside DeviceHealthApi, which reads processors, memory and storage the
/// same way; until it's there, it's built on the transport here.
/// </para>
/// </remarks>
public sealed class SensorSource(FleetCache fleet, ILibreNmsTransport transport, TimeProvider time,
    SensorDefinitionStore? definitions = null, DeviceDirectory? devices = null)
{
    public const int MaxConcurrency = 6;

    /// <summary>Above this many devices, reading them one by one is too slow and too heavy to do.</summary>
    public const int MaxDevicesOneByOne = 25;

    public static readonly TimeSpan ForbiddenRetry = TimeSpan.FromHours(1);

    /// <summary>The health types that aren't sensors: Core's DeviceHealthApi already covers these, and device_health uses them.</summary>
    private static readonly HashSet<string> NotSensors = new(StringComparer.OrdinalIgnoreCase) { "device_processor", "device_mempool", "device_storage" };

    private DateTimeOffset? _fleetForbiddenAt;

    /// <summary>True once LibreNMS has refused the network-wide list, until it's worth asking again.</summary>
    public bool FleetForbidden => _fleetForbiddenAt is { } at && time.GetUtcNow() - at < ForbiddenRetry;

    /// <summary>Every sensor in the network, or null when the token isn't allowed the network-wide list.</summary>
    public async Task<IReadOnlyList<Sensor>?> TryGetFleetAsync(CancellationToken cancellationToken)
    {
        if (FleetForbidden)
        {
            return null;
        }

        try
        {
            var sensors = await fleet.GetSensorsAsync(cancellationToken).ConfigureAwait(false);
            await ApplyDefinitionsAsync(sensors, cancellationToken).ConfigureAwait(false);
            return sensors;
        }
        catch (LibreNmsApiException ex) when (ex.IsPermissionDenied)
        {
            _fleetForbiddenAt = time.GetUtcNow();
            return null;
        }
    }

    /// <summary>One device's sensors, from the network-wide list when allowed, otherwise read device by device.</summary>
    public async Task<IReadOnlyList<Sensor>> GetForDeviceAsync(int deviceId, CancellationToken cancellationToken)
    {
        if (await TryGetFleetAsync(cancellationToken).ConfigureAwait(false) is { } all)
        {
            return all.Where(s => s.DeviceId == deviceId).ToList();
        }

        var sensors = await ReadDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await ApplyDefinitionsAsync(sensors, cancellationToken).ConfigureAwait(false);
        return sensors;
    }

    /// <summary>
    /// Several devices' sensors read one by one - only for a few devices; see
    /// <see cref="MaxDevicesOneByOne"/>. A device this account may not read
    /// (LibreNMS 26.10 checks per device, #79) is skipped and counted, rather
    /// than failing the rest.
    /// </summary>
    public async Task<SensorRead> GetForDevicesAsync(IReadOnlyCollection<int> deviceIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var lists = await Task.WhenAll(deviceIds.Select(async id =>
        {
            try
            {
                return (Sensors: await ReadDeviceAsync(id, cancellationToken).ConfigureAwait(false), Denied: false);
            }
            catch (LibreNmsApiException ex) when (ex.IsPermissionDenied)
            {
                return (Sensors: (IReadOnlyList<Sensor>)Array.Empty<Sensor>(), Denied: true);
            }
        })).ConfigureAwait(false);

        var sensors = lists.SelectMany(l => l.Sensors).ToList();
        await ApplyDefinitionsAsync(sensors, cancellationToken).ConfigureAwait(false);
        return new SensorRead(sensors, lists.Count(l => l.Denied));
    }

    private async Task ApplyDefinitionsAsync(IReadOnlyList<Sensor> sensors, CancellationToken token)
    {
        if (definitions is null || devices is null) return;
        var known = (await devices.GetAllAsync(token).ConfigureAwait(false)).GroupBy(device => device.DeviceId)
            .ToDictionary(group => group.Key, group => group.First());
        await definitions.ApplyWithDevicesAsync(sensors, id => known.GetValueOrDefault(id), token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Sensor>> ReadDeviceAsync(int deviceId, CancellationToken cancellationToken)
    {
        var baseUrl = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/health";
        var types = await transport.GetCollectionAsync<HealthType>(baseUrl, "graphs", cancellationToken).ConfigureAwait(false);

        using var gate = new SemaphoreSlim(MaxConcurrency);
        async Task<T> Gated<T>(Func<Task<T>> call)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await call().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        var refs = await Task.WhenAll(types
            .Where(t => t.Name is { } name && name.StartsWith("device_", StringComparison.OrdinalIgnoreCase) && !NotSensors.Contains(name))
            .Select(t => Gated(async () => (Type: t.Name!, Sensors: await transport.GetCollectionAsync<SensorRef>(baseUrl + "/" + t.Name, "graphs", cancellationToken).ConfigureAwait(false)))))
            .ConfigureAwait(false);

        var details = await Task.WhenAll(refs
            .SelectMany(r => r.Sensors.Select(s => (r.Type, s.SensorId)))
            .Select(r => Gated(async () =>
            {
                var rows = await transport.GetCollectionAsync<Sensor>(
                    baseUrl + "/" + r.Type + "/" + r.SensorId.ToString(CultureInfo.InvariantCulture), "graphs", cancellationToken).ConfigureAwait(false);
                return rows.Count > 0 ? rows[0] : null;
            })))
            .ConfigureAwait(false);

        return details.Where(s => s is not null).Select(s => s!).ToList();
    }

    private sealed class HealthType
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class SensorRef
    {
        [JsonPropertyName("sensor_id")]
        public int SensorId { get; set; }
    }
}

/// <summary>Sensors read device by device, and how many devices this account wasn't allowed to read.</summary>
public sealed record SensorRead(IReadOnlyList<Sensor> Sensors, int DeniedDevices);
