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
            // A caller asking for every address must not get a silently narrower search.
            interfaceAddresses = await client.Ports.ListIpAddressesAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        }

        return GraylogQuery.DeviceAddresses(device, resolvedHostname: null, interfaceAddresses);
    }

}
