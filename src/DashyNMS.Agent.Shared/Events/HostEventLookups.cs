using DashyNMS.Agent.Alerts;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;

namespace DashyNMS.Agent.Events;

public sealed record EventReadOutcome<T>(T? Result, HostFailure? Error) where T : class;

/// <summary>Historical reads with fixed failures over host-owned LibreNMS, Graylog and Unimus clients.</summary>
/// <remarks>Optional services may be unconfigured. Hosts own transport deadlines and discard retired-generation results.</remarks>
public sealed class HostEventLookups : IDisposable
{
    private readonly DeviceEventHistory device;
    private readonly NetworkEventHistory network;
    private readonly RaisedAlerts alerts;
    private readonly WhatChanged changes;
    private readonly CancellationTokenSource lifetime = new();

    private HostEventLookups(DeviceEventHistory device, NetworkEventHistory network, RaisedAlerts alerts, WhatChanged changes)
    {
        this.device = device;
        this.network = network;
        this.alerts = alerts;
        this.changes = changes;
    }
    public static HostEventLookups Create(ILibreNmsClient client, IUnimusApi unimus, IGraylogApi graylog, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(unimus);
        ArgumentNullException.ThrowIfNull(graylog);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        var timeline = new DeviceTimeline(client, options);
        var scopes = new DeviceScope(client, directory, time);
        var redactor = new Redactor();
        return new(new(directory, timeline, options, time, redactor),
            new(client, new FleetCache(client, time), directory, scopes, options, time, redactor),
            new(client, directory, scopes, options, time, redactor),
            new(client, unimus, graylog, new UnimusDirectory(unimus, options, time), directory, timeline, options, time, redactor));
    }
    public async Task<EventReadOutcome<DeviceEventsResult>> ReadDeviceAsync(DeviceEventsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => device.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null) result = result with { Data = result.Data with { Unavailable = SafeSources(result.Data.Unavailable) } };
        return new(result, failure);
    }
    public async Task<EventReadOutcome<NetworkEventsResult>> ReadNetworkAsync(NetworkEventsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => network.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public async Task<EventReadOutcome<RaisedAlertsResult>> ReadRaisedAlertsAsync(RaisedAlertsRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => alerts.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }
    public async Task<EventReadOutcome<WhatChangedResult>> ReadChangesAsync(WhatChangedRequest request, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(token => changes.ReadAsync(request, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        if (result is not null) result = result with { Data = result.Data with
        {
            Librenms = result.Data.Librenms with { Unavailable = SafeSources(result.Data.Librenms.Unavailable) },
            Unimus = result.Data.Unimus with { Error = result.Data.Unimus.Error is null ? null : "The config source could not be read." },
            Graylog = result.Data.Graylog with { Error = result.Data.Graylog.Error is null ? null : "The log source could not be read." },
        } };
        return new(result, failure);
    }
    private static IReadOnlyList<Unavailable>? SafeSources(IReadOnlyList<Unavailable>? sources) => sources?.Select(u =>
        new Unavailable(u.Source, u.Reason == "denied" ? "This account isn't allowed to read this source." : "This source could not be read.")).ToList();
    public void Dispose() => lifetime.Cancel();
}
