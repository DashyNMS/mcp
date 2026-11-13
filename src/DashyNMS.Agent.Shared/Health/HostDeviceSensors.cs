using DesktopNMS.Core.Api;
using DesktopNMS.Core.Sensors;

namespace DashyNMS.Agent.Health;

/// <summary>A fixed safe failure. Cancellation is propagated separately, never an empty success.</summary>
public sealed record DeviceSensorsError(HostFailure Code, string Message);

/// <summary>Exactly one of Result and Error is populated.</summary>
public sealed class DeviceSensorsOutcome
{
    internal DeviceSensorsOutcome(DeviceSensorsResult? result, DeviceSensorsError? error)
    {
        Result = result;
        Error = error;
    }

    public DeviceSensorsResult? Result { get; }
    public DeviceSensorsError? Error { get; }
}

/// <summary>
/// One device's sensor readings for hosts that must never receive transport messages, bodies or
/// exception objects: the same boundary as <see cref="Alerts.HostOpenAlerts"/>. Own one instance
/// per captured immutable connection and dispose it on switch or sign-out. No transports,
/// credentials, environment settings or logging services are registered.
/// </summary>
/// <remarks>
/// Unlike the device search and open alerts, this needs the transport as well as the client:
/// LibreNMS's per-device sensor routes, used when the account may not read the network-wide list,
/// aren't in Core's client yet (see <see cref="Devices.SensorSource"/>). Both must stay bound to
/// the same connection. The routes read are <c>devices</c>, <c>resources/sensors</c> and, only
/// after that list is refused, <c>devices/{id}/health</c> and the sensor types and sensors beneath
/// it (never processors, memory pools or storage).
/// </remarks>
public sealed class HostDeviceSensors : IDisposable
{
    private readonly DeviceSensors sensors;
    private readonly CancellationTokenSource lifetime = new();

    private HostDeviceSensors(DeviceSensors sensors) => this.sensors = sensors;

    /// <summary>
    /// Creates fresh caches. The client and transport must remain bound to this connection even
    /// after retirement; cancellation cannot make a mutable session client's routing safe.
    /// Definitions, when given, should be read-only, as desktop gives MCP, so a question never
    /// downloads a catalogue.
    /// </summary>
    public static HostDeviceSensors Create(ILibreNmsClient client, ILibreNmsTransport transport, TimeProvider time,
        bool serverTimestampsAreUtc, SensorDefinitionStore? definitions = null)
        => new(DeviceSensors.Create(client, transport, time, new AgentOptions { ServerTimestampsAreUtc = serverTimestampsAreUtc }, definitions));

    /// <summary>
    /// Returns safe failures without logging. Caller and retired-generation cancellation throw.
    /// Hosts supply their own linked deadline token and distinguish its expiry from user cancellation.
    /// No partial success: if the device list or the readings can't be read, the whole call fails,
    /// so an unreadable source is never reported as no sensors. A device ID the device list doesn't
    /// hold is <see cref="HostFailure.InvalidRequest"/>.
    /// </summary>
    public async Task<DeviceSensorsOutcome> ReadAsync(DeviceSensorsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => request is null
                ? throw new ToolException("A device sensors request is required.")
                : sensors.ReadAsync(request, token),
            cancellationToken, lifetime.Token).ConfigureAwait(false);
        return failure is { } code ? new(null, new(code, MessageFor(code))) : new(result, null);
    }

    private static string MessageFor(HostFailure code) => code switch
    {
        HostFailure.InvalidRequest => "The device sensors request is invalid, or the device isn't visible to this account.",
        HostFailure.Refused => "The account cannot access the requested sensor data.",
        HostFailure.NotFound => "The requested sensor data is unavailable on this server.",
        HostFailure.Timeout => "The device sensors request timed out.",
        HostFailure.Unavailable => "The sensor source is unavailable.",
        _ => "The device sensors request failed.",
    };

    /// <summary>
    /// Retires this generation, cancels in-flight waits and refuses future calls, including cached calls.
    /// Keep the source alive for concurrent calls that may still be reading its token.
    /// </summary>
    public void Dispose() => lifetime.Cancel();
}
