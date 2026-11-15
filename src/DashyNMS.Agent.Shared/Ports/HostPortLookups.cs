using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Ports;

public sealed record PortReadOutcome<T>(T? Result, HostFailure? Error) where T : class;

/// <summary>Port and topology reads tied to one immutable host connection.</summary>
/// <remarks>Disposal retires pending waits and cached reads. Hosts own transport deadlines and discard old-generation results.</remarks>
public sealed class HostPortLookups : IDisposable
{
    private readonly PortStatus ports;
    private readonly DeviceNeighbours neighbours;
    private readonly TraceEndpoint trace;
    private readonly CancellationTokenSource lifetime = new();

    private HostPortLookups(PortStatus ports, DeviceNeighbours neighbours, TraceEndpoint trace)
    {
        this.ports = ports;
        this.neighbours = neighbours;
        this.trace = trace;
    }

    public static HostPortLookups Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        var fleet = new FleetCache(client, time);
        var redactor = new Redactor();
        return new(new(client, directory, options, redactor), new(client, fleet, directory, redactor),
            new(client, transport, fleet, directory, options, redactor));
    }

    public async Task<PortReadOutcome<PortStatusResult>> ReadStatusAsync(PortStatusRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => ports.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with { Data = result.Data with { Unavailable = SafeSources(result.Data.Unavailable) } };
        return new(result, failure);
    }

    public async Task<PortReadOutcome<DeviceNeighboursResult>> ReadNeighboursAsync(DeviceNeighboursRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => neighbours.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with { Data = result.Data with { Unavailable = SafeSources(result.Data.Unavailable) } };
        return new(result, failure);
    }

    public async Task<PortReadOutcome<TraceEndpointResult>> TraceAsync(TraceEndpointRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => trace.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with
            {
                Data = result.Data with { Unavailable = SafeSources(result.Data.Unavailable) },
                Observations = result.Observations.Select(o => o with { Unavailable = SafeSources(o.Unavailable)! }).ToList(),
            };
        return new(result, failure);
    }

    private static IReadOnlyList<Unavailable>? SafeSources(IReadOnlyList<Unavailable>? sources) => sources?.Select(u =>
        // The switches notice contains only our count and cap, and must remain distinguishable from a failed read.
        u.Source == "switches" ? u : new Unavailable(u.Source,
            u.Reason == "denied" ? "This account isn't allowed to read this source." : "This source could not be read.")).ToList();

    public void Dispose() => lifetime.Cancel();
}
