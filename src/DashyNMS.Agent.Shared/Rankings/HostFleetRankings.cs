using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Rankings;

public sealed record FleetRankingOutcome<T>(T? Result, HostFailure? Error) where T : class;

/// <summary>Fleet rankings with fixed failures and retirement of connection-bound caches.</summary>
public sealed class HostFleetRankings : IDisposable
{
    private readonly FleetRankings rankings;
    private readonly CancellationTokenSource lifetime = new();
    private HostFleetRankings(FleetRankings rankings) => this.rankings = rankings;
    public static HostFleetRankings Create(ILibreNmsClient client, TimeProvider time) => new(FleetRankings.Create(client, time));

    public async Task<FleetRankingOutcome<TopPortsResult>> ReadPortsAsync(TopPortsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => rankings.ReadPortsAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public async Task<FleetRankingOutcome<TopDevicesResult>> ReadDevicesAsync(TopDevicesRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => rankings.ReadDevicesAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public void Dispose() => lifetime.Cancel();
}
