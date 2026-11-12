using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Alerts;

/// <summary>
/// Typed arguments for the alerts open now. Execution owns grouping, filtering, ordering and paging.
/// </summary>
/// <param name="DeviceId">
/// Only this LibreNMS device. Hosts resolve names themselves (MCP's resolver, mobile's device
/// search), so the operation never guesses which device a phrase meant.
/// </param>
/// <param name="Severity">critical, warning or ok; null, empty, "all" or "any" for every severity.</param>
public sealed record OpenAlertsRequest(int? DeviceId = null, string? Severity = null, string? Group = null,
    string? Location = null, int Offset = 0, int Limit = OpenAlerts.DefaultLimit);

/// <summary>A device and rule group's state, from its faults.</summary>
public enum OpenAlertState
{
    /// <summary>At least one fault still needs attention and none is acknowledged.</summary>
    Active,

    /// <summary>Every fault is acknowledged.</summary>
    Acknowledged,

    /// <summary>
    /// Some faults acknowledged and some not. Worth saying apart from active: acknowledging
    /// one fault may not have covered the rest.
    /// </summary>
    PartlyAcknowledged,
}

/// <summary>One fault in a group, with its own source-qualified identity and state.</summary>
/// <param name="Key">
/// <c>fault:12</c> on LibreNMS 26.10 and later, <c>alert:12</c> before. A bare number is never
/// enough: a fault and a legacy alert can share one, and they are different things to open or act on.
/// </param>
/// <param name="Entity">"CPU 0", a port, a location; null for one about the whole device.</param>
/// <param name="EntityType">LibreNMS's entity type ("processor", "port"), when it says.</param>
public sealed record OpenAlertFault(AlertKey Key, string? Entity, string? EntityType, long? EntityId, AlertState State, DateTime? Since);

/// <summary>
/// One rule firing on one device: Core's <see cref="AlertGroup"/>, as every host reports it, so
/// MCP and the apps count, order and word groups the same way. Before LibreNMS 26.10 every group
/// holds one alert.
/// </summary>
/// <param name="Key">
/// The device and rule. Its own type, distinct from <see cref="AlertKey"/>, so a group can never be
/// taken for a fault or alert id, or a fault id for a group.
/// </param>
/// <param name="Representative">The fault to open for the group: the first unacknowledged one, else the first.</param>
/// <param name="Severity">The worst of its faults.</param>
/// <param name="Entity">What the fault is about, for a group of one.</param>
/// <param name="Since">The earliest any fault was first seen, in UTC.</param>
/// <param name="LastChanged">The latest any fault changed, in UTC.</param>
/// <param name="Faults">Up to <see cref="OpenAlerts.MaxFaults"/> faults, unacknowledged first; the rest are counted in <paramref name="FaultsOmitted"/>.</param>
/// <param name="Hostname">The device's hostname from the device list, or the alert's when the list doesn't hold the device.</param>
/// <param name="Device">The device as the device list has it; null when the list doesn't hold it.</param>
public sealed record OpenAlertGroup(
    AlertGroupKey Key,
    AlertKey Representative,
    string Rule,
    AlertSeverity Severity,
    OpenAlertState State,
    int FaultCount,
    int AcknowledgedCount,
    string? Entity,
    DateTime? Since,
    DateTime? LastChanged,
    string? Note,
    IReadOnlyList<OpenAlertFault> Faults,
    int FaultsOmitted,
    string? Hostname,
    DeviceSummary? Device)
{
    public int DeviceId => Key.DeviceId;

    public int RuleId => Key.RuleId;
}

/// <summary>A page of groups and the totals over every group that matched.</summary>
/// <param name="Totals">Over every matching group, not only this page, so a host can say how many it left out.</param>
/// <param name="Filtered">True when a device, severity, group or location narrowed the alerts.</param>
public sealed record OpenAlertsResult(IReadOnlyList<OpenAlertGroup> Groups, OpenAlertTotals Totals, PageInfo Page, bool Filtered)
{
    /// <summary>Every matching group in order, so MCP can shorten a page to its byte cap without fetching again.</summary>
    internal IReadOnlyList<OpenAlertGroup> Matching { get; init; } = [];

    /// <summary>The same matches, paged again from the same offset with a smaller limit.</summary>
    internal OpenAlertsResult Repage(int limit)
    {
        var groups = Paging.Apply(Matching, Page.Offset, limit, out var page);
        return this with { Groups = groups, Page = page };
    }
}

/// <summary>
/// The alerts open now, one per rule firing on a device, without MCP invocation or JSON. On LibreNMS
/// 26.10 a group holds every fault LibreNMS keeps for it: eight busy cores are one group of eight
/// faults, not eight alerts. Groups come in Core's stable order (worst, then most recently changed),
/// so pages don't shuffle between calls.
/// </summary>
/// <remarks>
/// This executor does not log. Propagated exceptions may contain sensitive messages or details;
/// hosts without MCP's reviewed error policy use <see cref="HostOpenAlerts"/>, which maps them to
/// fixed safe categories. Create one per captured connection; the caller owns transport lifetime
/// and cancellation.
/// </remarks>
public sealed class OpenAlerts
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    /// <summary>Faults listed per group; the rest are counted, never silently dropped.</summary>
    public const int MaxFaults = 25;

    private readonly ILibreNmsClient client;
    private readonly DeviceDirectory directory;
    private readonly DeviceScope scopes;
    private readonly AgentOptions options;
    private readonly Redactor redactor;

    /// <summary>Reuses MCP's connection-bound caches; other hosts use <see cref="Create"/>.</summary>
    internal OpenAlerts(ILibreNmsClient client, DeviceDirectory directory, DeviceScope scopes, AgentOptions options, Redactor redactor)
    {
        this.client = client;
        this.directory = directory;
        this.scopes = scopes;
        this.options = options;
        this.redactor = redactor;
    }

    /// <summary>Creates isolated caches around a host-owned, connection-bound client without registering transports.</summary>
    public static OpenAlerts Create(ILibreNmsClient client, TimeProvider time, AgentOptions options, Redactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        return new(client, directory, new DeviceScope(client, directory, time), options, redactor ?? new Redactor());
    }

    public async Task<OpenAlertsResult> ListAsync(OpenAlertsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var severity = ParseSeverity(request.Severity);
        Paging.Check(request.Offset, request.Limit, MaxLimit);
        if (request.DeviceId is <= 0)
        {
            throw new ToolException("A device ID must be a positive number.");
        }

        var scope = await scopes.ResolveAsync(request.Group, request.Location, cancellationToken).ConfigureAwait(false);
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);
        var alerts = await client.Alerts.ListAsync(AlertQuery.Open, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // The open query asks only for firing and acknowledged rows, but a recovered fault
        // returned beside its open siblings must not keep its group open or count as a fault,
        // as OpenAlertTotals already treats it.
        var groups = AlertGrouping.ByDeviceAndRule(alerts
                .Where(a => a.State != AlertState.Recovered)
                .Where(a => request.DeviceId is not { } id || a.DeviceId == id)
                .Where(a => scope.Includes(a.DeviceId, devices)))
            .Where(g => severity is null || g.Severity == severity)
            .ToList();

        var matching = groups.Select(g => Shape(g, devices)).ToList();
        var shown = Paging.Apply(matching, request.Offset, request.Limit, out var page);
        var filtered = severity is not null || request.DeviceId is not null || !scope.IsEverything;
        return new(shown, OpenAlertTotals.From(groups), page, filtered) { Matching = matching };
    }

    internal OpenAlertGroup Shape(AlertGroup group, IReadOnlyDictionary<int, Device> devices)
    {
        devices.TryGetValue(group.DeviceId, out var device);
        var faults = group.Faults.Take(MaxFaults).Select(f => new OpenAlertFault(
            new AlertKey(f.Source, f.Id),
            Clean(AlertGrouping.EntityName(f)),
            Clean(f.EntityType),
            f.EntityId,
            f.State,
            Utc(f.FirstSeen ?? f.Timestamp))).ToList();
        var state = group.IsPartlyAcknowledged ? OpenAlertState.PartlyAcknowledged
            : group.State == AlertState.Acknowledged ? OpenAlertState.Acknowledged
            : OpenAlertState.Active;
        return new(
            group.Key,
            new AlertKey(group.Primary.Source, group.Primary.Id),
            Clean(group.DisplayRuleName)!,
            group.Severity,
            state,
            group.FaultCount,
            group.AcknowledgedCount,
            group.IsSingle ? faults[0].Entity : null,
            Utc(group.FirstSeen),
            Utc(group.LastChanged),
            string.IsNullOrWhiteSpace(group.Primary.Note) ? null : Clean(group.Primary.Note),
            faults,
            Math.Max(0, group.FaultCount - MaxFaults),
            Clean(device is not null ? device.Hostname : group.Primary.Hostname),
            device is null ? null : DeviceSummary.Redacted(device, options, redactor));
    }

    private string? Clean(string? text) => text is null ? null : redactor.Redact(text);

    private DateTime? Utc(DateTime? value) => value is { } v ? ServerTime.ToLocal(v, options.ServerTimestampsAreUtc).ToUniversalTime() : null;

    /// <summary>The one severity vocabulary, shared with MCP's alert tools so their errors read alike.</summary>
    internal static AlertSeverity? ParseSeverity(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" or "any" => null,
        "critical" => AlertSeverity.Critical,
        "warning" => AlertSeverity.Warning,
        "ok" => AlertSeverity.Ok,
        _ => throw new ToolException("Severity must be critical, warning or ok, or left out for all."),
    };
}
