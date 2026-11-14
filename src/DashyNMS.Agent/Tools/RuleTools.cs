using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Alerts;
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

    [McpServerTool(Name = ToolText.AlertDetails.Name, Title = ToolText.AlertDetails.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.AlertDetails.Description)]
    public Task<string> AlertDetails(
        [Description(ToolText.AlertDetails.Args.AlertId)] int alert_id,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.AlertDetails.Name, ct => DetailsAsync(alert_id, ct), cancellationToken);

    [McpServerTool(Name = ToolText.AlertRules.Name, Title = ToolText.AlertRules.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.AlertRules.Description)]
    public Task<string> AlertRules(
        [Description(ToolText.AlertRules.Args.Name)] string? name = null,
        [Description(ToolText.AlertRules.Args.Severity)] string? severity = null,
        [Description(ToolText.AlertRules.Args.IncludeDisabled)] bool include_disabled = false,
        [Description(ToolText.AlertRules.Args.Sort)] string? sort = null,
        [Description(ToolText.AlertRules.Args.Offset)] int offset = 0,
        [Description(ToolText.AlertRules.Args.Limit)] int limit = DefaultRules,
        [Description(ToolText.AlertRules.Args.Verbose)] bool verbose = false,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.AlertRules.Name, ct => RulesAsync(name, severity, include_disabled, sort, offset, limit, verbose, ct), cancellationToken);

    private AlertRules Operation() => new(client, fleet, directory, scopes, options, time, runner.Redactor);

    private async Task<JsonObject> DetailsAsync(int alertId, CancellationToken token)
    {
        var result = await Operation().ReadDetailsAsync(new(alertId), token).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private async Task<JsonObject> RulesAsync(string? name, string? severity, bool includeDisabled, string? sort,
        int offset, int limit, bool verbose, CancellationToken token)
    {
        var result = await Operation().ReadRulesAsync(new(name, severity, includeDisabled, sort, offset, limit, verbose), token).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, new
        {
            rules = result.Data.Rules.Select(r => new
            {
                r.RuleId, r.Name, r.Severity, r.Enabled, r.Condition,
                applies_to = r.AppliesTo.AllDevices ? (object)"all devices" : r.AppliesTo.Targets!,
                r.Template, r.NotifyPerEntity, r.MaxEntities, r.MaxEntitiesInherited, r.Acknowledging,
                r.RemovedColumns, r.OpenAlerts, raised_last_7_days = r.RaisedLast7Days, r.Notes, r.Procedure,
            }).ToList(),
            result.Data.Page, result.Data.Unavailable,
        });
    }
}
