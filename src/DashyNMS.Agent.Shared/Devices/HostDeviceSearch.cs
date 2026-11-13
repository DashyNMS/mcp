using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Devices;

/// <summary>Stable host-facing failure categories; no server text or exception details are returned.</summary>
public enum DeviceSearchFailure
{
    InvalidRequest,
    Refused,
    NotFound,
    Timeout,
    Unavailable,
    Failed,
}

/// <summary>A fixed safe failure. Cancellation is propagated separately, never an empty success.</summary>
public sealed record DeviceSearchError(DeviceSearchFailure Code, string Message);

/// <summary>Exactly one of Result and Error is populated by the executor.</summary>
public sealed class DeviceSearchOutcome
{
    internal DeviceSearchOutcome(FindDevicesResult? result, DeviceSearchError? error)
    {
        Result = result;
        Error = error;
    }

    public FindDevicesResult? Result { get; }
    public DeviceSearchError? Error { get; }
}

/// <summary>
/// Safe boundary for hosts that must never receive transport messages, bodies or exception objects.
/// Own one instance per captured immutable connection and dispose it on switch or sign-out.
/// No transports, credentials, environment settings or logging services are registered.
/// </summary>
public sealed class HostDeviceSearch : IDisposable
{
    private readonly DeviceSearch search;
    private readonly CancellationTokenSource lifetime = new();

    private HostDeviceSearch(DeviceSearch search) => this.search = search;

    /// <summary>
    /// Creates fresh caches. The client must remain bound to this connection even after retirement;
    /// cancellation cannot make a mutable session client's routing safe.
    /// </summary>
    public static HostDeviceSearch Create(ILibreNmsClient client, TimeProvider time, bool serverTimestampsAreUtc)
        => new(DeviceSearch.Create(client, time, new AgentOptions { ServerTimestampsAreUtc = serverTimestampsAreUtc }));

    /// <summary>
    /// Returns safe failures without logging. Caller and retired-generation cancellation throw.
    /// WaitAsync bounds the caller's wait when a transport ignores cancellation; late results are discarded.
    /// Hosts supply their own linked deadline token and distinguish its expiry from user cancellation.
    /// </summary>
    public async Task<DeviceSearchOutcome> FindDevicesAsync(FindDevicesRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => request is null
                ? throw new ToolException("A device search request is required.")
                : search.FindDevicesAsync(request, token),
            cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (failure is not { } host)
            return new(result, null);
        var code = host switch
        {
            HostFailure.InvalidRequest => DeviceSearchFailure.InvalidRequest,
            HostFailure.Refused => DeviceSearchFailure.Refused,
            HostFailure.NotFound => DeviceSearchFailure.NotFound,
            HostFailure.Timeout => DeviceSearchFailure.Timeout,
            HostFailure.Unavailable => DeviceSearchFailure.Unavailable,
            _ => DeviceSearchFailure.Failed,
        };
        return new(null, new(code, MessageFor(code)));
    }

    internal static Task ObserveLateFault(Task task) => HostBoundary.ObserveLateFault(task);

    private static string MessageFor(DeviceSearchFailure code) => code switch
    {
        DeviceSearchFailure.InvalidRequest => "The device search or scope is invalid or ambiguous.",
        DeviceSearchFailure.Refused => "The account cannot access the requested device data.",
        DeviceSearchFailure.NotFound => "The requested device data is unavailable on this server.",
        DeviceSearchFailure.Timeout => "The device search timed out.",
        DeviceSearchFailure.Unavailable => "The device search source is unavailable.",
        _ => "The device search failed.",
    };

    /// <summary>
    /// Retires this generation, cancels in-flight waits and refuses future calls, including cached calls.
    /// Keep the source alive for concurrent calls that may still be reading its token.
    /// </summary>
    public void Dispose() => lifetime.Cancel();
}
