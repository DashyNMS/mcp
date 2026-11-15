using System.Globalization;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Graylog;

public sealed record SearchLogsRequest(string? Text = null, int? DeviceId = null, string? Since = null, string? Until = null,
    int? Hours = null, int? Level = null, string? Stream = null, int Limit = 20);
public sealed record LogSearchCriteria(string? Text, DeviceBrief? Device, string? Level, string? Stream, DateTime Since, DateTime Until);
public sealed record LogCountBucket(DateTime From, long Count);
public sealed record LogSearchLine(DateTime? Time, string? Source, string? Level, string? Facility, string? Application, string? Text);
public sealed record LogTopValue(string Value, int Count);
public sealed record LogTopValues(IReadOnlyList<LogTopValue>? Sources, IReadOnlyList<LogTopValue>? Levels,
    IReadOnlyList<LogTopValue>? Facilities, IReadOnlyList<LogTopValue>? Applications);
public sealed record SearchLogsData(LogSearchCriteria Search, long Count, IReadOnlyList<LogCountBucket>? OverTime, string? TopFrom,
    LogTopValues? Top, IReadOnlyList<LogSearchLine>? Lines, string? LinesAre, string? WhyNone);
public sealed record SearchLogsResult(string Summary, SearchLogsData Data, int SampledMessages, bool NewestRead, long LinesOmitted);

/// <summary>Graylog search counts, samples and filters shared with MCP over host-owned clients.</summary>
public sealed class LogSearch(ILibreNmsClient client, IGraylogApi graylog, DeviceDirectory directory, AgentOptions options,
    TimeProvider time, Redactor redactor)
{
    public const int DefaultHours = 24;
    public const int MaxLines = 50;
    public const int PageSize = 150;
    public const int MaxOffset = 10000;
    public const int MaxLineLength = 300;
    public const int Buckets = 12;
    public const int TopValues = 5;
    public static readonly TimeSpan NowSlack = TimeSpan.FromMinutes(2);

    public static LogSearch Create(ILibreNmsClient client, IGraylogApi graylog, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(graylog);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        return new(client, graylog, new DeviceDirectory(client, time), options, time, new Redactor());
    }
    internal static TimeWindow Validate(bool configured, SearchLogsRequest request, TimeProvider time)
    {
        if (!configured) throw new ToolException("Graylog isn't configured on this server, so there are no logs to search. Set GRAYLOG_URL, GRAYLOG_USERNAME and GRAYLOG_PASSWORD to turn it on.");
        if (request.Limit is < 0 or > MaxLines) throw new ToolException($"Limit must be between 0 and {MaxLines}.");
        if (request.Level is < 0 or > 7) throw new ToolException("Level must be between 0 (emergency) and 7 (debug).");
        if (request.DeviceId is <= 0) throw new ToolException("A device ID must be positive.");
        return TimeWindow.Resolve(request.Since, request.Until, request.Hours, DefaultHours, time);
    }
    public async Task<SearchLogsResult> ReadAsync(SearchLogsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var window = Validate(graylog.IsConfigured, request, time);
        var now = time.GetUtcNow().UtcDateTime;
        Device? device = request.DeviceId is { } id
            ? (await directory.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == id) ?? throw new DeviceNotFoundException()
            : null;
        return await ReadAsync(request, device, window, now, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<SearchLogsResult> ReadAsync(SearchLogsRequest request, Device? device, TimeWindow window, DateTime now, CancellationToken cancellationToken)
    {
        var (text, _, _, _, _, level, streamQuery, limit) = request;
        IReadOnlyList<string>? addresses = device is null ? null : await GraylogDeviceAddresses.ReadAsync(client, device, options, cancellationToken).ConfigureAwait(false);

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
            .Select(i => new LogCountBucket(window.Since + (width * i), Math.Max(0, totals[i] - totals[i + 1])))
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

        cancellationToken.ThrowIfCancellationRequested();
        var lines = (newestRead ? read.OrderByDescending(m => m.Timestamp) : read.OrderBy(m => m.Timestamp)).Take(limit)
            .Select(m => new LogSearchLine(m.Timestamp?.UtcDateTime, Clean(m.Source), Blank(GraylogQuery.LevelText(m.Level)),
                Blank(GraylogQuery.FacilityText(m.Facility)), Clean(Blank(m.GetText("application_name"))), Cut(Clean(m.Text)))).ToList();
        var data = new SearchLogsData(new LogSearchCriteria(Clean(string.IsNullOrWhiteSpace(text) ? null : text.Trim()),
            device is null ? null : DeviceBrief.FromSummary(DeviceSummary.Redacted(device, options, redactor)),
            level is { } l ? GraylogQuery.LevelText(l) + " or more severe" : null, Clean(stream?.Title), window.Since, window.Until),
            count, count == 0 ? null : overTime,
            read.Count == 0 ? null : read.Count >= count ? "every message in the window" : $"the {(newestRead ? "newest" : "oldest")} {Count(read.Count)} of the window's {Count(count)} messages",
            read.Count == 0 ? null : new LogTopValues(Top(read, m => m.Source), Top(read, m => Blank(GraylogQuery.LevelText(m.Level))),
                Top(read, m => Blank(GraylogQuery.FacilityText(m.Facility))), Top(read, m => Blank(m.GetText("application_name")))),
            limit == 0 || lines.Count == 0 ? null : lines,
            !newestRead && lines.Count > 0 ? "the oldest in the window: the newest are past the furthest Graylog pages to; narrow the window for them" : null,
            count == 0 ? redactor.Redact(WhyNone(text, device, level, stream, window, now)) : null);

        var summary = count == 0
            ? "No messages match in this window."
            : $"{Count(count)} {(count == 1 ? "message matches" : "messages match")} in this window" +
              (lines.Count > 0 ? $"; {(newestRead ? "the newest" : "the oldest")} {lines.Count} {(lines.Count == 1 ? "is" : "are")} shown." : ".");

        return new(summary, data, read.Count, newestRead, Math.Max(0, count - lines.Count));
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

    private IReadOnlyList<LogTopValue>? Top(IReadOnlyList<GraylogMessage> read, Func<GraylogMessage, string?> value)
    {
        var top = read
            .Select(value)
            .OfType<string>()
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(TopValues)
            .Select(g => new LogTopValue(redactor.Redact(g.Key), g.Count()))
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
        => text is { Length: > MaxLineLength } ? text[..MaxLineLength] + "…" : text;

    internal static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
}
