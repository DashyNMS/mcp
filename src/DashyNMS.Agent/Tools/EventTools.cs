using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>What happened to a device, and what changed on it.</summary>
[McpServerToolType]
public sealed class EventTools(
    IUnimusApi unimus,
    IGraylogApi graylog,
    DeviceResolver resolver,
    DeviceTimeline timeline,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int MaxEvents = 50;
    public const int MaxChangeEvents = 30;
    public const int MaxDiffLines = 200;
    public const int MaxLogSamples = 10;

    [McpServerTool(Name = "device_events", Title = "Device events", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "A device's history from LibreNMS for the last few hours, newest first: event log entries (interface changes, reboots, discovery), " +
        "alert state changes, and outages with their length. Use it for \"what happened to X?\" or \"when did X go down?\". " +
        "Shows at most 50 entries. Config changes and syslog aren't included; what_changed adds those.")]
    public Task<string> DeviceEvents(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("How many hours back to look, 1 to 168. Defaults to 24.")] int hours = 24,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("device_events", ct => EventsAsync(device, hours, ct), cancellationToken);

    [McpServerTool(Name = "what_changed", Title = "What changed", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Everything that changed on a device in the last few hours, from every source configured: the LibreNMS timeline (as device_events, up to 30 entries), " +
        "the config diff between its two newest Unimus backups if the newest is inside the window (secrets redacted, long diffs cut), " +
        "and how many Graylog messages it sent, with up to 10 of the newest. Use it when something broke and the question is why. " +
        "Each source says when it isn't configured or couldn't be reached.")]
    public Task<string> WhatChanged(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("How many hours back to look, 1 to 168. Defaults to 24.")] int hours = 24,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("what_changed", ct => ChangesAsync(device, hours, ct), cancellationToken);

    private async Task<JsonObject> EventsAsync(string query, int hours, CancellationToken cancellationToken)
    {
        DeviceTimeline.CheckHours(hours);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await timeline.BuildAsync(device, hours, MaxEvents, cancellationToken).ConfigureAwait(false);

        var data = new
        {
            device = DeviceSummary.From(device, options),
            window_hours = hours,
            entries = result.Entries,
            unavailable = result.Unavailable.Count > 0 ? result.Unavailable : null,
        };

        return ToolResult.Ok(TimelineSentence(result, hours), data, result.Omitted);
    }

    private async Task<JsonObject> ChangesAsync(string query, int hours, CancellationToken cancellationToken)
    {
        DeviceTimeline.CheckHours(hours);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var since = time.GetUtcNow().UtcDateTime.AddHours(-hours);

        var events = timeline.BuildAsync(device, hours, MaxChangeEvents, cancellationToken);
        var config = ConfigChangeAsync(device, since, cancellationToken);
        var logs = LogsAsync(device, hours, cancellationToken);
        await Task.WhenAll(events, config, logs).ConfigureAwait(false);

        var (configData, configSentence) = config.Result;
        var (logData, logSentence) = logs.Result;
        var data = new
        {
            device = DeviceSummary.From(device, options),
            window_hours = hours,
            librenms = new
            {
                entries = events.Result.Entries,
                omitted = events.Result.Omitted > 0 ? events.Result.Omitted : (int?)null,
                unavailable = events.Result.Unavailable.Count > 0 ? events.Result.Unavailable : null,
            },
            unimus = configData,
            graylog = logData,
        };

        return ToolResult.Ok($"{TimelineSentence(events.Result, hours)} {configSentence} {logSentence}", data);
    }

    private static string TimelineSentence(Timeline result, int hours)
        => $"In the last {DeviceTools.Count(hours, "hour")}: {DeviceTools.Count(result.Events, "event")}, " +
           $"{DeviceTools.Count(result.AlertChanges, "alert change")}, {DeviceTools.Count(result.Outages, "outage")}" +
           (result.Unavailable.Count > 0 ? $" ({DeviceTools.Count(result.Unavailable.Count, "log")} couldn't be read)." : ".");

    /// <summary>
    /// The newest Unimus backup's diff against the one before, when the newest
    /// started inside the window - a backup is a config version, valid from
    /// when Unimus first saw it until it changed again.
    /// </summary>
    private async Task<(object Data, string Sentence)> ConfigChangeAsync(Device device, DateTime since, CancellationToken cancellationToken)
    {
        if (!unimus.IsConfigured)
        {
            return (new { configured = false }, "Unimus isn't configured.");
        }

        var lookup = await Sources.TryAsync(async () =>
        {
            var all = await unimus.ListAllDevicesAsync(cancellationToken).ConfigureAwait(false);
            return UnimusDeviceMatcher.Match(all, UnimusAddressCandidates.ForDevice(device, myDomain: null));
        }, cancellationToken).ConfigureAwait(false);

        if (lookup.Error is { } lookupError)
        {
            return (new { configured = true, error = lookupError }, "Unimus couldn't be reached.");
        }

        if (lookup.Value is not { } match)
        {
            return (new { configured = true, device_found = false }, "Unimus doesn't know this device.");
        }

        var fetched = await Sources.TryAsync<(List<UnimusBackup> Backups, UnimusBackupDiff? Diff)>(async () =>
        {
            var page = await unimus.GetBackupsAsync(match.Id, 0, 10, cancellationToken).ConfigureAwait(false);
            var backups = page.Backups.OrderByDescending(b => b.ValidSince ?? 0).ToList();
            if (backups.Count < 2 || backups[0].ValidSinceUtc is not { } newest || newest < since)
            {
                return (backups, null);
            }

            return (backups, await unimus.GetDiffAsync(backups[1].Id, backups[0].Id, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);

        if (fetched.Error is { } error)
        {
            return (new { configured = true, device_found = true, error }, "Unimus backups couldn't be read.");
        }

        var (list, diff) = fetched.Value;
        var latest = list.FirstOrDefault();
        if (diff is null)
        {
            return (new
            {
                configured = true,
                device_found = true,
                config_changed = false,
                latest_backup_since = latest?.ValidSinceUtc,
                last_backed_up = latest?.ValidUntilUtc,
            }, "No config change in Unimus.");
        }

        var (text, lines, omittedLines) = Render(diff);
        return (new
        {
            configured = true,
            device_found = true,
            config_changed = true,
            changed_at = latest!.ValidSinceUtc,
            previous_version_since = list[1].ValidSinceUtc,
            changed_lines = lines,
            config_diff = text,
            config_diff_lines_omitted = omittedLines > 0 ? omittedLines : (int?)null,
        }, $"Unimus shows a config change ({DeviceTools.Count(lines, "changed line")}).");
    }

    /// <summary>The changed lines as a unified-style diff: "- old", "+ new", common lines left out.</summary>
    /// <remarks>
    /// Private keys are blanked line by line here, before anything is cut,
    /// following each side of the diff through the unchanged lines too: a
    /// rotated key's diff holds only its body lines (BEGIN and END are
    /// unchanged), and a long one can be cut between BEGIN and END - either
    /// way the redactor would never see a whole block.
    /// </remarks>
    internal static (string Text, int Lines, int Omitted) Render(UnimusBackupDiff diff)
    {
        var lines = new List<string>();
        bool originalInKey = false, revisedInKey = false;
        foreach (var group in diff.LineGroups)
        {
            if (group.IsCommon)
            {
                foreach (var line in group.OriginalLines)
                {
                    Redactor.MaskPrivateKeyLine(line.Text, ref originalInKey);
                }

                foreach (var line in group.RevisedLines.Count > 0 ? group.RevisedLines : group.OriginalLines)
                {
                    Redactor.MaskPrivateKeyLine(line.Text, ref revisedInKey);
                }

                continue;
            }

            lines.AddRange(group.OriginalLines.Select(l => "- " + Redactor.MaskPrivateKeyLine(l.Text, ref originalInKey)));
            lines.AddRange(group.RevisedLines.Select(l => "+ " + Redactor.MaskPrivateKeyLine(l.Text, ref revisedInKey)));
        }

        var shown = ToolResult.Take(lines, MaxDiffLines, out var omitted);
        return (string.Join('\n', shown), lines.Count, omitted);
    }

    /// <summary>Messages from the device in the window, matched the way LibreNMS's own Graylog tab matches them.</summary>
    private async Task<(object Data, string Sentence)> LogsAsync(Device device, int hours, CancellationToken cancellationToken)
    {
        if (!graylog.IsConfigured)
        {
            return (new { configured = false }, "Graylog isn't configured.");
        }

        var addresses = GraylogQuery.DeviceAddresses(device, resolvedHostname: null, interfaceAddresses: null);
        var query = GraylogQuery.BuildSimpleQuery(search: null, options.GraylogQueryField, addresses);

        var search = await Sources.TryAsync(
            () => graylog.SearchAsync(query, hours * 3600, MaxLogSamples, 0, "timestamp:desc", filter: null, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (search.Error is { } error)
        {
            return (new { configured = true, error }, "Graylog couldn't be searched.");
        }

        var result = search.Value!;
        var samples = result.Messages
            .Select(m => m.Message)
            .Select(m => new
            {
                time = Field(m, "timestamp"),
                source = m.Source,
                level = Field(m, "level"),
                text = m.Text,
            })
            .ToList();

        return (new { configured = true, message_count = result.TotalResults, log_lines = samples },
            $"Graylog has {DeviceTools.Count((int)Math.Min(result.TotalResults, int.MaxValue), "message")} from it.");
    }

    private static string? Field(GraylogMessage message, string name)
        => message.Fields.TryGetValue(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;
}
