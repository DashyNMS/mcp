using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>What is alerting right now, across the estate.</summary>
[McpServerToolType]
public sealed class AlertTools(
    ILibreNmsClient client,
    DeviceDirectory directory,
    DeviceResolver resolver,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int MaxAlerts = 50;

    [McpServerTool(Name = "active_alerts", Title = "Active alerts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The alerts open in LibreNMS now (firing or acknowledged), grouped by device, newest first, with rule name, severity and how long each has been open. " +
        "Use it for \"what's alerting?\" or \"is anything critical?\". Optionally only one severity or one device. " +
        "Shows at most 50 alerts. Recovered alerts and alert history aren't included; use device_events for a device's history.")]
    public Task<string> ActiveAlerts(
        [Description("Only alerts of this severity: critical, warning or ok. Leave out for all.")] string? severity = null,
        [Description("Only alerts on this device: hostname, display name, IP address or LibreNMS device ID. Leave out for every device.")] string? device = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("active_alerts", ct => ListAsync(severity, device, ct), cancellationToken);

    private async Task<JsonObject> ListAsync(string? severityText, string? deviceQuery, CancellationToken cancellationToken)
    {
        var severity = ParseSeverity(severityText);
        int? deviceId = string.IsNullOrWhiteSpace(deviceQuery)
            ? null
            : (await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false)).DeviceId;

        var alerts = await client.Alerts.ListAsync(AlertQuery.Open, cancellationToken).ConfigureAwait(false);
        var devices = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.DeviceId);

        var matching = alerts
            .Where(a => severity is null || a.Severity == severity)
            .Where(a => deviceId is null || a.DeviceId == deviceId)
            .OrderByDescending(a => a.Timestamp)
            .ToList();

        var shown = ToolResult.Take(matching, MaxAlerts, out var omitted);
        var now = time.GetUtcNow().UtcDateTime;
        var groups = shown
            .GroupBy(a => a.DeviceId)
            .Select(g => new
            {
                device_id = g.Key,
                hostname = devices.TryGetValue(g.Key, out var d) ? d.Hostname : g.First().Hostname,
                display = d is not null && !string.Equals(d.Display, d.Hostname, StringComparison.OrdinalIgnoreCase) ? d.Display : null,
                device_status = d is not null ? DeviceSummary.StatusOf(d) : null,
                alerts = g.Select(a =>
                {
                    var alert = AlertSummary.From(a, options);
                    return new
                    {
                        alert.AlertId,
                        alert.Rule,
                        alert.Severity,
                        alert.State,
                        alert.Since,
                        age_minutes = alert.Since is { } since ? (long?)Math.Max(0, (long)(now - since).TotalMinutes) : null,
                        alert.Note,
                    };
                }).ToList(),
            })
            .ToList();

        var critical = matching.Count(a => a.Severity == AlertSeverity.Critical);
        var warning = matching.Count(a => a.Severity == AlertSeverity.Warning);
        var summary = matching.Count == 0
            ? "No open alerts" + (severity is not null || deviceId is not null ? " match." : ".")
            : $"{DeviceTools.Count(matching.Count, "open alert")} on {DeviceTools.Count(matching.Select(a => a.DeviceId).Distinct().Count(), "device")} ({critical} critical, {warning} warning).";

        return ToolResult.Ok(summary, new { devices = groups }, omitted);
    }

    private static AlertSeverity? ParseSeverity(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" or "any" => null,
        "critical" => AlertSeverity.Critical,
        "warning" => AlertSeverity.Warning,
        "ok" => AlertSeverity.Ok,
        _ => throw new ToolException("Severity must be critical, warning or ok, or left out for all."),
    };
}
