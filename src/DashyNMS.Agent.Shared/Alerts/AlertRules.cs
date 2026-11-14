using System.Text.Json.Serialization;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Alerts;

public sealed record AlertDetailsRequest(int AlertId, AlertSource? Source = null);
public sealed record AlertRulesRequest(string? Name = null, string? Severity = null, bool IncludeDisabled = false,
    string? Sort = null, int Offset = 0, int Limit = 50, bool Verbose = false);
public sealed record DetailAlert(int AlertId, string Source, string? Entity, string State, string Severity, DateTime? Since,
    long? OpenMinutes, string? Note);
public sealed record DetailRule(int RuleId, string Name, string Severity, string? Condition, string? Procedure, string? Notes);
public sealed record TriggerValue(string Field, string Value);
public sealed record DetailFault(string What, IReadOnlyList<TriggerValue> TriggerValues);
public sealed record DetailWhy(int Matched, DateTime? RecordedAt, bool RuleConditionKnown, IReadOnlyList<DetailFault> Faults, int? FaultsOmitted);
public sealed record DetailHistory([property: JsonPropertyName("fired_before_in_30_days")] int FiredBeforeIn30Days, bool? AtLeast, IReadOnlyList<DateTime> Recent);
public sealed record AlertDetailsData(DetailAlert Alert, AlertGroupSummary? Group, DeviceBrief? Device,
    DetailRule Rule, DetailWhy Why, DetailHistory History, IReadOnlyList<Unavailable>? Unavailable);
public sealed record AlertDetailsResult(string Summary, AlertDetailsData Data, AlertKey Identity);
public sealed record RuleTargets(bool? Except, IReadOnlyList<string>? Devices, IReadOnlyList<string>? Groups, IReadOnlyList<string>? Locations);
public sealed record RuleApplicability(bool AllDevices, RuleTargets? Targets);
public sealed record RemovedRuleColumn(string Column, string RemovedIn, string? Replacement, string Advice);
public sealed record AlertRuleRow(int RuleId, string Name, string Severity, bool Enabled, string? Condition,
    RuleApplicability AppliesTo, string? Template, bool? NotifyPerEntity, int? MaxEntities, bool? MaxEntitiesInherited,
    string? Acknowledging, IReadOnlyList<RemovedRuleColumn>? RemovedColumns, int? OpenAlerts, [property: JsonPropertyName("raised_last_7_days")] int? RaisedLast7Days,
    string? Notes, string? Procedure);
public sealed record AlertRulesData(IReadOnlyList<AlertRuleRow> Rules, PageInfo Page, IReadOnlyList<Unavailable>? Unavailable);
public sealed record AlertRulesResult(string Summary, AlertRulesData Data);

/// <summary>Typed alert details and rule queries shared by MCP and app hosts.</summary>
/// <remarks>Use HostAlertRules to suppress source exception text and retire a connection generation.</remarks>
public sealed class AlertRules(ILibreNmsClient client, FleetCache fleet, DeviceDirectory directory, DeviceScope scopes,
    AgentOptions options, TimeProvider time, Redactor redactor)
{
    public const int MaxFaults = 20;
    public const int MaxFaultFields = 12;
    public const int RecentFirings = 5;
    public const int DefaultRules = 50;
    public const int MaxRules = 100;
    public const int HistoryDepth = 200;
    public static readonly TimeSpan HistoryPeriod = AlertHistory.DefaultPeriod;
    public static readonly TimeSpan RecentlyRaised = TimeSpan.FromDays(7);

    public static AlertRules Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        return new(client, new FleetCache(client, time), directory, new DeviceScope(client, directory, time), options, time, new Redactor());
    }

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
    public async Task<AlertDetailsResult> ReadDetailsAsync(AlertDetailsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AlertId <= 0) throw new ToolException("An alert ID must be positive.");
        var alert = await FindAlertAsync(request.AlertId, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException("No alert has that ID. It may have been cleared and removed; active_alerts lists the current ones.");

        if (alert.Id != request.AlertId || request.Source is { } expectedSource && alert.Source != expectedSource)
            throw new ToolException("No alert with that source and ID is visible.");
        var rulesTask = Sources.TryAsync(() => fleet.GetRulesAsync(cancellationToken), cancellationToken);
        var history = Sources.TryAsync(() => client.Logs.ListAlertLogAsync(alert.DeviceId, HistoryDepth, cancellationToken), cancellationToken);
        var siblings = Sources.TryAsync(() => client.Alerts.ListAsync(new AlertQuery { RuleId = alert.RuleId, States = AlertQuery.Open.States }, cancellationToken), cancellationToken);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        await Task.WhenAll(rulesTask, history, siblings, devicesTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

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
                .Where(a => a.DeviceId == alert.DeviceId && a.RuleId == alert.RuleId)
                .Append(alert)
                .Where(a => a.State != AlertState.Recovered)
                .GroupBy(a => a.Identity)
                .Select(g => g.First()))
            .FirstOrDefault();
        var device = devicesTask.Result.FirstOrDefault(d => d.DeviceId == alert.DeviceId);
        var now = time.GetUtcNow().UtcDateTime;
        var summary = AlertSummary.From(alert, options);

        // Earlier occurrences of this rule on this device (#76): eight cores
        // going into alert together are one, and the newest is this alert's own
        // (as Core's AlertHistory counts them for the apps).
        var log = history.Result.Value ?? [];
        var occurrences = AlertOccurrences.From(log, options)
            .Where(o => o.DeviceId == alert.DeviceId && o.RuleId == alert.RuleId)
            .ToList();
        var earlier = occurrences.Take(Math.Max(0, occurrences.Count - 1)).ToList();
        var firings = earlier
            .Where(o => o.Started is { } started && now - started <= HistoryPeriod)
            .Select(o => o.Started!.Value)
            .OrderByDescending(t => t)
            .ToList();

        // The log read may not reach back 30 days, or may start partway through one.
        var oldest = log.Select(e => Sources.Utc(e.TimeLogged, options)).Where(t => t is not null).DefaultIfEmpty(null).Min();
        var historyIncomplete = (log.Count >= HistoryDepth && oldest is { } first && now - first < HistoryPeriod)
            || earlier.Any(o => o.Started is null && (o.Ended is null || now - o.Ended <= HistoryPeriod));
        var faults = detail.Faults.Take(MaxFaults).ToList();
        var omittedFaults = Math.Max(0, detail.Faults.Count - faults.Count);
        var data = new AlertDetailsData(
            new DetailAlert(alert.Id, alert.Source == AlertSource.Fault ? "fault" : "alert",
                Clean(AlertGrouping.EntityName(alert)), summary.State, summary.Severity, summary.Since,
                summary.Since is { } started ? Math.Max(0, (long)(now - started).TotalMinutes) : null, Clean(summary.Note)),
            group is null || group.IsSingle ? null : CleanGroup(AlertGroupSummary.From(group, options)),
            device is null ? null : DeviceBrief.FromSummary(DeviceSummary.Redacted(device, options, redactor)),
            new DetailRule(alert.RuleId, redactor.Redact(alert.DisplayRuleName),
                (rule?.Severity ?? alert.Severity).ToString().ToLowerInvariant(), Clean(Condition(rule)),
                string.IsNullOrWhiteSpace(rule?.Procedure ?? alert.Procedure) ? null : Clean(rule?.Procedure ?? alert.Procedure),
                string.IsNullOrWhiteSpace(rule?.Notes ?? alert.RuleNotes) ? null : Clean(rule?.Notes ?? alert.RuleNotes)),
            new DetailWhy(detail.Faults.Count, Sources.Utc(detail.RecordedAt, options), detail.RuleConditionKnown,
                faults.Select(f => new DetailFault(redactor.Redact(f.Title), f.PrimaryFields.Take(MaxFaultFields)
                    .Select(x => new TriggerValue(redactor.Redact(x.Name), redactor.Redact(x.Value))).ToList())).ToList(),
                omittedFaults > 0 ? omittedFaults : null),
            new DetailHistory(firings.Count, historyIncomplete ? true : null, firings.Take(RecentFirings).ToList()),
            unavailable.Count > 0 ? unavailable : null);

        var times = (firings.Count, historyIncomplete) switch
        {
            (0, true) => "no earlier firing in the alert log LibreNMS returned, which doesn't cover the whole 30 days",
            (0, _) => "the first time this rule has fired on this device in 30 days",
            (1, true) => "it fired at least once before in the last 30 days",
            (1, _) => "it fired once before in the last 30 days",
            (_, true) => $"it fired at least {firings.Count} times before in the last 30 days",
            _ => $"it fired {firings.Count} times before in the last 30 days",
        };
        var matched = detail.Faults.Count == 0 ? string.Empty : $", matching {Devices.DeviceHealth.Count(detail.Faults.Count, "item")}";
        var ofGroup = group is { IsSingle: false } ? $"; one of {group.FaultCount} faults this rule has open on the device" : string.Empty;
        return new($"A {summary.Severity} {summary.State} alert{matched}{ofGroup}; {times}.", data, alert.Identity);
    }

    public async Task<AlertRulesResult> ReadRulesAsync(AlertRulesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (name, severityText, includeDisabled, sortText, offset, limit, verbose) = request;
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
        var system = Sources.TryAsync(() => client.System.GetAsync(cancellationToken), cancellationToken);
        var devicesTask = directory.GetAllAsync(cancellationToken);
        await Task.WhenAll(rulesTask, templates, open, raisedLog, groups, locations, system, devicesTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Which device columns this server has removed (LibreNMS 26.10, #78); unknown keeps them.
        var serverVersion = LibreNmsVersion.Parse(system.Result.Value?.LocalVersion);

        var unavailable = new List<Unavailable>();
        foreach (var (source, error) in new[] { ("templates", templates.Result.Error), ("open alerts", open.Result.Error), ("alert log", raisedLog.Result.Error), ("device groups", groups.Result.Error), ("locations", locations.Result.Error) })
        {
            if (error is not null)
            {
                unavailable.Add(new Unavailable(source, error));
            }
        }

        // Open alerts per rule, counted as active_alerts counts them (#77): one per device it's firing on, however many faults.
        var openByRule = open.Result.Value?.Where(a => a.State != AlertState.Recovered).GroupBy(a => a.RuleId).ToDictionary(g => g.Key, g => g.Select(a => a.DeviceId).Distinct().Count());
        // Raised = occurrences started in the last 7 days (#76), not fault events: eight cores together are one.
        var raisedByRule = raisedLog.Result.Value is { } raisedRead
            ? AlertOccurrences.From(raisedRead.Items, options)
                .Where(o => o.Started is { } at && recent.Contains(at))
                .GroupBy(o => o.RuleId)
                .ToDictionary(g => g.Key, g => g.Count())
            : null;
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
            return new AlertRuleRow(r.Id, redactor.Redact(r.Name ?? $"Rule {r.Id}"),
                r.Severity.ToString().ToLowerInvariant(), !r.Disabled, Clean(Condition(r)),
                new RuleApplicability(everywhere, everywhere ? null : new RuleTargets(r.InvertMap ? true : null,
                    r.Devices.Count > 0 ? r.Devices.Select(id => redactor.Redact(hostnames.GetValueOrDefault(id) ?? $"device {id}")).ToList() : null,
                    r.Groups.Count > 0 ? r.Groups.Select(id => redactor.Redact(groupNames.GetValueOrDefault(id) ?? $"group {id}")).ToList() : null,
                    r.Locations.Count > 0 ? r.Locations.Select(id => redactor.Redact(locationNames.GetValueOrDefault(id) ?? $"location {id}")).ToList() : null)),
                templateByRule is null ? null : Clean(templateByRule.GetValueOrDefault(r.Id) ?? "default"),
                r.NotifyPerEntity, r.MaxEntities, r.NotifyPerEntity == true && r.MaxEntities is null ? true : null,
                PerEntityNote(r), RemovedColumns(r, serverVersion), x.Open, x.Raised,
                verbose && !string.IsNullOrWhiteSpace(r.Notes) ? Clean(r.Notes) : null,
                verbose && !string.IsNullOrWhiteSpace(r.Procedure) ? Clean(r.Procedure) : null);
        }).ToList();

        var page = Paging.Apply(shaped, offset, limit, out var info);
        var needReview = sorted.Count(x => RemovedColumns(x.Rule, serverVersion) is not null);
        var summary = $"{Devices.DeviceHealth.Count(rules.Count, "rule")}" + (includeDisabled ? string.Empty : " enabled") +
            (name is null && severity is null ? "." : " match.") +
            (needReview == 0 ? string.Empty : $" {(needReview == 1 ? "1 rule uses" : $"{needReview} rules use")} device columns LibreNMS 26.10 removed, so won't match as before - see removed_columns.") +
            (raisedComplete || raisedByRule is null ? string.Empty : " The 7-day raised counts only cover as far back as LibreNMS's alert log reaches.") +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "rule", "rules") : string.Empty);

        return new(summary, new AlertRulesData(page, info, unavailable.Count > 0 ? unavailable : null));
    }

    /// <summary>
    /// What acknowledging one of the rule's faults does on LibreNMS 26.10 -
    /// null before 26.10, where an alert is one row anyway.
    /// </summary>
    private static string? PerEntityNote(AlertRule rule) => rule.NotifyPerEntity switch
    {
        null => null,
        false => "Per-entity notification is off, so acknowledging any fault acknowledges every open fault for that device and rule.",
        true when rule.MaxEntities is { } limit => $"Each fault is notified and acknowledged on its own while a device has no more than {limit} open; beyond that, acknowledging one acknowledges them all.",
        true => "Each fault is notified and acknowledged on its own while a device has no more than the server's default limit open (20 unless changed in LibreNMS); beyond that, acknowledging one acknowledges them all.",
    };

    /// <summary>The removed device columns a rule's conditions or SQL use, with what to use instead - null when none.</summary>
    private static IReadOnlyList<RemovedRuleColumn>? RemovedColumns(AlertRule rule, Version? serverVersion)
    {
        var removed = AlertRuleCompatibility.FindRemovedColumns(rule, serverVersion ?? LibreNmsVersion.Faults);
        return removed.Count == 0
            ? null
            : removed.Select(c => new RemovedRuleColumn(c.Field, c.RemovedIn.ToString(3), c.Replacement, c.Advice)).ToList();
    }

    /// <summary>The condition as LibreNMS shows it: its own text when stored, otherwise built from the rule's builder JSON (Core's formatter).</summary>
    private static string? Condition(AlertRule? rule)
        => rule is null ? null
            : !string.IsNullOrWhiteSpace(rule.Rule) ? rule.Rule
            : AlertRuleSqlFormatter.Format(rule.Builder);
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
    private AlertGroupSummary CleanGroup(AlertGroupSummary row) => row with
    {
        Rule = redactor.Redact(row.Rule), Entity = Clean(row.Entity), Note = Clean(row.Note),
        Faults = row.Faults?.Select(f => f with { Entity = Clean(f.Entity), EntityType = Clean(f.EntityType) }).ToList(),
    };
}
