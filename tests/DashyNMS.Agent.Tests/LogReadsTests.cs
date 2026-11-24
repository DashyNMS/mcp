using System.Globalization;
using System.Net;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Events;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

/// <summary>Reading a log back over a window on a network too busy to read it in one go (#70).</summary>
public sealed class LogReadsTests
{
    private static readonly DateTime Now = FakeLibreNms.Now.UtcDateTime;

    private readonly FakeTimeProvider _time = new(FakeLibreNms.Now);

    private static TimeWindow Last(TimeSpan span) => new(Now - span, Now);

    private Task<Fetched<DeviceTimeline.ReadBack<EventLogEntry>>> Read(
        BusyLog log, TimeWindow window, Func<EventLogEntry, bool>? keep = null, AgentOptions? options = null)
        => new LogReads(options ?? new AgentOptions(), _time).EventsAsync(log, null, window, keep, CancellationToken.None);

    [Fact]
    public async Task A_filtered_week_on_a_busy_network_is_complete()
    {
        // An event every 10 seconds for ten days, a reboot every hour.
        var log = BusyLog.Every(TimeSpan.FromSeconds(10), TimeSpan.FromDays(10), i => i % 360 == 0 ? "reboot" : "interface");
        var window = Last(TimeSpan.FromDays(7));

        var read = (await Read(log, window, e => e.Type == "reboot" && window.Contains(Utc(e)))).Value!;

        Assert.True(read.CoveredSince <= window.Since);
        Assert.Null(read.Stopped);
        Assert.Equal((7 * 24) + 1, read.Items.Count);
        Assert.Equal(read.Items.Count, read.Items.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task Entries_sharing_a_second_across_a_page_edge_are_neither_missed_nor_doubled()
    {
        // Seven to a second, so no page ends where a second does.
        var log = new BusyLog(Enumerable.Range(0, 30_000).Select(i => (Now.AddSeconds(-(i / 7)), "interface")));
        var window = Last(TimeSpan.FromMinutes(20));

        var read = (await Read(log, window, e => window.Contains(Utc(e)))).Value!;

        Assert.Null(read.Stopped);
        Assert.Equal(7 * 1201, read.Items.Count);
        Assert.Equal(read.Items.Count, read.Items.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task More_in_one_second_than_a_page_are_read_with_a_bigger_one()
    {
        var log = new BusyLog(Enumerable.Range(0, 5000).Select(_ => (Now.AddMinutes(-1), "interface"))
            .Concat(Enumerable.Range(0, 10).Select(i => (Now.AddMinutes(-2 - i), "interface"))));

        var read = (await Read(log, Last(TimeSpan.FromHours(1)))).Value!;

        Assert.Null(read.Stopped);
        Assert.Equal(5010, read.Items.Count);
    }

    [Fact]
    public async Task An_unfiltered_question_stops_at_the_limit_and_says_where()
    {
        var log = BusyLog.Every(TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), _ => "interface");
        var window = Last(TimeSpan.FromDays(1));

        var read = (await Read(log, window)).Value!;

        Assert.Equal(LogReads.ReadLimit, read.Stopped);
        Assert.True(read.CoveredSince > window.Since);
        Assert.All(read.Items, e => Assert.True(Utc(e) >= read.CoveredSince.AddSeconds(-1)));

        // Asking for the rest, as the gap says to, covers it without a hole.
        var rest = (await Read(log, new TimeWindow(read.CoveredSince.AddHours(-2), read.CoveredSince))).Value!;
        Assert.Null(rest.Stopped);
        Assert.Contains(rest.Items, e => Utc(e) == read.CoveredSince);
    }

    [Fact]
    public async Task A_window_in_the_past_starts_at_its_end()
    {
        var log = BusyLog.Every(TimeSpan.FromSeconds(1), TimeSpan.FromDays(2), _ => "interface");
        var window = new TimeWindow(Now.AddHours(-30), Now.AddHours(-29.9));

        var read = (await Read(log, window)).Value!;

        Assert.Null(read.Stopped);
        Assert.Equal(361, read.Items.Count(e => window.Contains(Utc(e))));
        Assert.True(log.Asked.Sum() < 5000, string.Join(", ", log.Asked));
    }

    [Fact]
    public async Task A_window_in_the_past_allows_for_a_server_keeping_local_time()
    {
        var log = BusyLog.Every(TimeSpan.FromMinutes(1), TimeSpan.FromDays(2), _ => "interface");
        log.Shift = time => time.ToLocalTime();
        var options = new AgentOptions { ServerTimestampsAreUtc = false };
        var window = new TimeWindow(Now.AddHours(-30), Now.AddHours(-29));

        var read = (await Read(log, window, e => Sources.Utc(e.Timestamp, options) is { } at && window.Contains(at), options)).Value!;

        Assert.Null(read.Stopped);
        Assert.Equal(61, read.Items.Count);
    }

    [Fact]
    public async Task Reading_that_takes_too_long_stops_and_says_so()
    {
        var log = BusyLog.Every(TimeSpan.FromSeconds(1), TimeSpan.FromDays(2), _ => "interface");
        log.Reading = () => _time.Advance(TimeSpan.FromSeconds(7));

        var read = (await Read(log, Last(TimeSpan.FromDays(1)), _ => false)).Value!;

        Assert.Equal(LogReads.TimeLimit, read.Stopped);
        Assert.Equal(3, log.Asked.Count);
    }

    [Fact]
    public async Task A_failure_part_way_keeps_what_was_read()
    {
        var log = BusyLog.Every(TimeSpan.FromSeconds(1), TimeSpan.FromDays(2), _ => "interface");
        log.Reading = () =>
        {
            if (log.Asked.Count == 3)
            {
                throw new LibreNmsApiException("Gateway timeout", HttpStatusCode.GatewayTimeout);
            }
        };

        var read = await Read(log, Last(TimeSpan.FromDays(1)));

        Assert.Null(read.Error);
        Assert.Equal(LogReads.SourceFailed, read.Value!.Stopped);
        Assert.NotEmpty(read.Value.Items);
    }

    [Fact]
    public async Task A_failure_at_the_start_is_the_sources_error()
    {
        var log = BusyLog.Every(TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), _ => "interface");
        log.Reading = () => throw new LibreNmsApiException("Forbidden", HttpStatusCode.Forbidden);

        Assert.NotNull((await Read(log, Last(TimeSpan.FromDays(1)))).Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_log_that_cannot_be_read_back_from_a_time_is_read_as_before(bool ignoresTime)
    {
        var log = BusyLog.Every(TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), _ => "interface");
        log.IgnoresTime = ignoresTime;
        log.CanReadUpTo = ignoresTime;

        // In the past, so a server that ignores the time gives itself away at once.
        var window = new TimeWindow(Now.AddHours(-20), Now.AddHours(-10));
        var read = (await Read(log, window)).Value!;

        Assert.Equal([200, 1000, 2000], log.Asked.TakeLast(3));
        Assert.True(read.CoveredSince > window.Since);
        Assert.Null(read.Stopped);
    }

    [Fact]
    public async Task A_short_log_covers_everything()
    {
        var log = BusyLog.Every(TimeSpan.FromHours(1), TimeSpan.FromDays(1), _ => "interface");

        var read = (await Read(log, Last(TimeSpan.FromDays(30)))).Value!;

        Assert.Equal(DateTime.MinValue, read.CoveredSince);
        Assert.Single(log.Asked);
    }

    [Fact]
    public async Task A_window_not_read_in_full_names_the_part_missing_and_what_to_do()
    {
        var nms = new FakeLibreNms();
        nms.Client.Logs.Returns(BusyLog.Every(TimeSpan.FromSeconds(1), TimeSpan.FromDays(8), _ => "interface"));
        var history = new NetworkEventHistory(nms.Client, nms.Fleet, nms.Directory, nms.Scope, nms.Options, nms.Time, new Redactor());

        var busy = await history.ReadAsync(new NetworkEventsRequest(Hours: 168));

        Assert.False(busy.Data.Window.WindowComplete);
        var gap = busy.Data.Window.NotCovered!;
        Assert.Equal(busy.Data.Window.Since, gap.Since);
        Assert.Equal(busy.Data.Window.CoveredSince, gap.Until);
        Assert.Equal("read_limit", gap.Reason);
        Assert.Contains("the start of the window isn't covered", busy.Summary, StringComparison.Ordinal);
        Assert.Contains("until", busy.Summary, StringComparison.Ordinal);

        var quiet = await history.ReadAsync(new NetworkEventsRequest(Hours: 24, Type: "reboot"));
        Assert.True(quiet.Data.Window.WindowComplete);
        Assert.Null(quiet.Data.Window.NotCovered);
        Assert.Equal(0, quiet.Data.Page.Total);
    }

    private static DateTime Utc(EventLogEntry e) => DateTime.SpecifyKind(e.Timestamp!.Value, DateTimeKind.Utc);

    /// <summary>LibreNMS's event log: newest first, at most so many, optionally at or before a time to the second.</summary>
    private sealed class BusyLog : ILogsApi
    {
        private readonly List<(int Id, DateTime Time, string Type)> _entries;

        public BusyLog(IEnumerable<(DateTime Time, string Type)> newestFirst)
        {
            var entries = newestFirst.ToList();
            _entries = entries.Select((e, i) => (entries.Count - i, e.Time, e.Type)).ToList();
        }

        public static BusyLog Every(TimeSpan gap, TimeSpan span, Func<int, string> type)
            => new(Enumerable.Range(0, (int)(span / gap)).Select(i => (Now - (gap * i), type(i))));

        public List<int> Asked { get; } = [];

        public Action? Reading { get; set; }

        public Func<DateTime, DateTime> Shift { get; set; } = time => time;

        public bool IgnoresTime { get; set; }

        public bool CanReadUpTo { get; set; } = true;

        public Task<IReadOnlyList<EventLogEntry>> ListEventLogAsync(int? deviceId, int limit = 50, CancellationToken cancellationToken = default)
            => Answer(_entries, limit);

        public Task<IReadOnlyList<EventLogEntry>> ListEventLogUpToAsync(int? deviceId, DateTime upTo, int limit, CancellationToken cancellationToken = default)
            => CanReadUpTo
                ? Answer(IgnoresTime ? _entries : _entries.Where(e => Shift(e.Time) <= upTo), limit)
                : throw new NotSupportedException();

        public Task<IReadOnlyList<AlertLogEntry>> ListAlertLogAsync(int? deviceId, int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlertLogEntry>>([]);

        public Task<AlertLogEntry?> GetLatestForRuleAsync(int deviceId, int ruleId, int searchDepth = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertLogEntry?>(null);

        private Task<IReadOnlyList<EventLogEntry>> Answer(IEnumerable<(int Id, DateTime Time, string Type)> entries, int limit)
        {
            Asked.Add(limit);
            Reading?.Invoke();
            return Task.FromResult<IReadOnlyList<EventLogEntry>>(entries.Take(limit).Select(e => new EventLogEntry
            {
                Id = e.Id,
                DeviceId = 1,
                // As the server writes it: no zone, whole seconds.
                Timestamp = DateTime.ParseExact(Shift(e.Time).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Type = e.Type,
            }).ToList());
        }
    }
}
