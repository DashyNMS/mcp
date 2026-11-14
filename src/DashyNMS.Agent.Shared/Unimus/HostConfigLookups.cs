using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Unimus;

public sealed record ConfigReadOutcome<T>(T? Result, HostFailure? Error) where T : class;

/// <summary>Config reads with fixed failures, host-owned policy and connection retirement.</summary>
public sealed class HostConfigLookups : IDisposable
{
    private readonly ConfigLookups lookups;
    private readonly CancellationTokenSource lifetime = new();
    private HostConfigLookups(ConfigLookups lookups) => this.lookups = lookups;
    public static HostConfigLookups Create(ILibreNmsClient client, IUnimusApi unimus, TimeProvider time, AgentOptions options)
        => new(ConfigLookups.Create(client, unimus, time, options));
    public async Task<ConfigReadOutcome<ConfigBackupResult>> ReadBackupAsync(ConfigBackupRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => lookups.ReadBackupAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public async Task<ConfigReadOutcome<ConfigDiffResult>> ReadDiffAsync(ConfigDiffRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => lookups.ReadDiffAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public async Task<ConfigReadOutcome<RecentConfigChangesResult>> ReadChangesAsync(RecentConfigChangesRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => lookups.ReadChangesAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null) result = result with { Data = result.Data with { Unreadable = result.Data.Unreadable?
            .Select(u => u with { Message = "This device's backups could not be read." }).ToList() } };
        return new(result, failure);
    }
    public void Dispose() => lifetime.Cancel();
}
