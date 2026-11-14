using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Events;

public sealed record DeviceEventsRequest(int DeviceId, int? Hours = null, string? Since = null, string? Until = null, int Offset = 0, int Limit = 50);
public sealed record DeviceEventsData(DeviceSummary Device, EventWindowInfo Window, IReadOnlyList<TimelineEntry> Entries,
    PageInfo Page, IReadOnlyList<Unavailable>? Unavailable);
public sealed record DeviceEventsResult(string Summary, DeviceEventsData Data);

/// <summary>The same merged event, alert and outage history that MCP presents.</summary>
public sealed class DeviceEventHistory(DeviceDirectory directory, DeviceTimeline timeline, AgentOptions options, TimeProvider time, Redactor redactor)
{
    public const int DefaultHours = 24;
    public const int MaxEvents = 100;
    public static DeviceEventHistory Create(ILibreNmsClient client, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        return new(new DeviceDirectory(client, time), new DeviceTimeline(client, options), options, time, new Redactor());
    }
    public async Task<DeviceEventsResult> ReadAsync(DeviceEventsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be positive.");
        var window = TimeWindow.Resolve(request.Since, request.Until, request.Hours, DefaultHours, time);
        Paging.Check(request.Offset, request.Limit, MaxEvents);
        var device = (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == request.DeviceId)
            ?? throw new DeviceNotFoundException();
        return await ReadAsync(device, window, request.Offset, request.Limit, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<DeviceEventsResult> ReadAsync(Device device, TimeWindow window, int offset, int limit, CancellationToken cancellationToken)
    {
        var result = await timeline.BuildAsync(device, window, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = Paging.Apply(result.Entries, offset, limit, out var page).Select(e => CleanEntry(e, redactor)).ToList();
        var data = new DeviceEventsData(DeviceSummary.Redacted(device, options, redactor), EventWindowText.WindowData(window, result), entries,
            page, result.Unavailable.Count > 0 ? result.Unavailable : null);
        var paging = page.NextOffset is not null || page.Offset > 0 ? " " + Paging.Describe(page, "entry", "entries") : string.Empty;
        return new(EventWindowText.TimelineSentence(result, window, time.GetUtcNow().UtcDateTime) + paging, data);
    }
    internal static TimelineEntry CleanEntry(TimelineEntry entry, Redactor redactor)
    {
        string? Clean(string? value) => value is null ? null : redactor.Redact(value);
        return entry with { Kind = Clean(entry.Kind), Rule = Clean(entry.Rule), Text = Clean(entry.Text), User = Clean(entry.User) };
    }
}
