using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Devices;

/// <summary>A fixed safe failure. Cancellation is propagated separately, never an empty success.</summary>
public sealed record GroupsAndLocationsError(HostFailure Code, string Message);

/// <summary>Exactly one of Result and Error is populated.</summary>
public sealed class GroupsAndLocationsOutcome
{
    internal GroupsAndLocationsOutcome(GroupsAndLocationsResult? result, GroupsAndLocationsError? error)
    {
        Result = result;
        Error = error;
    }

    public GroupsAndLocationsResult? Result { get; }
    public GroupsAndLocationsError? Error { get; }
}

/// <summary>
/// One device's health for hosts that must never receive transport messages, bodies or exception
/// objects: the same boundary as <see cref="HostDeviceSearch"/>. Own one instance per captured
/// immutable connection and dispose it on switch or sign-out. No transports, credentials,
/// environment settings or logging services are registered.
/// </summary>
public sealed class HostGroupsAndLocations : IDisposable
{
    private readonly GroupsAndLocations summaries;
    private readonly CancellationTokenSource lifetime = new();

    private HostGroupsAndLocations(GroupsAndLocations summaries) => this.summaries = summaries;

    /// <summary>
    /// The client must remain bound to this connection even after retirement; cancellation cannot
    /// make a mutable session client's routing safe.
    /// </summary>
    public static HostGroupsAndLocations Create(ILibreNmsClient client, TimeProvider time)
        => new(GroupsAndLocations.Create(client, time));

    /// <summary>
    /// Returns safe failures without logging. Caller and retired-generation cancellation throw.
    /// Hosts supply their own linked deadline token and distinguish its expiry from user cancellation.
    /// Devices are required; unreadable groups are explicitly unavailable.
    /// </summary>
    public async Task<GroupsAndLocationsOutcome> ReadAsync(CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => summaries.ReadAsync(token),
            cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null)
            result = result with { Data = result.Data with { Unavailable = result.Data.Unavailable?.Select(u =>
                new Unavailable(u.Source, u.Reason == "denied" ? "This account isn't allowed to read this source." : "This source could not be read.")).ToList() } };
        return failure is { } code ? new(null, new(code, MessageFor(code))) : new(result, null);
    }

    private static string MessageFor(HostFailure code) => code switch
    {
        HostFailure.InvalidRequest => "The groups and locations request is invalid.",
        HostFailure.Refused => "The account cannot access the requested device.",
        HostFailure.NotFound => "The device was not found on this server.",
        HostFailure.Timeout => "The groups and locations request timed out.",
        HostFailure.Unavailable => "The device source is unavailable.",
        _ => "The groups and locations request failed.",
    };

    /// <summary>
    /// Retires this generation, cancels in-flight waits and refuses future calls.
    /// Keep the source alive for concurrent calls that may still be reading its token.
    /// </summary>
    public void Dispose() => lifetime.Cancel();
}
