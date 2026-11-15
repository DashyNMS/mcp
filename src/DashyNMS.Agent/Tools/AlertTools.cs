using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Alerts;
using DashyNMS.Agent.Catalogue;
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

    [McpServerTool(Name = ToolText.ActiveAlerts.Name, Title = ToolText.ActiveAlerts.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.ActiveAlerts.Description)]
    public Task<string> ActiveAlerts(
        [Description(ToolText.ActiveAlerts.Args.Severity)] string? severity = null,
        [Description(ToolText.ActiveAlerts.Args.Device)] string? device = null,
        [Description(ToolText.ActiveAlerts.Args.Group)] string? group = null,
        [Description(ToolText.ActiveAlerts.Args.Location)] string? location = null,
        [Description(ToolText.ActiveAlerts.Args.Hours)] int? hours = null,
        [Description(ToolText.ActiveAlerts.Args.Since)] string? since = null,
        [Description(ToolText.ActiveAlerts.Args.Until)] string? until = null,
        [Description(ToolText.ActiveAlerts.Args.Offset)] int offset = 0,
        [Description(ToolText.ActiveAlerts.Args.Limit)] int limit = DefaultAlerts,
        [Description(ToolText.ActiveAlerts.Args.Verbose)] bool verbose = false,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            ToolText.ActiveAlerts.Name,
            ct => hours is null && string.IsNullOrWhiteSpace(since) && string.IsNullOrWhiteSpace(until)
                ? OpenAsync(severity, device, group, location, offset, limit, verbose, ct)
                : RaisedAsync(severity, device, group, location, TimeWindow.Resolve(since, until, hours, EventTools.DefaultHours, time), offset, limit, verbose, ct),
            cancellationToken);

    /// <summary>
    /// The alerts open now, one per rule firing on a device (#75), from the shared typed
    /// operation hosts such as mobile also run, so grouping, order and totals are one
    /// implementation. On LibreNMS 26.10 eight busy cores are one group of eight faults,
    /// not eight alerts. Paged by group in Core's stable order, so pages don't shuffle.
    /// </summary>
    private async Task<JsonObject> OpenAsync(string? severityText, string? deviceQuery, string? group, string? location, int offset, int limit, bool verbose, CancellationToken cancellationToken)
    {
        // Checked before the device is resolved, so a bad argument costs no request.
        OpenAlerts.ParseSeverity(severityText);
        Paging.Check(offset, limit, MaxAlerts);
        int? deviceId = string.IsNullOrWhiteSpace(deviceQuery)
            ? null
            : (await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false)).DeviceId;

        var result = await new OpenAlerts(client, directory, scopes, options, runner.Redactor)
            .ListAsync(new OpenAlertsRequest(deviceId, severityText, group, location, offset, limit), cancellationToken)
            .ConfigureAwait(false);

        var now = time.GetUtcNow().UtcDateTime;
        var totals = result.Totals;

        // A page must fit the result cap whole (#80): cut by the cap, the
        // alerts left off would be skipped by next_offset. So a page that's too
        // big is shortened, and next_offset follows what was really returned.
        var take = limit;
        while (true)
        {
            var shown = take == limit ? result : result.Repage(take);
            var page = shown.Page;
            var summary = totals.AlertGroups == 0
                ? "No open alerts visible to this account" + (result.Filtered ? " match." : ".")
                : $"{totals.Describe()} on {DeviceTools.Count(totals.Devices, "device")} ({totals.Critical} critical, {totals.Warning} warning)." +
                  PagingNote(page);
            var output = ToolResult.Ok(summary, new { totals, devices = Shape(shown.Groups, now, verbose), page });

            if (take <= 1 || ToolResult.Fits(output, options.MaxResultBytes))
            {
                return output;
            }

            take = Math.Max(1, take * 3 / 4);
        }
    }

    /// <summary>
    /// A page of alert groups by device. Without verbose a group lists its
    /// first few faults by name and state only; verbose gives up to
    /// <see cref="OpenAlerts.MaxFaults"/> with their entity IDs and times.
    /// </summary>
    private static object Shape(IReadOnlyList<OpenAlertGroup> shown, DateTime now, bool verbose)
        => shown
            .GroupBy(g => g.DeviceId)
            .Select(g => new
            {
                device_id = g.Key,
                hostname = g.First().Hostname,
                display = string.IsNullOrWhiteSpace(g.First().Device?.Display) ? null : g.First().Device!.Display,
                device_status = g.First().Device?.Status,
                alerts = g.Select(alert =>
                {
                    // A group of one names its fault in entity; only larger groups list faults.
                    var listed = alert.FaultCount > 1 ? alert.Faults.Take(verbose ? OpenAlerts.MaxFaults : BriefFaults).ToList() : null;
                    return new
                    {
                        GroupId = alert.Key.ToString(),
                        AlertId = alert.Representative.Id,
                        alert.RuleId,
                        source = alert.Representative.Source == AlertSource.Fault ? "fault" : "alert",
                        alert.Rule,
                        Severity = alert.Severity.ToString().ToLowerInvariant(),
                        State = StateText(alert.State),
                        alert.FaultCount,
                        acknowledged_count = alert.AcknowledgedCount > 0 && alert.FaultCount > 1 ? (int?)alert.AcknowledgedCount : null,
                        alert.Entity,
                        since = verbose ? alert.Since : null,
                        age_minutes = alert.Since is { } started ? (long?)Math.Max(0, (long)(now - started).TotalMinutes) : null,
                        note = verbose ? alert.Note : null,
                        faults = listed?.Select(f => verbose
                            ? (object)new FaultBrief(f.Key.Id, f.Entity, f.EntityType, f.EntityId, f.State.ToString().ToLowerInvariant(), f.Since)
                            : new { FaultId = f.Key.Id, f.Entity, State = f.State.ToString().ToLowerInvariant() }).ToList(),
                        faults_omitted = listed is null || alert.FaultCount <= listed.Count ? (int?)null : alert.FaultCount - listed.Count,
                    };
                }).ToList(),
            })
            .ToList();

    /// <summary>The state words every alert tool uses: active, acknowledged or partly_acknowledged.</summary>
    private static string StateText(OpenAlertState state) => state switch
    {
        OpenAlertState.PartlyAcknowledged => "partly_acknowledged",
        OpenAlertState.Acknowledged => "acknowledged",
        _ => "active",
    };

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
        OpenAlerts.ParseSeverity(severityText);
        Paging.Check(offset, limit, MaxAlerts);
        int? deviceId = string.IsNullOrWhiteSpace(deviceQuery) ? null : (await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false)).DeviceId;
        var result = await new RaisedAlerts(client, directory, scopes, options, time, runner.Redactor)
            .ReadAsync(new(deviceId, severityText, group, location, Since: window.Since.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Until: window.Until.ToString("O", System.Globalization.CultureInfo.InvariantCulture), Offset: offset, Limit: limit), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private static string PagingNote(PageInfo page)
        => page.NextOffset is not null || page.Offset > 0 ? " " + Paging.Describe(page, "alert", "alerts") : string.Empty;
}
