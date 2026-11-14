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
        var result = await new DashyNMS.Agent.Events.DeviceEventHistory(new DeviceDirectory(client, time), timeline, options, time, runner.Redactor)
            .ReadAsync(device, window, offset, limit, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    private async Task<JsonObject> ChangesAsync(string query, int? hours, string? since, string? until, CancellationToken cancellationToken)
    {
        var window = TimeWindow.Resolve(since, until, hours, DefaultHours, time);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await new DashyNMS.Agent.Events.WhatChanged(client, unimus, graylog, unimusDevices, new DeviceDirectory(client, time), timeline, options, time, runner.Redactor)
            .ReadAsync(device, window, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    internal static string Describe(TimeWindow window, DateTime now) => DashyNMS.Agent.Events.EventWindowText.Describe(window, now);
    internal static (string Text, int Lines, int Omitted) Render(UnimusBackupDiff diff)
    {
        var rendered = ConfigDiff.Render(diff, MaxDiffLines);
        return (rendered.Text, rendered.Lines, rendered.Omitted);
    }
}
