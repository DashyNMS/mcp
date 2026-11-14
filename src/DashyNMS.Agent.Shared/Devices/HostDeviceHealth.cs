using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Devices;

/// <summary>A fixed safe failure. Cancellation is propagated separately, never an empty success.</summary>
public sealed record DeviceHealthError(HostFailure Code, string Message);

/// <summary>Exactly one of Result and Error is populated.</summary>
public sealed class DeviceHealthOutcome
{
    internal DeviceHealthOutcome(DeviceHealthResult? result, DeviceHealthError? error)
    {
        Result = result;
        Error = error;
    }

    public DeviceHealthResult? Result { get; }
    public DeviceHealthError? Error { get; }
}

/// <summary>
/// One device's health for hosts that must never receive transport messages, bodies or exception
/// objects: the same boundary as <see cref="HostDeviceSearch"/>. Own one instance per captured
/// immutable connection and dispose it on switch or sign-out. No transports, credentials,
/// environment settings or logging services are registered.
/// </summary>
public sealed class HostDeviceHealth : IDisposable
{
    private readonly DeviceHealth summaries;
    private readonly CancellationTokenSource lifetime = new();

    private HostDeviceHealth(DeviceHealth summaries) => this.summaries = summaries;

    /// <summary>
    /// The client must remain bound to this connection even after retirement; cancellation cannot
    /// make a mutable session client's routing safe.
    /// </summary>
    public static HostDeviceHealth Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
        => new(DeviceHealth.Create(client, time, options));

    /// <summary>
    /// Returns safe failures without logging. Caller and retired-generation cancellation throw.
    /// Hosts supply their own linked deadline token and distinguish its expiry from user cancellation.
    /// A device absent from the device list is <see cref="HostFailure.NotFound"/>; nothing falls back
    /// outside the visible device list. Individual failed sources are explicitly unavailable.
    /// </summary>
    public async Task<DeviceHealthOutcome> ReadAsync(DeviceHealthRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => summaries.ReadAsync(request, token),
            cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with { Data = result.Data with { Unavailable = result.Data.Unavailable?.Select(u =>
                new Unavailable(u.Source, u.Reason == "denied" ? "This account isn't allowed to read this source." : "This source could not be read.")).ToList() } };
        return failure is { } code ? new(null, new(code, MessageFor(code))) : new(result, null);
    }

    private static string MessageFor(HostFailure code) => code switch
    {
        HostFailure.InvalidRequest => "The device health request is invalid.",
        HostFailure.Refused => "The account cannot access the requested device.",
        HostFailure.NotFound => "The device was not found on this server.",
        HostFailure.Timeout => "The device health request timed out.",
        HostFailure.Unavailable => "The device source is unavailable.",
        _ => "The device health request failed.",
    };

    /// <summary>
    /// Retires this generation, cancels in-flight waits and refuses future calls.
    /// Keep the source alive for concurrent calls that may still be reading its token.
    /// </summary>
    public void Dispose() => lifetime.Cancel();
}
