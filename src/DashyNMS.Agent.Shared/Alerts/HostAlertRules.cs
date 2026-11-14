using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Alerts;

/// <summary>A typed result or a fixed failure code. Cancellation propagates separately.</summary>
public sealed record AlertReadOutcome<T>(T? Result, HostFailure? Error) where T : class;

/// <summary>Connection-bound alert reads with no source exception text or logging.</summary>
/// <remarks>Hosts must discard results after switching generations and own the transport deadline.</remarks>
public sealed class HostAlertRules : IDisposable
{
    private readonly AlertRules rules;
    private readonly CancellationTokenSource lifetime = new();

    private HostAlertRules(AlertRules rules) => this.rules = rules;

    public static HostAlertRules Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
        => new(AlertRules.Create(client, time, options));

    public async Task<AlertReadOutcome<AlertDetailsResult>> ReadDetailsAsync(AlertDetailsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => rules.ReadDetailsAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with { Data = result.Data with { Unavailable = SafeSources(result.Data.Unavailable) } };
        return new(result, failure);
    }

    public async Task<AlertReadOutcome<AlertRulesResult>> ReadRulesAsync(AlertRulesRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => rules.ReadRulesAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with { Data = result.Data with { Unavailable = SafeSources(result.Data.Unavailable) } };
        return new(result, failure);
    }

    private static IReadOnlyList<Unavailable>? SafeSources(IReadOnlyList<Unavailable>? sources) => sources?.Select(u =>
        new Unavailable(u.Source, u.Reason == "denied" ? "This account isn't allowed to read this source." : "This source could not be read.")).ToList();

    public void Dispose() => lifetime.Cancel();
}
