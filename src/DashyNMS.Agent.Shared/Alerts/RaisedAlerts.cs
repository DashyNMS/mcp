using System.Text.Json.Serialization;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Events;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Alerts;

public sealed record RaisedAlertsRequest(int? DeviceId = null, string? Severity = null, string? Group = null, string? Location = null,
    int? Hours = null, string? Since = null, string? Until = null, int Offset = 0, int Limit = 50);
public sealed record RaisedAlertRow([property: JsonIgnore] int RuleId, string Rule, string Severity, DateTime RaisedAt,
    DateTime? RecoveredAt, long? LastedMinutes, bool? StillOpen, int? FaultCount, bool? RecoveryTimeUnknown);
public sealed record RaisedDeviceGroup(int DeviceId, string? Hostname, string? Display, string? DeviceStatus, IReadOnlyList<RaisedAlertRow> Alerts);
public sealed record RaisedAlertsData(EventWindowInfo Window, IReadOnlyList<RaisedDeviceGroup> Devices, PageInfo Page);
public sealed record RaisedAlertsResult(string Summary, RaisedAlertsData Data, bool RulesUnavailable, bool OpenStatusUnavailable);

/// <summary>Historical alert occurrences, including recovered ones, shared with MCP.</summary>
public sealed class RaisedAlerts(ILibreNmsClient client, DeviceDirectory directory, DeviceScope scopes,
    AgentOptions options, TimeProvider time, Redactor redactor)
{
    public const int MaxAlerts = 100;
    public static RaisedAlerts Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        return new(client, directory, new DeviceScope(client, directory, time), options, time, new Redactor());
    }
    public async Task<RaisedAlertsResult> ReadAsync(RaisedAlertsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (deviceId, severityText, group, location, hours, since, until, offset, limit) = request;
        if (deviceId is <= 0) throw new ToolException("A device ID must be positive.");
        var severity = OpenAlerts.ParseSeverity(severityText);
        Paging.Check(offset, limit, MaxAlerts);
        var window = TimeWindow.Resolve(since, until, hours, DeviceEventHistory.DefaultHours, time);
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);

        var log = DeviceTimeline.ReadBackAsync(
            depth => client.Logs.ListAlertLogAsync(deviceId, depth, cancellationToken),
            a => Sources.Utc(a.TimeLogged, options),
            window,
            cancellationToken);
        var rules = Sources.TryAsync(() => client.Rules.ListAsync(cancellationToken), cancellationToken);
        var open = Sources.TryAsync(() => client.Alerts.ListAsync(AlertQuery.Open, cancellationToken), cancellationToken);
        await Task.WhenAll(log, rules, open).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (log.Result.Error is { } logError)
        {
            throw new ToolException(logError);
        }

        var ruleById = rules.Result.Value?.ToDictionary(r => r.Id) ?? [];
        var openKeys = open.Result.Value?.Select(a => (a.DeviceId, a.RuleId)).ToHashSet();

        var raised = AlertOccurrences.From(log.Result.Value!.Items, options)
            .Where(o => o.Started is { } started && window.Contains(started))
            .Where(o => deviceId is null || o.DeviceId == deviceId)
            .Where(o => scope.Includes(o.DeviceId, devices))
            .Select(o =>
            {
                var rule = ruleById.GetValueOrDefault(o.RuleId);
                return new Raised(
                    o.DeviceId, o.RuleId,
                    redactor.Redact(string.IsNullOrWhiteSpace(rule?.Name) ? $"Rule {o.RuleId}" : rule.Name),
                    rule?.Severity ?? AlertSeverity.Unknown,
                    o.Started!.Value,
                    o.Ended,
                    openKeys is null ? null : o.IsOpen && openKeys.Contains((o.DeviceId, o.RuleId)),
                    o.FaultCount,
                    o.RecoveryUnseen);
            })
            .Where(r => severity is null || r.Severity == severity)
            .OrderByDescending(r => r.RaisedAt)
            .ToList();
        var shown = Paging.Apply(raised, offset, limit, out var page);
        var groups = shown.GroupBy(r => r.DeviceId).Select(g =>
        {
            var device = devices.GetValueOrDefault(g.Key);
            return new RaisedDeviceGroup(g.Key, Clean(device?.Hostname), Clean(DeviceSummary.DisplayOf(device)),
                device is null ? null : DeviceSummary.StatusOf(device),
                g.Select(r => new RaisedAlertRow(r.RuleId, r.Rule, r.Severity.ToString().ToLowerInvariant(), r.RaisedAt, r.RecoveredAt,
                    r.RecoveredAt is { } end ? (long?)(end - r.RaisedAt).TotalMinutes : null, r.StillOpen,
                    r.FaultCount > 1 ? r.FaultCount : null, r.RecoveryUnseen ? true : null)).ToList());
        }).ToList();

        var covered = log.Result.Value.CoveredSince;
        var complete = covered <= window.Since;
        var stillOpen = raised.Count(r => r.StillOpen == true);
        var critical = raised.Count(r => r.Severity == AlertSeverity.Critical);
        var warning = raised.Count(r => r.Severity == AlertSeverity.Warning);
        var lead = EventWindowText.Describe(window, time.GetUtcNow().UtcDateTime);
        var summary = raised.Count == 0
            ? $"{lead}: no alerts raised" + (severity is not null || deviceId is not null || !scope.IsEverything ? " that match." : ".")
            : $"{lead}: {DeviceHealth.Count(raised.Count, "alert")} raised{FaultNote(raised)} on {DeviceHealth.Count(raised.Select(r => r.DeviceId).Distinct().Count(), "device")} " +
              $"({critical} critical, {warning} warning); {stillOpen} still open.";
        summary += complete ? string.Empty : $" LibreNMS's alert log only reaches back to {covered:yyyy-MM-dd HH:mm} UTC, so the start of the window isn't covered.";
        summary += PagingNote(page);

        return new(summary, new RaisedAlertsData(new(window.Since, window.Until, complete, complete ? null : covered), groups, page),
            rules.Result.Error is not null, open.Result.Error is not null);
    }

    /// <param name="FaultCount">How many faults went into alert during it - 1 before LibreNMS 26.10.</param>
    /// <param name="RecoveryUnseen">A legacy alert fired again with no recovery logged between: it ended, but when isn't known.</param>
    private sealed record Raised(int DeviceId, int RuleId, string Rule, AlertSeverity Severity, DateTime RaisedAt, DateTime? RecoveredAt, bool? StillOpen, int FaultCount, bool RecoveryUnseen);

    /// <summary>" (14 faults)" when the alerts raised held more faults than alerts.</summary>
    private static string FaultNote(IReadOnlyCollection<Raised> raised)
    {
        var faults = raised.Sum(r => r.FaultCount);
        return faults > raised.Count ? $" ({faults} faults)" : string.Empty;
    }

    private static string PagingNote(PageInfo page) => page.NextOffset is not null || page.Offset > 0 ? " " + Paging.Describe(page, "alert", "alerts") : string.Empty;
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
}
