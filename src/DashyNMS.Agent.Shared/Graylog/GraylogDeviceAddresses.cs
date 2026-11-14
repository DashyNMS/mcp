using DesktopNMS.Core.Api;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Graylog;

public static class GraylogDeviceAddresses
{
    public static async Task<IReadOnlyList<string>> ReadAsync(ILibreNmsClient client, Device device, AgentOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<DeviceIpAddress>? interfaceAddresses = null;
        if (options.GraylogMatchAnyAddress)
        {
            var fetched = await Sources.TryAsync(() => client.Ports.ListIpAddressesAsync(device.DeviceId, cancellationToken), cancellationToken).ConfigureAwait(false);
            interfaceAddresses = fetched.Value;
        }

        return GraylogQuery.DeviceAddresses(device, resolvedHostname: null, interfaceAddresses);
    }

}
