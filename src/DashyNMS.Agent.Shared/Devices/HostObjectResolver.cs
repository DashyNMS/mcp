using System.Globalization;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>An exact identity revalidated on this connection, with a redacted display name.</summary>
public sealed record ResolvedObject(IntelligenceObjectReference Identity, string Name);

/// <summary>Only a resolved identity or a fixed failure category crosses the host boundary.</summary>
public sealed record ObjectResolutionOutcome(ResolvedObject? Result, HostFailure? Failure);

/// <summary>
/// Revalidates Spotlight identities without name matching or detail lookups. Create one per immutable
/// server/account connection and retire it on sign-out or switch; IDs alone do not identify a server.
/// The caller must also check chat ownership after awaiting, before applying a result.
/// </summary>
public sealed class HostObjectResolver : IDisposable
{
    private readonly ILibreNmsClient client;
    private readonly Redactor redactor = new();
    private readonly CancellationTokenSource lifetime = new();

    private HostObjectResolver(ILibreNmsClient client) => this.client = client;

    public static HostObjectResolver Create(ILibreNmsClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(client);
    }

    /// <summary>
    /// Reads fresh identities, including recovered alerts. A missing or mismatched object is NotFound,
    /// never a fallback to its parent device. Caller and retired-generation cancellation throw.
    /// Hosts supply their own deadline; transport exceptions and server text never escape.
    /// </summary>
    public async Task<ObjectResolutionOutcome> ResolveAsync(IntelligenceObjectReference reference, CancellationToken cancellationToken = default)
    {
        var (result, failure) = await HostBoundary.RunAsync(
            token => ReadAsync(reference, token), cancellationToken, lifetime.Token).ConfigureAwait(false);
        return new(result, failure);
    }

    private async Task<ResolvedObject> ReadAsync(IntelligenceObjectReference reference, CancellationToken token)
    {
        if (reference is null || !reference.IsValid || reference.RuleId is <= 0)
            throw new ToolException("A valid object identity is required.");
        string? name = null;
        switch (reference.Kind)
        {
            case "device":
                var device = await client.Devices.GetAsync(reference.Id.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
                if (device?.DeviceId == reference.Id) name = device.Display ?? device.Hostname ?? $"Device {reference.Id}";
                break;
            case "group":
                name = (await client.DeviceGroups.ListAsync(token).ConfigureAwait(false)).SingleOrDefault(g => g.Id == reference.Id)?.Name;
                break;
            case "location":
                name = (await client.Locations.ListAsync(token).ConfigureAwait(false)).SingleOrDefault(l => l.Id == reference.Id)?.Name;
                break;
            case "sensor":
                var sensor = (await client.Sensors.ListAsync(token).ConfigureAwait(false))
                    .SingleOrDefault(s => s.SensorId == reference.Id && s.DeviceId == reference.DeviceId);
                if (sensor is not null) name = sensor.Description ?? $"Sensor {reference.Id}";
                break;
            case "port":
                var port = (await client.Ports.ListForDeviceAsync(reference.DeviceId!.Value, token).ConfigureAwait(false))
                    .SingleOrDefault(p => p.PortId == reference.Id && p.DeviceId == reference.DeviceId);
                if (port is not null) name = port.IfName ?? port.IfDescr ?? $"Port {reference.Id}";
                break;
            case "alert":
            case "fault":
                // Direct GetAsync defaults to active state on some servers. Check every state so
                // an acknowledged or recovered object does not disappear from a saved Spotlight.
                var alert = (await client.Alerts.ListAsync(new AlertQuery
                    { Id = reference.Id, States = AlertQuery.All.States, OrderBy = null }, token).ConfigureAwait(false))
                    .SingleOrDefault(a => a.Id == reference.Id && a.DeviceId == reference.DeviceId
                        && (reference.RuleId is null || a.RuleId == reference.RuleId)
                        && (a.Source == AlertSource.Fault) == (reference.Kind == "fault"));
                if (alert is not null) name = $"{(reference.Kind == "fault" ? "Fault" : "Alert")} {reference.Id}";
                break;
        }
        token.ThrowIfCancellationRequested();
        if (name is null) throw new DeviceNotFoundException();
        return new(reference, redactor.Redact(name));
    }

    /// <summary>Cancel in-flight waits and reject future reads for this connection generation.</summary>
    public void Dispose() => lifetime.Cancel();
}
