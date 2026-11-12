using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Alerts;

/// <summary>A fixed safe failure. Cancellation is propagated separately, never an empty success.</summary>
public sealed record OpenAlertsError(HostFailure Code, string Message);

/// <summary>Exactly one of Result and Error is populated.</summary>
public sealed class OpenAlertsOutcome
{
    internal OpenAlertsOutcome(OpenAlertsResult? result, OpenAlertsError? error)
    {
        Result = result;
        Error = error;
    }

    public OpenAlertsResult? Result { get; }
    public OpenAlertsError? Error { get; }
}

/// <summary>
/// The open alerts for hosts that must never receive transport messages, bodies or exception
/// objects: the same boundary as <see cref="Devices.HostDeviceSearch"/>. Own one instance per
/// captured immutable connection and dispose it on switch or sign-out. No transports, credentials,
/// environment settings or logging services are registered.
/// </summary>
public sealed class HostOpenAlerts : IDisposable
{
    private readonly OpenAlerts alerts;
    private readonly CancellationTokenSource lifetime = new();

    private HostOpenAlerts(OpenAlerts alerts) => this.alerts = alerts;

    /// <summary>
    /// Creates fresh caches. The client must remain bound to this connection even after retirement;
    /// cancellation cannot make a mutable session client's routing safe.
    /// </summary>
    public static HostOpenAlerts Create(ILibreNmsClient client, TimeProvider time, bool serverTimestampsAreUtc)
        => new(OpenAlerts.Create(client, time, new AgentOptions { ServerTimestampsAreUtc = serverTimestampsAreUtc }));

    /// <summary>
    /// Returns safe failures without logging. Caller and retired-generation cancellation throw.
    /// Hosts supply their own linked deadline token and distinguish its expiry from user cancellation.
    /// No partial success: if the device list or the alerts can't be read, the whole call fails,
    /// so an unreadable source is never reported as no alerts.
    /// </summary>
    public async Task<OpenAlertsOutcome> ListAsync(OpenAlertsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => request is null
                ? throw new ToolException("An open alerts request is required.")
                : alerts.ListAsync(request, token),
            cancellationToken, lifetime.Token).ConfigureAwait(false);
        return failure is { } code ? new(null, new(code, MessageFor(code))) : new(result, null);
    }

    private static string MessageFor(HostFailure code) => code switch
    {
        HostFailure.InvalidRequest => "The open alerts request or scope is invalid or ambiguous.",
        HostFailure.Refused => "The account cannot access the requested alert data.",
        HostFailure.NotFound => "The requested alert data is unavailable on this server.",
        HostFailure.Timeout => "The open alerts request timed out.",
        HostFailure.Unavailable => "The alert source is unavailable.",
        _ => "The open alerts request failed.",
    };

    /// <summary>
    /// Retires this generation, cancels in-flight waits and refuses future calls, including cached calls.
    /// Keep the source alive for concurrent calls that may still be reading its token.
    /// </summary>
    public void Dispose() => lifetime.Cancel();
}
