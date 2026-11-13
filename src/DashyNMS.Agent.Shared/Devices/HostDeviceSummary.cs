using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Devices;

/// <summary>A fixed safe failure. Cancellation is propagated separately, never an empty success.</summary>
public sealed record DeviceSummaryError(HostFailure Code, string Message);

/// <summary>Exactly one of Result and Error is populated.</summary>
public sealed class DeviceSummaryOutcome
{
    internal DeviceSummaryOutcome(DeviceSummaryResult? result, DeviceSummaryError? error)
    {
        Result = result;
        Error = error;
    }

    public DeviceSummaryResult? Result { get; }
    public DeviceSummaryError? Error { get; }
}

/// <summary>
/// One device's summary for hosts that must never receive transport messages, bodies or exception
/// objects: the same boundary as <see cref="HostDeviceSearch"/>. Own one instance per captured
/// immutable connection and dispose it on switch or sign-out. No transports, credentials,
/// environment settings or logging services are registered.
/// </summary>
public sealed class HostDeviceSummary : IDisposable
{
    private readonly DeviceSummaries summaries;
    private readonly CancellationTokenSource lifetime = new();

    private HostDeviceSummary(DeviceSummaries summaries) => this.summaries = summaries;

    /// <summary>
    /// The client must remain bound to this connection even after retirement; cancellation cannot
    /// make a mutable session client's routing safe.
    /// </summary>
    public static HostDeviceSummary Create(ILibreNmsClient client, bool serverTimestampsAreUtc)
        => new(DeviceSummaries.Create(client, new AgentOptions { ServerTimestampsAreUtc = serverTimestampsAreUtc }));

    /// <summary>
    /// Returns safe failures without logging. Caller and retired-generation cancellation throw.
    /// Hosts supply their own linked deadline token and distinguish its expiry from user cancellation.
    /// A device the server doesn't return is <see cref="HostFailure.NotFound"/>; nothing falls back
    /// to the device list, so a failed read is never reported as a device.
    /// </summary>
    public async Task<DeviceSummaryOutcome> ReadAsync(DeviceSummaryRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => summaries.ReadAsync(request, token),
            cancellationToken, lifetime.Token).ConfigureAwait(false);
        return failure is { } code ? new(null, new(code, MessageFor(code))) : new(result, null);
    }

    private static string MessageFor(HostFailure code) => code switch
    {
        HostFailure.InvalidRequest => "The device summary request is invalid.",
        HostFailure.Refused => "The account cannot access the requested device.",
        HostFailure.NotFound => "The device was not found on this server.",
        HostFailure.Timeout => "The device summary request timed out.",
        HostFailure.Unavailable => "The device source is unavailable.",
        _ => "The device summary request failed.",
    };

    /// <summary>
    /// Retires this generation, cancels in-flight waits and refuses future calls.
    /// Keep the source alive for concurrent calls that may still be reading its token.
    /// </summary>
    public void Dispose() => lifetime.Cancel();
}
