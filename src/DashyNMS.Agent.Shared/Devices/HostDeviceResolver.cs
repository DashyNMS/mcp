using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Devices;

/// <summary>Typed arguments for resolving a device phrase.</summary>
/// <param name="Device">A hostname, display name, sysName, IP address or LibreNMS device id, as a model gives it.</param>
public sealed record DeviceResolutionRequest(string? Device);

/// <summary>A fixed safe failure. Cancellation is propagated separately, never an empty success.</summary>
public sealed record DeviceResolutionError(HostFailure Code, string Message);

/// <summary>
/// Exactly one of Result and Error is populated. An ambiguous or unmatched phrase is a result
/// (<see cref="ResolvedMatch"/>), not an error: the device list was read, and it says so.
/// </summary>
public sealed class DeviceResolutionOutcome
{
    internal DeviceResolutionOutcome(DeviceResolution? result, DeviceResolutionError? error)
    {
        Result = result;
        Error = error;
    }

    public DeviceResolution? Result { get; }
    public DeviceResolutionError? Error { get; }
}

/// <summary>
/// Resolves a device phrase as MCP's device tools do, for hosts that must never receive transport
/// messages, bodies or exception objects: the same boundary as <see cref="HostDeviceSearch"/>. Own one
/// instance per captured immutable connection and dispose it on switch or sign-out. Its device list is
/// cached for <see cref="DeviceDirectory.Lifetime"/> per instance, never across connections, so a found
/// device's state can be up to that old.
/// </summary>
public sealed class HostDeviceResolver : IDisposable
{
    private readonly DeviceResolver resolver;
    private readonly AgentOptions options;
    private readonly Redactor redactor = new();
    private readonly CancellationTokenSource lifetime = new();

    private HostDeviceResolver(DeviceResolver resolver, AgentOptions options)
    {
        this.resolver = resolver;
        this.options = options;
    }

    /// <summary>
    /// Creates a fresh device list cache. The client must remain bound to this connection even after
    /// retirement; cancellation cannot make a mutable session client's routing safe.
    /// </summary>
    public static HostDeviceResolver Create(ILibreNmsClient client, TimeProvider time, bool serverTimestampsAreUtc)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        return new(new DeviceResolver(new DeviceDirectory(client, time)), new AgentOptions { ServerTimestampsAreUtc = serverTimestampsAreUtc });
    }

    /// <summary>
    /// Returns the outcome, or a safe failure without logging. An empty phrase is
    /// <see cref="HostFailure.InvalidRequest"/>; a refused device list is <see cref="HostFailure.Refused"/>,
    /// never "no match". Caller and retired-generation cancellation throw. Hosts supply their own linked
    /// deadline token and distinguish its expiry from user cancellation.
    /// </summary>
    public async Task<DeviceResolutionOutcome> ResolveAsync(DeviceResolutionRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => request is null
                ? throw new ToolException("A device resolution request is required.")
                : resolver.FindAsync(request.Device, options, redactor, token),
            cancellationToken, lifetime.Token).ConfigureAwait(false);
        return failure is { } code ? new(null, new(code, MessageFor(code))) : new(result, null);
    }

    private static string MessageFor(HostFailure code) => code switch
    {
        HostFailure.InvalidRequest => "Say which device: a hostname, display name, IP address or device id.",
        HostFailure.Refused => "The account cannot read the device list.",
        HostFailure.NotFound => "The device list was not found on this server.",
        HostFailure.Timeout => "The device list request timed out.",
        HostFailure.Unavailable => "The device source is unavailable.",
        _ => "The device resolution request failed.",
    };

    /// <summary>
    /// Retires this generation, cancels in-flight waits and refuses future calls.
    /// Keep the source alive for concurrent calls that may still be reading its token.
    /// </summary>
    public void Dispose() => lifetime.Cancel();
}
