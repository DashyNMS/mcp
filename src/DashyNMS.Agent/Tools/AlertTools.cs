using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>What is alerting right now, or what alerted in a window, across the estate.</summary>
[McpServerToolType]
public sealed class AlertTools(
    ILibreNmsClient client,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int DefaultAlerts = 50;
    public const int MaxAlerts = 100;

    [McpServerTool(Name = "active_alerts", Title = "Active alerts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Alerts in LibreNMS, grouped by device, with rule name, severity and age. With no time window: the alerts open now (firing or " +
        "acknowledged), worst first, for \"what's alerting?\" or \"is anything critical?\". On LibreNMS 26.10 and later one alert is a rule firing on a " +
        "device, and may hold several faults - one per core, port or sensor it matched: fault_count says how many, faults lists them, and totals counts " +
        "alerts and faults separately. alert_id is the first fault's ID, for alert_details; group_id names the device and rule. With a window (hours, or since/until): every alert raised in it, " +
        "including ones that have since recovered, each with still_open and recovered_at - for \"what fired overnight?\" or \"what alerted between " +
        "Friday and Monday?\". " + EventTools.WindowHelp + " Filter by severity, device, device group or location. " +
        "Paged: 50 by default, up to 100; use page.next_offset for more. Records are short unless verbose is true.")]
    public Task<string> ActiveAlerts(
        [Description("Only alerts of this severity: critical, warning or ok. Leave out for all.")] string? severity = null,
        [Description("Only alerts on this device: hostname, display name, IP address or LibreNMS device ID. Leave out for every device.")] string? device = null,
        [Description("Only devices in this LibreNMS device group (by name; list_groups_and_locations shows them).")] string? group = null,
        [Description("Only devices at this location (by name; list_groups_and_locations shows them).")] string? location = null,
        [Description("Alerts raised in the last this many hours, 1 to 168. Leave out for alerts open now.")] int? hours = null,
        [Description("Alerts raised since: a duration before now (90m, 24h, 7d) or an ISO 8601 time. Leave out for alerts open now.")] string? since = null,
        [Description("Alerts raised until: a duration before now or an ISO 8601 time. Defaults to now.")] string? until = null,
        [Description("How many alerts to skip, for paging. Start at 0, then use page.next_offset.")] int offset = 0,
        [Description("How many alerts to return, 1 to 100. Defaults to 50.")] int limit = DefaultAlerts,
        [Description("Include each alert's start time and note.")] bool verbose = false,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "active_alerts",
            ct => hours is null && string.IsNullOrWhiteSpace(since) && string.IsNullOrWhiteSpace(until)
                ? OpenAsync(severity, device, group, location, offset, limit, verbose, ct)
                : RaisedAsync(severity, device, group, location, TimeWindow.Resolve(since, until, hours, EventTools.DefaultHours, time), offset, limit, verbose, ct),
            cancellationToken);

    /// <summary>
    /// The alerts open now, one per rule firing on a device (#75): on LibreNMS
    /// 26.10 that's every fault it keeps for them - eight busy cores are one
    /// group of eight faults, not eight alerts. Paged by group, in Core's
    /// stable order (worst, then most recently changed), so pages don't shuffle.
    /// </summary>
    private async Task<JsonObject> OpenAsync(string? severityText, string? deviceQuery, string? group, string? location, int offset, int limit, bool verbose, CancellationToken cancellationToken)
    {
        var (severity, deviceId, scope, devices) = await PrepareAsync(severityText, deviceQuery, group, location, offset, limit, cancellationToken).ConfigureAwait(false);

        var alerts = await client.Alerts.ListAsync(AlertQuery.Open, cancellationToken).ConfigureAwait(false);
        var matching = AlertGrouping.ByDeviceAndRule(alerts
                .Where(a => deviceId is null || a.DeviceId == deviceId)
                .Where(a => scope.Includes(a.DeviceId, devices)))
            .Where(g => severity is null || g.Severity == severity)
            .ToList();

        var now = time.GetUtcNow().UtcDateTime;
        var totals = OpenAlertTotals.From(matching);
        var filtered = severity is not null || deviceId is not null || !scope.IsEverything;

        // A page must fit the result cap whole (#80): cut by the cap, the
        // alerts left off would be skipped by next_offset. So a page that's too
        // big is shortened, and next_offset follows what was really returned.
        var take = limit;
        while (true)
        {
            var shown = Paging.Apply(matching, offset, take, out var page);
            var summary = matching.Count == 0
                ? "No open alerts visible to this account" + (filtered ? " match." : ".")
                : $"{totals.Describe()} on {DeviceTools.Count(totals.Devices, "device")} ({totals.Critical} critical, {totals.Warning} warning)." +
                  PagingNote(page);
            var result = ToolResult.Ok(summary, new { totals, devices = Shape(shown, devices, now, verbose), page });

            if (take <= 1 || ToolResult.Fits(result, options.MaxResultBytes))
            {
                return result;
            }

            take = Math.Max(1, take * 3 / 4);
        }
    }

    /// <summary>
    /// A page of alert groups by device. Without verbose a group lists its
    /// first few faults by name and state only; verbose gives up to
    /// <see cref="AlertGroupSummary.MaxFaults"/> with their entity IDs and times.
    /// </summary>
    private object Shape(IReadOnlyList<AlertGroup> shown, IReadOnlyDictionary<int, Device> devices, DateTime now, bool verbose)
        => shown
            .GroupBy(g => g.DeviceId)
            .Select(g => new
            {
                device_id = g.Key,
                hostname = devices.TryGetValue(g.Key, out var d) ? d.Hostname : g.First().Primary.Hostname,
                display = DeviceSummary.DisplayOf(d),
                device_status = d is not null ? DeviceSummary.StatusOf(d) : null,
                alerts = g.Select(alertGroup =>
                {
                    var alert = AlertGroupSummary.From(alertGroup, options);
                    var listed = alert.Faults?.Take(verbose ? AlertGroupSummary.MaxFaults : BriefFaults).ToList();
                    return new
                    {
                        alert.GroupId,
                        alert.AlertId,
                        alert.Rule,
                        alert.Severity,
                        alert.State,
                        alert.FaultCount,
                        acknowledged_count = alert.AcknowledgedCount > 0 && alert.FaultCount > 1 ? (int?)alert.AcknowledgedCount : null,
                        alert.Entity,
                        since = verbose ? alert.Since : null,
                        age_minutes = alert.Since is { } started ? (long?)Math.Max(0, (long)(now - started).TotalMinutes) : null,
                        note = verbose ? alert.Note : null,
                        faults = listed?.Select(f => verbose
                            ? (object)f
                            : new { f.FaultId, f.Entity, f.State }).ToList(),
                        faults_omitted = listed is null || alert.FaultCount <= listed.Count ? (int?)null : alert.FaultCount - listed.Count,
                    };
                }).ToList(),
            })
            .ToList();

    /// <summary>Faults listed per group without verbose; alert_details or verbose lists more.</summary>
    public const int BriefFaults = 5;
    /// <summary>
    /// Alerts raised in a window, from the alert log (#76): one per occurrence
    /// of a rule on a device - from its first fault going into alert to its
    /// last recovering, each recovery matched to its own fault - so eight cores
    /// firing together are one raise, and one core recovering doesn't close
    /// it. Matched to the open alerts to say whether it's still going.
    /// </summary>
    private async Task<JsonObject> RaisedAsync(string? severityText, string? deviceQuery, string? group, string? location, TimeWindow window, int offset, int limit, bool verbose, CancellationToken cancellationToken)
    {
        var (severity, deviceId, scope, devices) = await PrepareAsync(severityText, deviceQuery, group, location, offset, limit, cancellationToken).ConfigureAwait(false);

        var log = DeviceTimeline.ReadBackAsync(
            depth => client.Logs.ListAlertLogAsync(deviceId, depth, cancellationToken),
            a => Sources.Utc(a.TimeLogged, options),
            window,
            cancellationToken);
        var rules = Sources.TryAsync(() => client.Rules.ListAsync(cancellationToken), cancellationToken);
        var open = Sources.TryAsync(() => client.Alerts.ListAsync(AlertQuery.Open, cancellationToken), cancellationToken);
        await Task.WhenAll(log, rules, open).ConfigureAwait(false);

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
                    o.DeviceId,
                    string.IsNullOrWhiteSpace(rule?.Name) ? $"Rule {o.RuleId}" : rule.Name,
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
        var groups = shown
            .GroupBy(r => r.DeviceId)
            .Select(g => new
            {
                device_id = g.Key,
                hostname = devices.TryGetValue(g.Key, out var d) ? d.Hostname : null,
                display = DeviceSummary.DisplayOf(d),
                device_status = d is not null ? DeviceSummary.StatusOf(d) : null,
                alerts = g.Select(r => new
                {
                    rule = r.Rule,
                    severity = r.Severity.ToString().ToLowerInvariant(),
                    raised_at = r.RaisedAt,
                    recovered_at = r.RecoveredAt,
                    lasted_minutes = r.RecoveredAt is { } end ? (long?)(end - r.RaisedAt).TotalMinutes : null,
                    still_open = r.StillOpen,
                    fault_count = r.FaultCount > 1 ? (int?)r.FaultCount : null,
                    recovery_time_unknown = r.RecoveryUnseen ? true : (bool?)null,
                }).ToList(),
            })
            .ToList();

        var covered = log.Result.Value.CoveredSince;
        var complete = covered <= window.Since;
        var stillOpen = raised.Count(r => r.StillOpen == true);
        var critical = raised.Count(r => r.Severity == AlertSeverity.Critical);
        var warning = raised.Count(r => r.Severity == AlertSeverity.Warning);
        var lead = EventTools.Describe(window, time.GetUtcNow().UtcDateTime);
        var summary = raised.Count == 0
            ? $"{lead}: no alerts raised" + (severity is not null || deviceId is not null || !scope.IsEverything ? " that match." : ".")
            : $"{lead}: {DeviceTools.Count(raised.Count, "alert")} raised{FaultNote(raised)} on {DeviceTools.Count(raised.Select(r => r.DeviceId).Distinct().Count(), "device")} " +
              $"({critical} critical, {warning} warning); {stillOpen} still open.";
        summary += complete ? string.Empty : $" LibreNMS's alert log only reaches back to {covered:yyyy-MM-dd HH:mm} UTC, so the start of the window isn't covered.";
        summary += PagingNote(page);

        var data = new
        {
            window = new
            {
                since = window.Since,
                until = window.Until,
                window_complete = complete,
                covered_since = complete ? (DateTime?)null : covered,
            },
            devices = groups,
            page,
        };

        return ToolResult.Ok(summary, data);
    }

    /// <param name="FaultCount">How many faults went into alert during it - 1 before LibreNMS 26.10.</param>
    /// <param name="RecoveryUnseen">A legacy alert fired again with no recovery logged between: it ended, but when isn't known.</param>
    private sealed record Raised(int DeviceId, string Rule, AlertSeverity Severity, DateTime RaisedAt, DateTime? RecoveredAt, bool? StillOpen, int FaultCount, bool RecoveryUnseen);

    private async Task<(AlertSeverity? Severity, int? DeviceId, Scope Scope, IReadOnlyDictionary<int, Device> Devices)> PrepareAsync(
        string? severityText, string? deviceQuery, string? group, string? location, int offset, int limit, CancellationToken cancellationToken)
    {
        var severity = ParseSeverity(severityText);
        Paging.Check(offset, limit, MaxAlerts);
        int? deviceId = string.IsNullOrWhiteSpace(deviceQuery)
            ? null
            : (await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false)).DeviceId;
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);
        return (severity, deviceId, scope, devices);
    }

    /// <summary>" (14 faults)" when the alerts raised held more faults than alerts.</summary>
    private static string FaultNote(IReadOnlyCollection<Raised> raised)
    {
        var faults = raised.Sum(r => r.FaultCount);
        return faults > raised.Count ? $" ({faults} faults)" : string.Empty;
    }

    private static string PagingNote(PageInfo page)
        => page.NextOffset is not null || page.Offset > 0 ? " " + Paging.Describe(page, "alert", "alerts") : string.Empty;

    private static AlertSeverity? ParseSeverity(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" or "any" => null,
        "critical" => AlertSeverity.Critical,
        "warning" => AlertSeverity.Warning,
        "ok" => AlertSeverity.Ok,
        _ => throw new ToolException("Severity must be critical, warning or ok, or left out for all."),
    };
}
