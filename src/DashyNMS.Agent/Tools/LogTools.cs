using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
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

    [McpServerTool(Name = "search_logs", Title = "Search logs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Searches Graylog's log messages across the network or for one device, over any window up to 31 days: \"any errors from the core switches overnight?\", " +
        "\"what did this firewall log between 2 and 4am?\". Gives the message count first, then the busiest sources, levels, facilities and applications, " +
        "messages over time, and the newest lines. Narrow it by text in the message, a device (matched on its names and addresses, as LibreNMS's Graylog tab does), " +
        "a maximum syslog level (0 emergency to 7 debug: 3 means errors and worse) and a stream. " +
        "The count and messages over time are exact for any window; the busiest values and the lines come from up to 150 messages, and top_from says which. " +
        "When nothing matches, it says what might be why. For a device's logs alongside its events and config changes, use what_changed.")]
    public Task<string> SearchLogs(
        [Description("Words to find in the message text, matched as a phrase. Leave out for every message.")] string? text = null,
        [Description("A device whose messages to search: hostname, display name, IP address or LibreNMS device ID. Leave out for the whole network.")] string? device = null,
        [Description("Start of the window: relative (\"90m\", \"6h\", \"3d\") or ISO 8601 (UTC unless a zone is given). Defaults to 24 hours before until.")] string? since = null,
        [Description("End of the window, the same forms as since. Defaults to now.")] string? until = null,
        [Description("Shorthand for since: this many hours before until, 1 to 168. Use since instead for longer windows.")] int? hours = null,
        [Description("Only messages at this syslog level or more severe: 0 emergency, 1 alert, 2 critical, 3 error, 4 warning, 5 notice, 6 informational, 7 debug.")] int? level = null,
        [Description("A Graylog stream, by title or ID. Leave out for every stream the account can read.")] string? stream = null,
        [Description("How many of the newest lines to return, 0 to 50. Defaults to 20; 0 for the counts alone.")] int limit = DefaultLines,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("search_logs", ct => SearchAsync(text, device, since, until, hours, level, stream, limit, ct), cancellationToken);

    private async Task<JsonObject> SearchAsync(
        string? text, string? deviceQuery, string? since, string? until, int? hours, int? level, string? streamQuery, int limit, CancellationToken cancellationToken)
    {
        if (!graylog.IsConfigured)
        {
            throw new ToolException("Graylog isn't configured on this server, so there are no logs to search. Set GRAYLOG_URL, GRAYLOG_USERNAME and GRAYLOG_PASSWORD to turn it on.");
        }

        if (limit is < 0 or > MaxLines)
        {
            throw new ToolException($"Limit must be between 0 and {MaxLines}.");
        }

        if (level is < 0 or > 7)
        {
            throw new ToolException("Level must be between 0 (emergency) and 7 (debug).");
        }

        var window = TimeWindow.Resolve(since, until, hours, DefaultHours, time);
        var now = time.GetUtcNow().UtcDateTime;

        Device? device = null;
        IReadOnlyList<string>? addresses = null;
        if (!string.IsNullOrWhiteSpace(deviceQuery))
        {
            device = await resolver.ResolveAsync(deviceQuery, cancellationToken).ConfigureAwait(false);
            addresses = await AddressesAsync(client, device, options, cancellationToken).ConfigureAwait(false);
        }

        var query = GraylogQuery.WithMaxLevel(GraylogQuery.BuildSimpleQuery(text, options.GraylogQueryField, addresses), level);
        GraylogStream? stream = null;
        if (!string.IsNullOrWhiteSpace(streamQuery))
        {
            stream = await ResolveStreamAsync(streamQuery.Trim(), cancellationToken).ConfigureAwait(false);
        }

        var filter = GraylogQuery.StreamFilter(stream?.Id);

        // The window's edges and its buckets' as "seconds back from now", Graylog's only kind of range.
        var endsNow = window.Until >= now - NowSlack;
        var width = window.Span / Buckets;
        var edges = Enumerable.Range(0, Buckets + 1)
            .Select(i => i == Buckets && endsNow ? 0 : SecondsBack(now, window.Since + (width * i)))
            .ToArray();

        // How many messages there are from each edge to now, all at once: each difference is a count,
        // from one edge up to the next - so a message on an edge counts once, in the later slice.
        var totals = await Task.WhenAll(edges.Select(seconds => CountBackAsync(query, seconds, filter, cancellationToken))).ConfigureAwait(false);
        var count = Math.Max(0, totals[0] - totals[Buckets]);
        var overTime = Enumerable.Range(0, Buckets)
            .Select(i => new { from = window.Since + (width * i), count = Math.Max(0, totals[i] - totals[i + 1]) })
            .ToList();

        // Then the messages themselves: the newest in the window, skipping those after it -
        // or, past the furthest Graylog pages to, the oldest in it.
        List<GraylogMessage> read = [];
        var newestRead = true;
        if (count > 0)
        {
            var newer = totals[Buckets];
            newestRead = newer + PageSize <= MaxOffset;
            var page = newestRead
                ? await graylog.SearchAsync(query, edges[0], PageSize, (int)newer, "timestamp:desc", filter, cancellationToken).ConfigureAwait(false)
                : await graylog.SearchAsync(query, edges[0], PageSize, 0, "timestamp:asc", filter, cancellationToken).ConfigureAwait(false);
            read = page.Messages.Select(m => m.Message).Where(m => InWindow(m, window, endsNow)).ToList();
        }

        var lines = (newestRead ? read.OrderByDescending(m => m.Timestamp) : read.OrderBy(m => m.Timestamp)).Take(limit).Select(m => new
        {
            time = m.Timestamp?.UtcDateTime,
            source = m.Source,
            level = Blank(GraylogQuery.LevelText(m.Level)),
            facility = Blank(GraylogQuery.FacilityText(m.Facility)),
            application = Blank(m.GetText("application_name")),
            text = Cut(m.Text),
        }).ToList();

        var data = new
        {
            search = new
            {
                text = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
                device = device is null ? null : DeviceBrief.From(device),
                level = level is { } l ? GraylogQuery.LevelText(l) + " or more severe" : null,
                stream = stream?.Title,
                since = window.Since,
                until = window.Until,
            },
            count,
            over_time = count == 0 ? null : overTime,
            top_from = read.Count == 0 ? null
                : read.Count >= count ? "every message in the window"
                : $"the {(newestRead ? "newest" : "oldest")} {Count(read.Count)} of the window's {Count(count)} messages",
            top = read.Count == 0 ? null : new
            {
                sources = Top(read, m => m.Source),
                levels = Top(read, m => Blank(GraylogQuery.LevelText(m.Level))),
                facilities = Top(read, m => Blank(GraylogQuery.FacilityText(m.Facility))),
                applications = Top(read, m => Blank(m.GetText("application_name"))),
            },
            lines = limit == 0 || lines.Count == 0 ? null : lines,
            lines_are = !newestRead && lines.Count > 0 ? "the oldest in the window: the newest are past the furthest Graylog pages to; narrow the window for them" : null,
            why_none = count == 0 ? WhyNone(text, device, level, stream, window, now) : null,
        };

        var summary = count == 0
            ? "No messages match in this window."
            : $"{Count(count)} {(count == 1 ? "message matches" : "messages match")} in this window" +
              (lines.Count > 0 ? $"; {(newestRead ? "the newest" : "the oldest")} {lines.Count} {(lines.Count == 1 ? "is" : "are")} shown." : ".");

        return ToolResult.Ok(summary, data);
    }

    /// <summary>How many messages match from <paramref name="seconds"/> ago to now - one row asked for, only the total kept.</summary>
    private async Task<long> CountBackAsync(string query, int seconds, string? filter, CancellationToken cancellationToken)
    {
        // Graylog reads a range of 0 as all time, so "from now to now" is answered here.
        if (seconds <= 0)
        {
            return 0;
        }

        var result = await graylog.SearchAsync(query, seconds, 1, 0, null, filter, cancellationToken).ConfigureAwait(false);
        return result.TotalResults;
    }

    /// <summary>Whole seconds back from now to a time, rounded up so the time itself is inside the range.</summary>
    private static int SecondsBack(DateTime now, DateTime at)
        => (int)Math.Min(int.MaxValue, Math.Max(0, Math.Ceiling((now - at).TotalSeconds)));

    /// <summary>
    /// A device's addresses for a Graylog search, as LibreNMS's Graylog tab
    /// builds them - its interface addresses too with "match any address"
    /// (<see cref="AgentOptions.GraylogMatchAnyAddress"/>). If those can't be
    /// read, its own names and address still make a search.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> AddressesAsync(ILibreNmsClient client, Device device, AgentOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<DeviceIpAddress>? interfaceAddresses = null;
        if (options.GraylogMatchAnyAddress)
        {
            var fetched = await Sources.TryAsync(() => client.Ports.ListIpAddressesAsync(device.DeviceId, cancellationToken), cancellationToken).ConfigureAwait(false);
            interfaceAddresses = fetched.Value;
        }

        return GraylogQuery.DeviceAddresses(device, resolvedHostname: null, interfaceAddresses);
    }

    private async Task<GraylogStream> ResolveStreamAsync(string text, CancellationToken cancellationToken)
    {
        var streams = await graylog.GetStreamsAsync(cancellationToken).ConfigureAwait(false);
        var match = streams.FirstOrDefault(s => string.Equals(s.Id, text, StringComparison.OrdinalIgnoreCase))
            ?? streams.FirstOrDefault(s => string.Equals(s.Title?.Trim(), text, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        var partial = streams.Where(s => s.Title?.Contains(text, StringComparison.OrdinalIgnoreCase) == true).ToList();
        return partial.Count == 1
            ? partial[0]
            : throw new ToolException(
                partial.Count == 0 ? "No Graylog stream has that title or ID." : $"{partial.Count} Graylog streams match. Use the full title or the ID.",
                new { streams = (partial.Count == 0 ? streams : partial).Take(20).Select(s => new { id = s.Id, title = s.Title }).ToList() });
    }

    /// <summary>
    /// From since up to, not including, until - as the count has it - unless
    /// the window ends now. A message without a timestamp is kept: Graylog
    /// matched it for the window it was asked about.
    /// </summary>
    private static bool InWindow(GraylogMessage message, TimeWindow window, bool endsNow)
        => message.Timestamp is not { } at
           || (at.UtcDateTime >= window.Since && (endsNow || at.UtcDateTime < window.Until));

    private static IReadOnlyList<object>? Top(IReadOnlyList<GraylogMessage> read, Func<GraylogMessage, string?> value)
    {
        var top = read
            .Select(value)
            .OfType<string>()
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(TopValues)
            .Select(g => (object)new { value = g.Key, count = g.Count() })
            .ToList();
        return top.Count > 0 ? top : null;
    }

    private string WhyNone(string? text, Device? device, int? level, GraylogStream? stream, TimeWindow window, DateTime now)
    {
        var reasons = new List<string>();
        if (now - window.Until < TimeSpan.FromMinutes(5))
        {
            reasons.Add("Graylog can take a minute or two to index new messages, so the last few minutes may not show yet.");
        }

        if (device is not null)
        {
            reasons.Add(options.GraylogMatchAnyAddress
                ? $"The device's names and addresses are matched in Graylog's {options.GraylogQueryField} field; it may log under another name or not send to Graylog."
                : $"Only the device's own names and address are matched, in Graylog's {options.GraylogQueryField} field. If it logs from another address (a VLAN or loopback interface), GRAYLOG_MATCH_ANY_ADDRESS=true matches those too.");
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            reasons.Add("The text is matched as a phrase in the message; try fewer or different words.");
        }

        if (level is not null)
        {
            reasons.Add("The level limit leaves out less severe messages.");
        }

        if (stream is not null)
        {
            reasons.Add("Only one stream is searched.");
        }

        reasons.Add("The window may simply have been quiet; a longer one would tell.");
        return string.Join(' ', reasons);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Cut(string? text)
        => text is { Length: > MaxLineLength } ? text[..MaxLineLength] + ToolResult.Ellipsis : text;

    internal static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
