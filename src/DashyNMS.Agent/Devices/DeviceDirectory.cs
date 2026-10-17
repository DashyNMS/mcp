using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>
/// LibreNMS's device list, fetched at most once a minute.
/// </summary>
/// <remarks>
/// Almost every tool call starts by resolving a device, and alerts need
/// device names, so without this one question could fetch the whole list
/// several times. A minute old is fresh enough for names and addresses;
/// up/down state comes from each tool's own calls.
/// </remarks>
public sealed class DeviceDirectory(ILibreNmsClient client, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<Device>? _devices;
    private DateTimeOffset _fetchedAt;

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } devices)
        {
            return devices;
        }

        // One fetch at a time, so parallel tool calls share it.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } fetched)
            {
                return fetched;
            }

            var list = await client.Devices.ListAsync(cancellationToken).ConfigureAwait(false);
            _devices = list;
            _fetchedAt = time.GetUtcNow();
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<Device>? Fresh()
        => _devices is { } devices && time.GetUtcNow() - _fetchedAt < Lifetime ? devices : null;
}
