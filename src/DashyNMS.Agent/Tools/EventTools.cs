using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
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
    ILibreNmsClient client,
    IUnimusApi unimus,
    IGraylogApi graylog,
    UnimusDirectory unimusDevices,
    DeviceResolver resolver,
    DeviceTimeline timeline,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int DefaultHours = 24;
    public const int DefaultEvents = 50;
    public const int MaxEvents = 100;
    public const int MaxChangeEvents = 30;
    public const int MaxDiffLines = 200;
    public const int MaxLogSamples = 10;

    /// <summary>Graylog's search here is relative to now, so a window that ends earlier can't be counted yet (#30 adds absolute ranges).</summary>
    private static readonly TimeSpan GraylogNowSlack = TimeSpan.FromMinutes(5);

    [McpServerTool(Name = ToolText.DeviceEvents.Name, Title = ToolText.DeviceEvents.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.DeviceEvents.Description)]
    public Task<string> DeviceEvents(
        [Description(ToolText.DeviceEvents.Args.Device)] string device,
        [Description(ToolText.DeviceEvents.Args.Hours)] int? hours = null,
        [Description(ToolText.DeviceEvents.Args.Since)] string? since = null,
        [Description(ToolText.DeviceEvents.Args.Until)] string? until = null,
        [Description(ToolText.DeviceEvents.Args.Offset)] int offset = 0,
        [Description(ToolText.DeviceEvents.Args.Limit)] int limit = DefaultEvents,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.DeviceEvents.Name, ct => EventsAsync(device, hours, since, until, offset, limit, ct), cancellationToken);

    [McpServerTool(Name = ToolText.WhatChanged.Name, Title = ToolText.WhatChanged.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.WhatChanged.Description)]
    public Task<string> WhatChanged(
        [Description(ToolText.WhatChanged.Args.Device)] string device,
        [Description(ToolText.WhatChanged.Args.Hours)] int? hours = null,
        [Description(ToolText.WhatChanged.Args.Since)] string? since = null,
        [Description(ToolText.WhatChanged.Args.Until)] string? until = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.WhatChanged.Name, ct => ChangesAsync(device, hours, since, until, ct), cancellationToken);

    private async Task<JsonObject> EventsAsync(string query, int? hours, string? since, string? until, int offset, int limit, CancellationToken cancellationToken)
    {
        var window = TimeWindow.Resolve(since, until, hours, DefaultHours, time);
        Paging.Check(offset, limit, MaxEvents);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await timeline.BuildAsync(device, window, cancellationToken).ConfigureAwait(false);
        var entries = Paging.Apply(result.Entries, offset, limit, out var page);

        var data = new
        {
            device = DeviceSummary.From(device, options),
            window = WindowData(window, result),
            entries,
            page,
            unavailable = result.Unavailable.Count > 0 ? result.Unavailable : null,
        };

        var paging = page.NextOffset is not null || page.Offset > 0 ? " " + Paging.Describe(page, "entry", "entries") : string.Empty;
        return ToolResult.Ok(TimelineSentence(result, window) + paging, data);
    }

    private async Task<JsonObject> ChangesAsync(string query, int? hours, string? since, string? until, CancellationToken cancellationToken)
    {
        var window = TimeWindow.Resolve(since, until, hours, DefaultHours, time);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);

        var events = timeline.BuildAsync(device, window, cancellationToken);
        var config = ConfigChangeAsync(device, window, cancellationToken);
        var logs = LogsAsync(device, window, cancellationToken);
        await Task.WhenAll(events, config, logs).ConfigureAwait(false);

        var (configData, configSentence) = config.Result;
        var (logData, logSentence) = logs.Result;
        var shown = ToolResult.Take(events.Result.Entries, MaxChangeEvents, out var omitted);
        var data = new
        {
            device = DeviceSummary.From(device, options),
            window = WindowData(window, events.Result),
            librenms = new
            {
                entries = shown,
                omitted = omitted > 0 ? omitted : (int?)null,
                unavailable = events.Result.Unavailable.Count > 0 ? events.Result.Unavailable : null,
            },
            unimus = configData,
            graylog = logData,
        };

        return ToolResult.Ok($"{TimelineSentence(events.Result, window)} {configSentence} {logSentence}", data);
    }

    /// <summary>The window as covered, so the model can say "between X and Y" and knows when LibreNMS's logs fell short.</summary>
    private static object WindowData(TimeWindow window, Timeline result) => new
    {
        since = window.Since,
        until = window.Until,
        window_complete = result.WindowComplete,
        covered_since = result.WindowComplete ? (DateTime?)null : result.CoveredSince,
    };

    /// <summary>"In the last 24 hours" for a window ending now, "In the 2 hours to 2026-10-17 04:00 UTC" otherwise.</summary>
    internal static string Describe(TimeWindow window, DateTime now)
    {
        var span = window.Span;
        var length = span.TotalHours >= 48 && span.TotalHours % 24 == 0 ? DeviceTools.Count((int)span.TotalDays, "day")
            : span.TotalMinutes >= 120 ? DeviceTools.Count((int)Math.Round(span.TotalHours), "hour")
            : DeviceTools.Count((int)Math.Round(span.TotalMinutes), "minute");
        return window.Until >= now - GraylogNowSlack ? $"In the last {length}" : $"In the {length} to {window.Until:yyyy-MM-dd HH:mm} UTC";
    }

    private string TimelineSentence(Timeline result, TimeWindow window)
    {
        var lead = Describe(window, time.GetUtcNow().UtcDateTime);
        return $"{lead}: {DeviceTools.Count(result.Events, "event")}, " +
            $"{DeviceTools.Count(result.AlertChanges, "alert change")}, {DeviceTools.Count(result.Outages, "outage")}" +
            (result.Unavailable.Count > 0 ? $" ({DeviceTools.Count(result.Unavailable.Count, "log")} couldn't be read)" : string.Empty) +
            (result.WindowComplete ? "." : $". LibreNMS's logs only reach back to {result.CoveredSince:yyyy-MM-dd HH:mm} UTC, so the start of the window isn't covered.");
    }

    /// <summary>
    /// The diff for the newest Unimus backup that began inside the window,
    /// against the one before it - a backup is a config version, valid from
    /// when Unimus first saw it until it changed again.
    /// </summary>
    private async Task<(object Data, string Sentence)> ConfigChangeAsync(Device device, TimeWindow window, CancellationToken cancellationToken)
    {
        if (!unimus.IsConfigured)
        {
            return (new { configured = false }, "Unimus isn't configured.");
        }

        if (options.ConfigText == ConfigText.Off)
        {
            return (new { configured = true, turned_off = "Config data is turned off on this server (MCP_CONFIG_TEXT=off)." }, "Config data is turned off.");
        }

        var lookup = await Sources.TryAsync(() => unimusDevices.MatchAsync(device, cancellationToken), cancellationToken).ConfigureAwait(false);

        if (lookup.Error is { } lookupError)
        {
            return (new { configured = true, error = lookupError }, "Unimus couldn't be reached.");
        }

        if (lookup.Value is not { } match)
        {
            return (new { configured = true, device_found = false }, "Unimus doesn't know this device.");
        }

        var fetched = await Sources.TryAsync<(List<UnimusBackup> Backups, int Changed, UnimusBackupDiff? Diff)>(async () =>
        {
            var page = await unimus.GetBackupsAsync(match.Id, 0, 10, cancellationToken).ConfigureAwait(false);
            var backups = page.Backups.OrderByDescending(b => b.ValidSince ?? 0).ToList();
            var changed = backups.FindIndex(b => b.ValidSinceUtc is { } began && window.Contains(began));
            if (changed < 0 || changed + 1 >= backups.Count)
            {
                return (backups, -1, null);
            }

            return (backups, changed, await unimus.GetDiffAsync(backups[changed + 1].Id, backups[changed].Id, cancellationToken).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);

        if (fetched.Error is { } error)
        {
            return (new { configured = true, device_found = true, error }, "Unimus backups couldn't be read.");
        }

        var (list, index, diff) = fetched.Value;
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
                warning = UnimusDirectory.StaleNote(latest, time.GetUtcNow().UtcDateTime),
            }, UnimusDirectory.StaleNote(latest, time.GetUtcNow().UtcDateTime) is { } stale
                ? "No config change in Unimus. " + stale
                : "No config change in Unimus.");
        }

        var rendered = ConfigDiff.Render(diff, MaxDiffLines);
        var full = options.ConfigText == ConfigText.Full;
        return (new
        {
            configured = true,
            device_found = true,
            config_changed = true,
            changed_at = list[index].ValidSinceUtc,
            previous_version_since = list[index + 1].ValidSinceUtc,
            changed_lines = rendered.Lines,
            lines_removed = rendered.Removed,
            lines_added = rendered.Added,
            config_diff = full ? rendered.Text : null,
            config_diff_lines_omitted = full && rendered.Omitted > 0 ? rendered.Omitted : (int?)null,
            config_text = full ? null : "Left out on this server (MCP_CONFIG_TEXT=summary).",
        }, $"Unimus shows a config change ({DeviceTools.Count(rendered.Lines, "changed line")}).");
    }

    /// <summary>The diff as text, as <see cref="ConfigDiff.Render"/> - kept for the tests that check what_changed's own rendering.</summary>
    internal static (string Text, int Lines, int Omitted) Render(UnimusBackupDiff diff)
    {
        var rendered = ConfigDiff.Render(diff, MaxDiffLines);
        return (rendered.Text, rendered.Lines, rendered.Omitted);
    }

    /// <summary>Messages from the device in the window, matched the way LibreNMS's own Graylog tab matches them.</summary>
    private async Task<(object Data, string Sentence)> LogsAsync(Device device, TimeWindow window, CancellationToken cancellationToken)
    {
        if (!graylog.IsConfigured)
        {
            return (new { configured = false }, "Graylog isn't configured.");
        }

        // Core's Graylog search is relative to now, so it can count "the last
        // N seconds" but not a window that ended earlier. Rather than return
        // a count for the wrong span, say so and point to search_logs, which
        // reads a past window oldest first to count it (#30).
        var now = time.GetUtcNow().UtcDateTime;
        if (window.Until < now - GraylogNowSlack)
        {
            return (new { configured = true, skipped = "Graylog is only counted here for windows that end now. Use search_logs with this device, since and until for an earlier window." },
                "Graylog isn't counted for windows that end in the past; search_logs can.");
        }

        // A switch often logs from a VLAN interface rather than the address
        // LibreNMS polls it on, hence "match any address" (see LogTools).
        var addresses = await LogTools.AddressesAsync(client, device, options, cancellationToken).ConfigureAwait(false);
        var query = GraylogQuery.BuildSimpleQuery(search: null, options.GraylogQueryField, addresses);
        var rangeSeconds = (int)Math.Ceiling((now - window.Since).TotalSeconds);

        var search = await Sources.TryAsync(
            () => graylog.SearchAsync(query, rangeSeconds, MaxLogSamples, 0, "timestamp:desc", filter: null, cancellationToken),
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
