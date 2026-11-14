using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Graphs;

public sealed record GraphLookupOutcome(GraphLookupResult? Result, HostFailure? Error);

/// <summary>Graph reads with fixed failures, Core image limits and connection retirement.</summary>
/// <remarks>Images contain unredacted pixels, as in MCP. Hosts own transport trust, deadlines and image presentation.</remarks>
public sealed class HostGraphLookup : IDisposable
{
    private readonly GraphLookup graphs;
    private readonly CancellationTokenSource lifetime = new();
    private HostGraphLookup(GraphLookup graphs) => this.graphs = graphs;
    public static HostGraphLookup Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time)
        => new(GraphLookup.Create(client, transport, time));
    public async Task<GraphLookupOutcome> ReadAsync(GraphLookupRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => graphs.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public void Dispose() => lifetime.Cancel();
}
