using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>
/// LibreNMS's network-wide lists that more than one tool reads - every
/// sensor, every neighbour link - fetched at most once a minute, like the
/// device list (see <see cref="DeviceDirectory"/>).
/// </summary>
/// <remarks>
/// LibreNMS has no per-device sensor endpoint (the apps read
/// /resources/sensors once and filter it too), and a device's neighbours
/// include links other devices report about it, so both lists are needed
/// whole for questions about one device as well as the network.
/// </remarks>
public sealed class FleetCache(ILibreNmsClient client, TimeProvider time)
{
    private readonly Cached<Sensor> _sensors = new(ct => client.Sensors.ListAsync(ct), time);
    private readonly Cached<NetworkLink> _links = new(ct => client.Links.ListAllAsync(ct), time);
    private readonly Cached<Port> _ports = new(ct => client.Ports.ListAllStatusAsync(ct), time);
    private readonly Cached<AlertRule> _rules = new(ct => client.Rules.ListAsync(ct), time);
    private readonly Cached<AlertTemplate> _templates = new(ct => client.AlertTemplates.ListAsync(ct), time);
    private readonly Cached<Location> _locations = new(ct => client.Locations.ListAsync(ct), time);

    public Task<IReadOnlyList<Sensor>> GetSensorsAsync(CancellationToken cancellationToken) => _sensors.GetAsync(cancellationToken);

    public Task<IReadOnlyList<NetworkLink>> GetLinksAsync(CancellationToken cancellationToken) => _links.GetAsync(cancellationToken);

    /// <summary>
    /// Every port's state and rates in one call - the same snapshot desktop's
    /// Top widgets share (#26). Thousands of rows on a big network, which is
    /// why it's cached rather than read per question.
    /// </summary>
    public Task<IReadOnlyList<Port>> GetPortsAsync(CancellationToken cancellationToken) => _ports.GetAsync(cancellationToken);

    public Task<IReadOnlyList<AlertRule>> GetRulesAsync(CancellationToken cancellationToken) => _rules.GetAsync(cancellationToken);

    public Task<IReadOnlyList<AlertTemplate>> GetTemplatesAsync(CancellationToken cancellationToken) => _templates.GetAsync(cancellationToken);

    /// <summary>LibreNMS's Locations resource, by ID - what an alert rule's "locations" refer to, unlike a device's free-text location.</summary>
    public Task<IReadOnlyList<Location>> GetLocationsAsync(CancellationToken cancellationToken) => _locations.GetAsync(cancellationToken);

    /// <summary>One list, refreshed after <see cref="DeviceDirectory.Lifetime"/>, with parallel callers sharing a fetch.</summary>
    private sealed class Cached<T>(Func<CancellationToken, Task<IReadOnlyList<T>>> fetch, TimeProvider time)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyList<T>? _items;
        private DateTimeOffset _fetchedAt;

        public async Task<IReadOnlyList<T>> GetAsync(CancellationToken cancellationToken)
        {
            if (Fresh() is { } items)
            {
                return items;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Fresh() is { } fetched)
                {
                    return fetched;
                }

                var list = await fetch(cancellationToken).ConfigureAwait(false);
                _items = list;
                _fetchedAt = time.GetUtcNow();
                return list;
            }
            finally
            {
                _gate.Release();
            }
        }

        private IReadOnlyList<T>? Fresh()
            => _items is { } items && time.GetUtcNow() - _fetchedAt < DeviceDirectory.Lifetime ? items : null;
    }
}
