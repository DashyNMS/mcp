using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>Why an alert fired (#27), and what each alert rule watches for (#28).</summary>
[McpServerToolType]
public sealed class RuleTools(
    ILibreNmsClient client,
    FleetCache fleet,
    DeviceDirectory directory,
    DeviceScope scopes,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int MaxFaults = 20;
    public const int MaxFaultFields = 12;
    public const int RecentFirings = 5;
    public const int DefaultRules = 50;
    public const int MaxRules = 100;
    public const int HistoryDepth = 200;
    public static readonly TimeSpan HistoryPeriod = AlertHistory.DefaultPeriod;
    public static readonly TimeSpan RecentlyRaised = TimeSpan.FromDays(7);

    [McpServerTool(Name = "alert_details", Title = "Alert details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Everything about one alert: why it fired (the ports, sensors or other things the rule matched, with the values that tripped it), the rule's " +
        "name, severity, condition, procedure link and notes, its state, acknowledgement note and how long it's been open, and how often the same rule " +
        "has fired on the same device in the last 30 days, with the most recent times - to tell a one-off from a flapping alert. " +
        "Takes an alert ID, which active_alerts gives for open alerts. On LibreNMS 26.10 and later that's a fault ID: the details are that fault's own " +
        "(never another core's or port's), and group lists every fault the same rule has open on the device.")]
    public Task<string> AlertDetails(
        [Description("The LibreNMS alert ID - on 26.10 and later, a fault ID - from active_alerts (alert_id, or a fault_id from its faults).")] int alert_id,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("alert_details", ct => DetailsAsync(alert_id, ct), cancellationToken);

    [McpServerTool(Name = "alert_rules", Title = "Alert rules", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "LibreNMS's alert rules: each one's name, severity, whether it's enabled, its condition, which devices, device groups and locations it applies to " +
        "(or all), its alert template, how many alerts it has open now and how many it raised in the last 7 days. For \"what are we alerting on?\", " +
        "\"why didn't X alert?\" or \"which rules are noisiest?\". Filter by part of the name or by severity; sort by name, open or raised. " +
        "Paged: 50 by default, up to 100. Read-only: rules can only be changed in LibreNMS.")]
    public Task<string> AlertRules(
        [Description("Only rules whose name contains this.")] string? name = null,
        [Description("Only rules of this severity: critical, warning or ok.")] string? severity = null,
        [Description("Include disabled rules (by default only enabled ones are listed).")] bool include_disabled = false,
        [Description("Sort by name (the default), open (most open alerts first) or raised (most raised in the last 7 days first).")] string? sort = null,
        [Description("How many rules to skip, for paging. Start at 0, then use page.next_offset.")] int offset = 0,
        [Description("How many rules to return, 1 to 100. Defaults to 50.")] int limit = DefaultRules,
        [Description("Include each rule's notes and procedure link.")] bool verbose = false,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("alert_rules", ct => RulesAsync(name, severity, include_disabled, sort, offset, limit, verbose, ct), cancellationToken);

    /// <summary>
    /// One alert or fault by ID, in any state. LibreNMS's alerts/{id} (and
    /// faults/{id}) filters by state like the list, answering only for an active
    /// one unless asked (checked live in #13's trial), so every state is asked for.
    /// </summary>
    private async Task<Alert?> FindAlertAsync(int alertId, CancellationToken cancellationToken)
    {
        var found = await client.Alerts
            .ListAsync(new AlertQuery { Id = alertId, States = AlertQuery.All.States, OrderBy = null }, cancellationToken)
            .ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }
    private async Task<JsonObject> DetailsAsync(int alertId, CancellationToken cancellationToken)
    {
        var alert = await FindAlertAsync(alertId, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException("No alert has that ID. It may have been cleared and removed; active_alerts lists the current ones.");

        var rulesTask = Sources.TryAsync(() => fleet.GetRulesAsync(cancellationToken), cancellationToken);
        var history = Sources.TryAsync(() => client.Logs.ListAlertLogAsync(alert.DeviceId, HistoryDepth, cancellationToken), cancellationToken);
        var siblings = Sources.TryAsync(() => client.Alerts.ListAsync(new AlertQuery { RuleId = alert.RuleId, States = AlertQuery.Open.States }, cancellationToken), cancellationToken);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        await Task.WhenAll(rulesTask, history, siblings, devicesTask).ConfigureAwait(false);

        var unavailable = new List<Unavailable>();
        foreach (var (source, error) in new[] { ("rule", rulesTask.Result.Error), ("history", history.Result.Error), ("group", siblings.Result.Error) })
        {
            if (error is not null)
            {
                unavailable.Add(new Unavailable(source, error));
            }
        }

        var rule = rulesTask.Result.Value?.FirstOrDefault(r => r.Id == alert.RuleId);
        // The fault's own details (26.10+), or its own log entry - never the
        // newest entry for the rule, which can be another core's (#75).
        var detail = AlertFaultDetails.Resolve(alert, history.Result.Value ?? [], AlertRuleConditions.ExtractFields(rule));

        // Every fault the rule has open on this device, this one included.
        var group = AlertGrouping.ByDeviceAndRule((siblings.Result.Value ?? [])
                .Where(a => a.DeviceId == alert.DeviceId)
                .Append(alert)
                .Where(a => a.State != AlertState.Recovered)
                .GroupBy(a => a.Identity)
                .Select(g => g.First()))
            .FirstOrDefault();
        var device = devicesTask.Result.FirstOrDefault(d => d.DeviceId == alert.DeviceId);
        var now = time.GetUtcNow().UtcDateTime;
        var summary = AlertSummary.From(alert, options);

        // Earlier firings of this rule on this device: only the times it went
        // into alert count, and the newest is this alert itself (as Core's
        // AlertHistory counts them for the apps).
        var firings = (history.Result.Value ?? [])
            .Where(e => e.RuleId == alert.RuleId && e.DeviceId == alert.DeviceId && e.State == AlertState.Active)
            .Select(e => Sources.Utc(e.TimeLogged, options))
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .OrderByDescending(t => t)
            .Skip(1)
            .Where(t => now - t <= HistoryPeriod)
            .ToList();

        var faults = ToolResult.Take(detail.Faults, MaxFaults, out var omittedFaults);
        var data = new
        {
            alert = new
            {
                alert_id = alert.Id,
                entity = AlertGrouping.EntityName(alert),
                state = summary.State,
                severity = summary.Severity,
                since = summary.Since,
                open_minutes = summary.Since is { } started ? (long?)Math.Max(0, (long)(now - started).TotalMinutes) : null,
                note = summary.Note,
            },
            group = group is null || group.IsSingle ? null : AlertGroupSummary.From(group, options),
            device = device is null ? null : DeviceBrief.From(device),
            rule = new
            {
                rule_id = alert.RuleId,
                name = alert.DisplayRuleName,
                severity = (rule?.Severity ?? alert.Severity).ToString().ToLowerInvariant(),
                condition = Condition(rule),
                procedure = string.IsNullOrWhiteSpace(rule?.Procedure ?? alert.Procedure) ? null : rule?.Procedure ?? alert.Procedure,
                notes = string.IsNullOrWhiteSpace(rule?.Notes ?? alert.RuleNotes) ? null : rule?.Notes ?? alert.RuleNotes,
            },
            why = new
            {
                matched = detail.Faults.Count,
                recorded_at = Sources.Utc(detail.RecordedAt, options),
                rule_condition_known = detail.RuleConditionKnown,
                faults = faults.Select(f => new
                {
                    what = f.Title,
                    trigger_values = f.PrimaryFields.Take(MaxFaultFields).Select(x => new { field = x.Name, value = x.Value }).ToList(),
                }).ToList(),
                faults_omitted = omittedFaults > 0 ? omittedFaults : (int?)null,
            },
            history = new
            {
                fired_before_in_30_days = firings.Count,
                recent = firings.Take(RecentFirings).ToList(),
            },
            unavailable = unavailable.Count > 0 ? unavailable : null,
        };

        var times = firings.Count switch
        {
            0 => "the first time this rule has fired on this device in 30 days",
            1 => "it fired once before in the last 30 days",
            _ => $"it fired {firings.Count} times before in the last 30 days",
        };
        var matched = detail.Faults.Count == 0 ? string.Empty : $", matching {DeviceTools.Count(detail.Faults.Count, "item")}";
        var ofGroup = group is { IsSingle: false } ? $"; one of {group.FaultCount} faults this rule has open on the device" : string.Empty;
        return ToolResult.Ok($"A {summary.Severity} {summary.State} alert{matched}{ofGroup}; {times}.", data);
    }

    private async Task<JsonObject> RulesAsync(string? name, string? severityText, bool includeDisabled, string? sortText, int offset, int limit, bool verbose, CancellationToken cancellationToken)
    {
        Paging.Check(offset, limit, MaxRules);
        var severity = severityText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "any" or "all" => (AlertSeverity?)null,
            "critical" => AlertSeverity.Critical,
            "warning" => AlertSeverity.Warning,
            "ok" => AlertSeverity.Ok,
            _ => throw new ToolException("Severity must be critical, warning or ok."),
        };
        var sort = sortText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "name" => "name",
            "open" => "open",
            "raised" or "noisiest" => "raised",
            _ => throw new ToolException("Sort must be name, open or raised."),
        };

        var now = time.GetUtcNow().UtcDateTime;
        var recent = new TimeWindow(now - RecentlyRaised, now);
        var rulesTask = fleet.GetRulesAsync(cancellationToken);
        var templates = Sources.TryAsync(() => fleet.GetTemplatesAsync(cancellationToken), cancellationToken);
        var open = Sources.TryAsync(() => client.Alerts.ListAsync(AlertQuery.Open, cancellationToken), cancellationToken);
        var raisedLog = DeviceTimeline.ReadBackAsync(depth => client.Logs.ListAlertLogAsync(null, depth, cancellationToken), a => Sources.Utc(a.TimeLogged, options), recent, cancellationToken);
        var groups = Sources.TryAsync(() => scopes.ListGroupsAsync(cancellationToken), cancellationToken);
        var locations = Sources.TryAsync(() => fleet.GetLocationsAsync(cancellationToken), cancellationToken);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        await Task.WhenAll(rulesTask, templates, open, raisedLog, groups, locations, devicesTask).ConfigureAwait(false);

        var unavailable = new List<Unavailable>();
        foreach (var (source, error) in new[] { ("templates", templates.Result.Error), ("open alerts", open.Result.Error), ("alert log", raisedLog.Result.Error), ("device groups", groups.Result.Error), ("locations", locations.Result.Error) })
        {
            if (error is not null)
            {
                unavailable.Add(new Unavailable(source, error));
            }
        }

        var openByRule = open.Result.Value?.GroupBy(a => a.RuleId).ToDictionary(g => g.Key, g => g.Count());
        var raisedByRule = raisedLog.Result.Value?.Items
            .Where(e => e.State == AlertState.Active && Sources.Utc(e.TimeLogged, options) is { } at && recent.Contains(at))
            .GroupBy(e => e.RuleId)
            .ToDictionary(g => g.Key, g => g.Count());
        var raisedComplete = raisedLog.Result.Value is { } read && read.CoveredSince <= recent.Since;
        var templateByRule = templates.Result.Value?
            .SelectMany(t => t.AlertRules.Select(r => (Rule: r, Template: t.Name)))
            .GroupBy(x => x.Rule)
            .ToDictionary(g => g.Key, g => g.First().Template);
        var groupNames = groups.Result.Value?.ToDictionary(g => g.Id, g => g.Name) ?? [];
        var locationNames = locations.Result.Value?.ToDictionary(l => l.Id, l => l.Name) ?? [];
        var hostnames = devicesTask.Result.ToDictionary(d => d.DeviceId, d => d.Hostname);

        var rules = rulesTask.Result
            .Where(r => includeDisabled || !r.Disabled)
            .Where(r => name is null || r.Name?.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase) == true)
            .Where(r => severity is null || r.Severity == severity)
            .Select(r => new
            {
                Rule = r,
                Open = openByRule?.GetValueOrDefault(r.Id),
                Raised = raisedByRule?.GetValueOrDefault(r.Id),
            })
            .ToList();

        var sorted = sort switch
        {
            "open" => rules.OrderByDescending(r => r.Open ?? 0).ThenBy(r => r.Rule.Name, StringComparer.OrdinalIgnoreCase),
            "raised" => rules.OrderByDescending(r => r.Raised ?? 0).ThenBy(r => r.Rule.Name, StringComparer.OrdinalIgnoreCase),
            _ => rules.OrderBy(r => r.Rule.Name, StringComparer.OrdinalIgnoreCase),
        };

        var shaped = sorted.Select(x =>
        {
            var r = x.Rule;
            var everywhere = r.Devices.Count == 0 && r.Groups.Count == 0 && r.Locations.Count == 0;
            return new
            {
                rule_id = r.Id,
                name = r.Name ?? $"Rule {r.Id}",
                severity = r.Severity.ToString().ToLowerInvariant(),
                enabled = !r.Disabled,
                condition = Condition(r),
                applies_to = everywhere
                    ? (object)"all devices"
                    : new
                    {
                        except = r.InvertMap ? true : (bool?)null,
                        devices = r.Devices.Count > 0 ? r.Devices.Select(id => hostnames.GetValueOrDefault(id) ?? $"device {id}").ToList() : null,
                        groups = r.Groups.Count > 0 ? r.Groups.Select(id => groupNames.GetValueOrDefault(id) ?? $"group {id}").ToList() : null,
                        locations = r.Locations.Count > 0 ? r.Locations.Select(id => locationNames.GetValueOrDefault(id) ?? $"location {id}").ToList() : null,
                    },
                template = templateByRule is null ? null : templateByRule.GetValueOrDefault(r.Id) ?? "default",
                open_alerts = x.Open,
                raised_last_7_days = x.Raised,
                notes = verbose && !string.IsNullOrWhiteSpace(r.Notes) ? r.Notes : null,
                procedure = verbose && !string.IsNullOrWhiteSpace(r.Procedure) ? r.Procedure : null,
            };
        }).ToList();

        var page = Paging.Apply(shaped, offset, limit, out var info);
        var summary = $"{DeviceTools.Count(rules.Count, "rule")}" + (includeDisabled ? string.Empty : " enabled") +
            (name is null && severity is null ? "." : " match.") +
            (raisedComplete || raisedByRule is null ? string.Empty : " The 7-day raised counts only cover as far back as LibreNMS's alert log reaches.") +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "rule", "rules") : string.Empty);

        return ToolResult.Ok(summary, new { rules = page, page = info, unavailable = unavailable.Count > 0 ? unavailable : null });
    }

    /// <summary>The condition as LibreNMS shows it: its own text when stored, otherwise built from the rule's builder JSON (Core's formatter).</summary>
    private static string? Condition(AlertRule? rule)
        => rule is null ? null
            : !string.IsNullOrWhiteSpace(rule.Rule) ? rule.Rule
            : AlertRuleSqlFormatter.Format(rule.Builder);
}
