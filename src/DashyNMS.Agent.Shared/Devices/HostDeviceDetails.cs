using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Devices;

public sealed record DeviceDetailsOutcome(DeviceDetailsResult? Result, HostFailure? Error);

/// <summary>Detail reads with fixed failures, tied to one immutable host connection.</summary>
/// <remarks>Hosts own the client, transport and deadline, and discard old-generation replies after switching.</remarks>
public sealed class HostDeviceDetails : IDisposable
{
    private readonly DeviceDetails details;
    private readonly CancellationTokenSource lifetime = new();
    private HostDeviceDetails(DeviceDetails details) => this.details = details;

    public static HostDeviceDetails Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time)
        => new(DeviceDetails.Create(client, transport, time));

    public async Task<DeviceDetailsOutcome> ReadAsync(DeviceDetailsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => details.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with { Data = result.Data with { Unavailable = result.Data.Unavailable?.Select(u =>
                new Unavailable(u.Source, u.Reason == "denied" ? "This account isn't allowed to read this source." : "This source could not be read.")).ToList() } };
        return new(result, failure);
    }

    public void Dispose() => lifetime.Cancel();
}
