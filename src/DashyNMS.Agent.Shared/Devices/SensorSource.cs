using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
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
/// so it's fine for a device or a small group and wrong for a whole network
/// in one go: <see cref="SweepAsync"/> reads one a part at a time, keeping
/// what it has read (#71).
/// Once the network-wide list is refused it isn't asked for again for a
/// while, so every call doesn't pay for a 403 first. This belongs in Core
/// alongside DeviceHealthApi, which reads processors, memory and storage the
/// same way; until it's there, it's built on the transport here.
/// </para>
/// <para>
/// The network-wide list comes through a delegate rather than MCP's fleet cache, so the
/// class can live in the Shared package: MCP passes its cached list, and other hosts
/// (<see cref="Health.HostDeviceSensors"/>) read their own connection-bound client. The
/// transport must be bound to the same connection as that list.
/// </para>
/// </remarks>
/// <param name="fleetSensors">Every sensor in the network, from LibreNMS's /resources/sensors.</param>
public sealed class SensorSource(Func<CancellationToken, Task<IReadOnlyList<Sensor>>> fleetSensors, ILibreNmsTransport transport,
    TimeProvider time, SensorDefinitionStore? definitions = null, DeviceDirectory? devices = null)
{
    public const int MaxConcurrency = 6;

    /// <summary>Above this many devices, reading them one by one is too slow and too heavy to do.</summary>
    public const int MaxDevicesOneByOne = 25;

    /// <summary>Short enough that giving the token's user the permission shows soon after.</summary>
    public static readonly TimeSpan ForbiddenRetry = TimeSpan.FromMinutes(10);

    /// <summary>How long a sweep keeps a device's readings: two of LibreNMS's usual polls, so a long sweep isn't chasing its own tail.</summary>
    public static readonly TimeSpan SweepLifetime = TimeSpan.FromMinutes(10);

    /// <summary>This many devices failing before any has been read is LibreNMS failing, not the devices.</summary>
    private const int FailuresThatEndASweep = 5;

    /// <summary>The health types that aren't sensors: Core's DeviceHealthApi already covers these, and device_health uses them.</summary>
    private static readonly HashSet<string> NotSensors = new(StringComparer.OrdinalIgnoreCase) { "device_processor", "device_mempool", "device_storage" };

    private readonly ConcurrentDictionary<(int DeviceId, string? Type), (IReadOnlyList<Sensor> Sensors, DateTimeOffset ReadAt)> _swept = new();

    /// <summary>Devices the last sweep ran out of time on part way through: they go last next time, so a few huge ones can't hold up the rest for ever.</summary>
    private readonly ConcurrentDictionary<int, byte> _unfinished = new();

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
            var sensors = await fleetSensors(cancellationToken).ConfigureAwait(false);
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
        => (await ReadForDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false)).Sensors;

    /// <summary>
    /// As <see cref="GetForDeviceAsync"/>, and whether the readings came device by device, so a
    /// host can say the network-wide list was refused rather than infer it from shared state
    /// another call may have changed.
    /// </summary>
    internal async Task<(IReadOnlyList<Sensor> Sensors, bool DeviceByDevice)> ReadForDeviceAsync(int deviceId, CancellationToken cancellationToken)
    {
        if (await TryGetFleetAsync(cancellationToken).ConfigureAwait(false) is { } all)
        {
            return (all.Where(s => s.DeviceId == deviceId).ToList(), false);
        }

        var sensors = await ReadDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await ApplyDefinitionsAsync(sensors, cancellationToken).ConfigureAwait(false);
        return (sensors, true);
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

    /// <summary>
    /// As many of <paramref name="deviceIds"/>' sensors as can be read in
    /// <paramref name="budget"/>, in the order given (#71). Devices read in the last
    /// <see cref="SweepLifetime"/> aren't read again, so asking again carries on
    /// where this stopped until every device is covered.
    /// </summary>
    /// <param name="type">Only this sensor class, which takes far fewer requests; null for every class.</param>
    /// <param name="requests">The most requests at once, over every device.</param>
    public async Task<SensorSweep> SweepAsync(IReadOnlyList<int> deviceIds, string? type, int requests, TimeSpan budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (key, value) in _swept)
        {
            if (time.GetUtcNow() - value.ReadAt >= SweepLifetime)
            {
                _swept.TryRemove(key, out _);
            }
        }

        using var deadline = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var gate = new SemaphoreSlim(requests, requests);
        var read = new ConcurrentBag<(IReadOnlyList<Sensor> Sensors, DateTimeOffset ReadAt)>();
        int denied = 0, failed = 0, done = 0;

        await Parallel.ForEachAsync(
            deviceIds.OrderBy(id => _unfinished.ContainsKey(id)),
            new ParallelOptions { MaxDegreeOfParallelism = requests, CancellationToken = cancellationToken },
            async (id, _) =>
            {
                if (Swept(id, type) is { } kept)
                {
                    read.Add(kept);
                    Interlocked.Increment(ref done);
                    return;
                }

                if (deadline.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var own = await ReadDeviceAsync(id, type, gate, linked.Token).ConfigureAwait(false);
                    var now = (own, time.GetUtcNow());
                    _swept[(id, type)] = now;
                    _unfinished.TryRemove(id, out byte _);
                    read.Add(now);
                    Interlocked.Increment(ref done);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _unfinished[id] = 0;
                }
                catch (LibreNmsApiException ex) when (ex.IsPermissionDenied)
                {
                    Interlocked.Increment(ref denied);
                }
                catch (LibreNmsApiException ex) when (!ex.IsAuthenticationFailure)
                {
                    // One device's trouble is its own, until it's everyone's.
                    if (Interlocked.Increment(ref failed) >= FailuresThatEndASweep && Volatile.Read(ref done) == 0)
                    {
                        throw;
                    }
                }
            }).ConfigureAwait(false);

        var sensors = read.SelectMany(r => r.Sensors).ToList();
        await ApplyDefinitionsAsync(sensors, cancellationToken).ConfigureAwait(false);
        return new SensorSweep(sensors, read.Count, denied, failed, deviceIds.Count - read.Count - denied - failed,
            read.IsEmpty ? null : read.Min(r => r.ReadAt));
    }

    private (IReadOnlyList<Sensor> Sensors, DateTimeOffset ReadAt)? Swept(int deviceId, string? type)
    {
        if (_swept.TryGetValue((deviceId, type), out var own))
        {
            return own;
        }

        // Every class was read for an earlier question: the one wanted is among them.
        return type is not null && _swept.TryGetValue((deviceId, null), out var every)
            ? (every.Sensors.Where(s => s.HasClass(type)).ToList(), every.ReadAt)
            : null;
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
        using var gate = new SemaphoreSlim(MaxConcurrency);
        return await ReadDeviceAsync(deviceId, type: null, gate, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Sensor>> ReadDeviceAsync(int deviceId, string? type, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        // Asked for by name, so it may be anything: one of these, or no class at all.
        if (type is not null && (NotSensors.Contains("device_" + type) || !type.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            return [];
        }

        var baseUrl = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/health";
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

        // A class the device may not have: an empty list, or a 404 for that.
        async Task<IReadOnlyList<SensorRef>> OneClass()
        {
            try
            {
                return await transport.GetCollectionAsync<SensorRef>(baseUrl + "/device_" + type, "graphs", cancellationToken).ConfigureAwait(false);
            }
            catch (LibreNmsApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }
        }

        async Task<(string Type, IReadOnlyList<SensorRef> Sensors)[]> EveryClass()
        {
            var types = await Gated(() => transport.GetCollectionAsync<HealthType>(baseUrl, "graphs", cancellationToken)).ConfigureAwait(false);
            return await Task.WhenAll(types
                .Where(t => t.Name is { } name && name.StartsWith("device_", StringComparison.OrdinalIgnoreCase) && !NotSensors.Contains(name))
                .Select(t => Gated(async () => (Type: t.Name!, Sensors: await transport.GetCollectionAsync<SensorRef>(baseUrl + "/" + t.Name, "graphs", cancellationToken).ConfigureAwait(false)))))
                .ConfigureAwait(false);
        }

        var refs = type is null ? await EveryClass().ConfigureAwait(false) : [("device_" + type, await Gated(OneClass).ConfigureAwait(false))];
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

/// <summary>What one <see cref="SensorSource.SweepAsync"/> covered.</summary>
/// <param name="Read">Devices whose sensors are in <paramref name="Sensors"/>, now or from an earlier sweep.</param>
/// <param name="Failed">Devices LibreNMS couldn't answer for.</param>
/// <param name="NotReached">Devices there wasn't time for.</param>
/// <param name="OldestReadAt">When the oldest of these readings was read; null when there are none.</param>
public sealed record SensorSweep(IReadOnlyList<Sensor> Sensors, int Read, int Denied, int Failed, int NotReached, DateTimeOffset? OldestReadAt);

/// <summary>Sensors read device by device, and how many devices this account wasn't allowed to read.</summary>
public sealed record SensorRead(IReadOnlyList<Sensor> Sensors, int DeniedDevices);
