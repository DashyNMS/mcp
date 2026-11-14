using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// Graylog across the network or for one device (#30) - desktop's Logs tab
/// and a device's Graylog tab, with totals first and a few lines after.
/// </summary>
/// <remarks>
/// <para>
/// Core searches Graylog the way LibreNMS does, relative to now
/// (<c>search/universal/relative</c>, which still answers on Graylog 7), so
/// it can't ask for "02:00 to 04:00 yesterday" directly - Graylog's own
/// absolute search needs new code in Core, which waits for v2.0.0. Instead
/// every edge of the window and of its <see cref="Buckets"/> slices is asked
/// for as "the last N seconds", one row each with only the total kept, and
/// each count is a difference of two totals. That makes the count and the
/// messages over time exact for any window, past or not, to within the
/// messages that arrive while the searches run.
/// </para>
/// <para>
/// The lines and the busiest sources, levels, facilities and applications
/// come from one page of messages: the newest in the window, reached by
/// skipping the ones after it. Graylog pages no further than
/// <see cref="MaxOffset"/> messages back, so for an old window under a busy
/// present it reads the oldest in the window instead and says so. On a busy
/// window that page is a sample, and the result says how much it covers.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class LogTools(
    ILibreNmsClient client,
    IGraylogApi graylog,
    DeviceResolver resolver,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int DefaultHours = 24;
    public const int DefaultLines = 20;
    public const int MaxLines = 50;

    /// <summary>Messages per Graylog request.</summary>
    public const int PageSize = 150;

    /// <summary>How deep Graylog pages (Elasticsearch's and OpenSearch's default result window): offset plus page size.</summary>
    public const int MaxOffset = 10_000;

    public const int MaxLineLength = 300;
    public const int Buckets = 12;
    public const int TopValues = 5;

    /// <summary>A window ending this close to now counts as ending now: the clock moved while the question was asked.</summary>
    public static readonly TimeSpan NowSlack = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = ToolText.SearchLogs.Name, Title = ToolText.SearchLogs.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.SearchLogs.Description)]
    public Task<string> SearchLogs(
        [Description(ToolText.SearchLogs.Args.Text)] string? text = null,
        [Description(ToolText.SearchLogs.Args.Device)] string? device = null,
        [Description(ToolText.SearchLogs.Args.Since)] string? since = null,
        [Description(ToolText.SearchLogs.Args.Until)] string? until = null,
        [Description(ToolText.SearchLogs.Args.Hours)] int? hours = null,
        [Description(ToolText.SearchLogs.Args.Level)] int? level = null,
        [Description(ToolText.SearchLogs.Args.Stream)] string? stream = null,
        [Description(ToolText.SearchLogs.Args.Limit)] int limit = DefaultLines,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.SearchLogs.Name, ct => SearchAsync(text, device, since, until, hours, level, stream, limit, ct), cancellationToken);

    private async Task<JsonObject> SearchAsync(string? text, string? deviceQuery, string? since, string? until, int? hours,
        int? level, string? streamQuery, int limit, CancellationToken cancellationToken)
    {
        var request = new DashyNMS.Agent.Graylog.SearchLogsRequest(text, null, since, until, hours, level, streamQuery, limit);
        var window = DashyNMS.Agent.Graylog.LogSearch.Validate(graylog.IsConfigured, request, time);
        var now = time.GetUtcNow().UtcDateTime;
        var device = string.IsNullOrWhiteSpace(deviceQuery) ? null : await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false);
        var result = await new DashyNMS.Agent.Graylog.LogSearch(client, graylog, new DeviceDirectory(client, time), options, time, runner.Redactor)
            .ReadAsync(request, device, window, now, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }

    internal static Task<IReadOnlyList<string>> AddressesAsync(ILibreNmsClient client, Device device, AgentOptions options, CancellationToken cancellationToken)
        => DashyNMS.Agent.Graylog.GraylogDeviceAddresses.ReadAsync(client, device, options, cancellationToken);
    internal static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
