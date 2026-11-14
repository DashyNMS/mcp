using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Graylog;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Events;

public sealed record WhatChangedRequest(int DeviceId, int? Hours = null, string? Since = null, string? Until = null);
public sealed record LibreNmsChanges(IReadOnlyList<TimelineEntry> Entries, int? Omitted, IReadOnlyList<Unavailable>? Unavailable);
public sealed record ConfigChangeData
{
    public bool Configured { get; init; }
    public string? TurnedOff { get; init; }
    public string? Error { get; init; }
    public bool? DeviceFound { get; init; }
    public bool? ConfigChanged { get; init; }
    public DateTime? LatestBackupSince { get; init; }
    public DateTime? LastBackedUp { get; init; }
    public string? Warning { get; init; }
    public DateTime? ChangedAt { get; init; }
    public DateTime? PreviousVersionSince { get; init; }
    public int? ChangedLines { get; init; }
    public int? LinesRemoved { get; init; }
    public int? LinesAdded { get; init; }
    public string? ConfigDiff { get; init; }
    public int? ConfigDiffLinesOmitted { get; init; }
    public string? ConfigText { get; init; }
}
public sealed record ChangeLogLine(string? Time, string? Source, string? Level, string? Text);
public sealed record GraylogChangeData
{
    public bool Configured { get; init; }
    public string? Skipped { get; init; }
    public string? Error { get; init; }
    public long? MessageCount { get; init; }
    public IReadOnlyList<ChangeLogLine>? LogLines { get; init; }
}
public sealed record WhatChangedData(DeviceSummary Device, EventWindowInfo Window, LibreNmsChanges Librenms, ConfigChangeData Unimus, GraylogChangeData Graylog);
public sealed record WhatChangedResult(string Summary, WhatChangedData Data);

/// <summary>Device changes combining the shared timeline with host-owned optional read-only services.</summary>
public sealed class WhatChanged(ILibreNmsClient client, IUnimusApi unimus, IGraylogApi graylog, UnimusDirectory unimusDevices,
    DeviceDirectory directory, DeviceTimeline timeline, AgentOptions options, TimeProvider time, Redactor redactor)
{
    public const int MaxChangeEvents = 30;
    public const int MaxDiffLines = 200;
    public const int MaxLogSamples = 10;
    private static readonly TimeSpan GraylogNowSlack = EventWindowText.NowSlack;

    public static WhatChanged Create(ILibreNmsClient client, IUnimusApi unimus, IGraylogApi graylog, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(unimus);
        ArgumentNullException.ThrowIfNull(graylog);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        return new(client, unimus, graylog, new UnimusDirectory(unimus, options, time), new DeviceDirectory(client, time),
            new DeviceTimeline(client, options), options, time, new Redactor());
    }
    public async Task<WhatChangedResult> ReadAsync(WhatChangedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be positive.");
        var window = TimeWindow.Resolve(request.Since, request.Until, request.Hours, DeviceEventHistory.DefaultHours, time);
        var device = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new DeviceNotFoundException();
        return await ReadAsync(device, window, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<WhatChangedResult> ReadAsync(Device device, TimeWindow window, CancellationToken cancellationToken)
    {

        var events = timeline.BuildAsync(device, window, cancellationToken);
        var config = ConfigChangeAsync(device, window, cancellationToken);
        var logs = LogsAsync(device, window, cancellationToken);
        await Task.WhenAll(events, config, logs).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var (configData, configSentence) = config.Result;
        var (logData, logSentence) = logs.Result;
        var shown = events.Result.Entries.Take(MaxChangeEvents).Select(e => DeviceEventHistory.CleanEntry(e, redactor)).ToList();
        var omitted = Math.Max(0, events.Result.Entries.Count - MaxChangeEvents);
        var data = new WhatChangedData(DeviceSummary.Redacted(device, options, redactor), EventWindowText.WindowData(window, events.Result),
            new LibreNmsChanges(shown, omitted > 0 ? omitted : null, events.Result.Unavailable.Count > 0 ? events.Result.Unavailable : null), configData, logData);
        return new($"{EventWindowText.TimelineSentence(events.Result, window, time.GetUtcNow().UtcDateTime)} {configSentence} {logSentence}", data);
    }

    private async Task<(ConfigChangeData Data, string Sentence)> ConfigChangeAsync(Device device, TimeWindow window, CancellationToken cancellationToken)
    {
        if (!unimus.IsConfigured)
        {
            return (new ConfigChangeData { Configured = false }, "Unimus isn't configured.");
        }

        if (options.ConfigText == ConfigText.Off)
        {
            return (new ConfigChangeData { Configured = true, TurnedOff = "Config data is turned off on this server (MCP_CONFIG_TEXT=off)." }, "Config data is turned off.");
        }

        var lookup = await Sources.TryAsync(() => unimusDevices.MatchAsync(device, cancellationToken), cancellationToken).ConfigureAwait(false);

        if (lookup.Error is { } lookupError)
        {
            return (new ConfigChangeData { Configured = true, Error = lookupError }, "Unimus couldn't be reached.");
        }

        if (lookup.Value is not { } match)
        {
            return (new ConfigChangeData { Configured = true, DeviceFound = false }, "Unimus doesn't know this device.");
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
            return (new ConfigChangeData { Configured = true, DeviceFound = true, Error = error }, "Unimus backups couldn't be read.");
        }

        var (list, index, diff) = fetched.Value;
        var latest = list.FirstOrDefault();
        if (diff is null)
        {
            return (new ConfigChangeData
            {
                Configured = true,
                DeviceFound = true,
                ConfigChanged = false,
                LatestBackupSince = latest?.ValidSinceUtc,
                LastBackedUp = latest?.ValidUntilUtc,
                Warning = UnimusDirectory.StaleNote(latest, time.GetUtcNow().UtcDateTime),
            }, UnimusDirectory.StaleNote(latest, time.GetUtcNow().UtcDateTime) is { } stale
                ? "No config change in Unimus. " + stale
                : "No config change in Unimus.");
        }

        var rendered = ConfigDiff.Render(diff, MaxDiffLines);
        var full = options.ConfigText == ConfigText.Full;
        return (new ConfigChangeData
        {
            Configured = true,
            DeviceFound = true,
            ConfigChanged = true,
            ChangedAt = list[index].ValidSinceUtc,
            PreviousVersionSince = list[index + 1].ValidSinceUtc,
            ChangedLines = rendered.Lines,
            LinesRemoved = rendered.Removed,
            LinesAdded = rendered.Added,
            ConfigDiff = full ? redactor.Redact(rendered.Text) : null,
            ConfigDiffLinesOmitted = full && rendered.Omitted > 0 ? rendered.Omitted : (int?)null,
            ConfigText = full ? null : "Left out on this server (MCP_CONFIG_TEXT=summary).",
        }, $"Unimus shows a config change ({DeviceHealth.Count(rendered.Lines, "changed line")}).");
    }

    private async Task<(GraylogChangeData Data, string Sentence)> LogsAsync(Device device, TimeWindow window, CancellationToken cancellationToken)
    {
        if (!graylog.IsConfigured)
        {
            return (new GraylogChangeData { Configured = false }, "Graylog isn't configured.");
        }

        // Core's Graylog search is relative to now, so it can count "the last
        // N seconds" but not a window that ended earlier. Rather than return
        // a count for the wrong span, say so and point to search_logs, which
        // reads a past window oldest first to count it (#30).
        var now = time.GetUtcNow().UtcDateTime;
        if (window.Until < now - GraylogNowSlack)
        {
            return (new GraylogChangeData { Configured = true, Skipped = "Graylog is only counted here for windows that end now. Use search_logs with this device, since and until for an earlier window." },
                "Graylog isn't counted for windows that end in the past; search_logs can.");
        }

        // A switch often logs from a VLAN interface rather than the address
        // LibreNMS polls it on, hence "match any address" (see LogTools).
        var addresses = await GraylogDeviceAddresses.ReadAsync(client, device, options, cancellationToken).ConfigureAwait(false);
        var query = GraylogQuery.BuildSimpleQuery(search: null, options.GraylogQueryField, addresses);
        var rangeSeconds = (int)Math.Ceiling((now - window.Since).TotalSeconds);

        var search = await Sources.TryAsync(
            () => graylog.SearchAsync(query, rangeSeconds, MaxLogSamples, 0, "timestamp:desc", filter: null, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (search.Error is { } error)
        {
            return (new GraylogChangeData { Configured = true, Error = error }, "Graylog couldn't be searched.");
        }

        var result = search.Value!;
        var samples = result.Messages
            .Select(m => m.Message)
            .Select(m => new ChangeLogLine(Clean(Field(m, "timestamp")), Clean(m.Source), Clean(Field(m, "level")), Clean(m.Text)))
            .ToList();

        return (new GraylogChangeData { Configured = true, MessageCount = result.TotalResults, LogLines = samples },
            $"Graylog has {DeviceHealth.Count((int)Math.Min(result.TotalResults, int.MaxValue), "message")} from it.");
    }

    private static string? Field(GraylogMessage message, string name)
        => message.Fields.TryGetValue(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
}
