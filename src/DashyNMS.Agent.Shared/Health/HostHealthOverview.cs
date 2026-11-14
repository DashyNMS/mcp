using DesktopNMS.Core.Api;
using DesktopNMS.Core.Sensors;

namespace DashyNMS.Agent.Health;

public sealed record HealthOverviewOutcome(HealthOverviewResult? Result, HostFailure? Error);

/// <summary>Network sensor reads with fixed failures, captured thresholds and connection retirement.</summary>
public sealed class HostHealthOverview : IDisposable
{
    private readonly HealthOverview overview;
    private readonly CancellationTokenSource lifetime = new();
    private HostHealthOverview(HealthOverview overview) => this.overview = overview;
    public static HostHealthOverview Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time,
        AgentOptions options, SensorDefinitionStore? definitions = null) => new(HealthOverview.Create(client, transport, time, options, definitions));

    public async Task<HealthOverviewOutcome> ReadAsync(HealthOverviewRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => overview.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public void Dispose() => lifetime.Cancel();
}
