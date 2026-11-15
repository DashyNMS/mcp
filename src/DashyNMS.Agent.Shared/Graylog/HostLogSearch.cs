using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Graylog;

public sealed record LogSearchOutcome(SearchLogsResult? Result, HostFailure? Error);

/// <summary>Read-only log search with fixed failures and connection retirement.</summary>
/// <remarks>Hosts control configuration, credentials, certificate trust and deadlines. No environment is read.</remarks>
public sealed class HostLogSearch : IDisposable
{
    private readonly LogSearch search;
    private readonly CancellationTokenSource lifetime = new();
    private HostLogSearch(LogSearch search) => this.search = search;
    public static HostLogSearch Create(ILibreNmsClient client, IGraylogApi graylog, TimeProvider time, AgentOptions options)
        => new(LogSearch.Create(client, graylog, time, options));
    public async Task<LogSearchOutcome> ReadAsync(SearchLogsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => search.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public void Dispose() => lifetime.Cancel();
}
