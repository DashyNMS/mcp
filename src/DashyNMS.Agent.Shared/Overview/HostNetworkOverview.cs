using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Overview;

public sealed record NetworkOverviewError(HostFailure Code, string Message);
public sealed record NetworkOverviewOutcome(NetworkOverviewResult? Result, NetworkOverviewError? Error);

/// <summary>Safe host boundary with per-connection retirement, matching search and open alerts.</summary>
public sealed class HostNetworkOverview : IDisposable
{
    private readonly NetworkOverview overview;
    private readonly CancellationTokenSource lifetime = new();
    private HostNetworkOverview(NetworkOverview overview) => this.overview = overview;
    public static HostNetworkOverview Create(ILibreNmsClient client, TimeProvider time, bool serverTimestampsAreUtc)
        => new(NetworkOverview.Create(client, time, serverTimestampsAreUtc));

    public async Task<NetworkOverviewOutcome> ReadAsync(CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(overview.ReadAsync, cancellationToken, lifetime.Token).ConfigureAwait(false);
        return failure is { } code ? new(null, new(code, "The network overview could not be read.")) : new(result, null);
    }

    public void Dispose() => lifetime.Cancel();
}
