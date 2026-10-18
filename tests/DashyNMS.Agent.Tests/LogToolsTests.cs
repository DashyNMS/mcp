using System.Text.Json;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Tests;

/// <summary>search_logs (#30), against a Graylog that answers relative searches from a list of messages.</summary>
public sealed class LogToolsTests
{
    private readonly IGraylogApi _graylog = Substitute.For<IGraylogApi>();
    private readonly List<(DateTimeOffset At, string Source, int Level, string Text)> _messages = [];

    public LogToolsTests()
    {
        _graylog.IsConfigured.Returns(true);
        _graylog.GetStreamsAsync(Arg.Any<CancellationToken>()).Returns([
            new GraylogStream { Id = "5f0a", Title = "Network devices" },
            new GraylogStream { Id = "5f0b", Title = "Firewalls" },
        ]);

        // Graylog's relative search: everything from "seconds" ago to now, sorted, then paged.
        _graylog.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var seconds = call.ArgAt<int>(1);
                var from = FakeLibreNms.Now.AddSeconds(-seconds);
                var matched = _messages.Where(m => m.At >= from && m.At <= FakeLibreNms.Now).ToList();
                var sorted = call.ArgAt<string?>(4) == "timestamp:asc" ? matched.OrderBy(m => m.At) : matched.OrderByDescending(m => m.At);
                return new GraylogSearchResult
                {
                    TotalResults = matched.Count,
                    Messages = sorted.Skip(call.ArgAt<int>(3)).Take(call.ArgAt<int>(2)).Select(m => new GraylogMessageEnvelope { Message = Message(m) }).ToList(),
                };
            });
    }

    private static GraylogMessage Message((DateTimeOffset At, string Source, int Level, string Text) m) => new(new Dictionary<string, JsonElement>
    {
        ["timestamp"] = JsonSerializer.SerializeToElement(m.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)),
        ["source"] = JsonSerializer.SerializeToElement(m.Source),
        ["level"] = JsonSerializer.SerializeToElement(m.Level),
        ["message"] = JsonSerializer.SerializeToElement(m.Text),
    });

    /// <summary>One message every <paramref name="every"/>, from <paramref name="hoursAgo"/> up to now.</summary>
    private void Every(TimeSpan every, double hoursAgo, string source = "core-sw1", int level = 6, string text = "link up")
    {
        for (var at = FakeLibreNms.Now.AddHours(-hoursAgo); at <= FakeLibreNms.Now; at += every)
        {
            _messages.Add((at, source, level, text));
        }
    }

    private LogTools Tools(AgentOptions? options = null)
    {
        var nms = new FakeLibreNms(options);
        return new LogTools(nms.Client, _graylog, nms.Resolver, nms.Runner, nms.Options, nms.Time);
    }

    [Fact]
    public async Task Window_ending_now_counts_exactly_and_shows_the_newest_lines()
    {
        Every(TimeSpan.FromMinutes(10), 30);

        var root = FakeLibreNms.Parse(await Tools().SearchLogs(hours: 24, limit: 3));
        var data = root.GetProperty("data");

        // 24 hours of one every 10 minutes, both ends included.
        Assert.Equal(145, data.GetProperty("count").GetInt64());
        Assert.Equal("145 messages match in this window; the newest 3 are shown.", root.GetProperty("summary").GetString());
        Assert.Equal(145, data.GetProperty("over_time").EnumerateArray().Sum(b => b.GetProperty("count").GetInt64()));
        Assert.Equal(12, data.GetProperty("over_time").GetArrayLength());
        Assert.Equal(FakeLibreNms.Now.UtcDateTime, data.GetProperty("lines")[0].GetProperty("time").GetDateTime());
        Assert.Equal("every message in the window", data.GetProperty("top_from").GetString());
    }

    [Fact]
    public async Task Past_window_is_counted_exactly_and_its_lines_come_from_inside_it()
    {
        // Busy after the window, quiet in it: the newest messages overall are no use.
        Every(TimeSpan.FromMinutes(1), 6, source: "edge-sw3");
        _messages.Add((FakeLibreNms.Now.AddHours(-30), "core-sw1", 3, "fan failure"));
        _messages.Add((FakeLibreNms.Now.AddHours(-29), "core-sw1", 4, "temperature high"));
        _messages.Add((FakeLibreNms.Now.AddHours(-40), "core-sw1", 6, "before the window"));

        var root = FakeLibreNms.Parse(await Tools().SearchLogs(since: "2026-10-16T02:00:00Z", until: "2026-10-16T06:00:00Z"));
        var data = root.GetProperty("data");

        Assert.Equal(2, data.GetProperty("count").GetInt64());
        Assert.Equal(["temperature high", "fan failure"], data.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray());
        Assert.Equal("(3) Error", data.GetProperty("lines")[1].GetProperty("level").GetString());
        Assert.Equal("every message in the window", data.GetProperty("top_from").GetString());
        Assert.Equal(2, data.GetProperty("top").GetProperty("levels").GetArrayLength());
    }

    [Fact]
    public async Task Old_window_under_a_busy_present_reads_its_oldest_and_says_so()
    {
        Every(TimeSpan.FromSeconds(2), 6, source: "edge-sw3");
        Every(TimeSpan.FromMinutes(1), 26, source: "core-sw1");

        var root = FakeLibreNms.Parse(await Tools().SearchLogs(since: "2026-10-16T08:00:00Z", until: "2026-10-16T09:00:00Z", limit: 1));
        var data = root.GetProperty("data");

        // 08:00 up to, not including, 09:00: the 09:00 message belongs to the next window.
        Assert.Equal(60, data.GetProperty("count").GetInt64());
        Assert.StartsWith("the oldest in the window", data.GetProperty("lines_are").GetString(), StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc), data.GetProperty("lines")[0].GetProperty("time").GetDateTime());
    }

    [Fact]
    public async Task Device_level_and_stream_narrow_the_query()
    {
        Every(TimeSpan.FromHours(1), 5);

        var root = FakeLibreNms.Parse(await Tools().SearchLogs(device: "core-sw1", level: 3, stream: "network", text: "link"));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("(3) Error or more severe", root.GetProperty("data").GetProperty("search").GetProperty("level").GetString());
        Assert.Equal("Network devices", root.GetProperty("data").GetProperty("search").GetProperty("stream").GetString());
        await _graylog.Received().SearchAsync(
            Arg.Is<string>(q => q.StartsWith("message:\"link\" && source: (", StringComparison.Ordinal) && q.Contains("\"10.0.0.1\"", StringComparison.Ordinal) && q.EndsWith(" AND level: <=3", StringComparison.Ordinal)),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), "streams:5f0a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_stream_lists_the_streams()
    {
        var root = FakeLibreNms.Parse(await Tools().SearchLogs(stream: "printers"));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("details").GetProperty("streams").GetArrayLength());
    }

    [Fact]
    public async Task Nothing_matching_says_what_might_be_why()
    {
        var root = FakeLibreNms.Parse(await Tools().SearchLogs(device: "core-sw1", hours: 2));
        var data = root.GetProperty("data");

        Assert.Equal("No messages match in this window.", root.GetProperty("summary").GetString());
        Assert.Equal(0, data.GetProperty("count").GetInt64());
        Assert.Contains("GRAYLOG_MATCH_ANY_ADDRESS=true", data.GetProperty("why_none").GetString(), StringComparison.Ordinal);
        Assert.False(data.TryGetProperty("lines", out _));
    }

    [Fact]
    public async Task Long_lines_are_cut_and_secrets_redacted()
    {
        _messages.Add((FakeLibreNms.Now.AddMinutes(-5), "core-sw1", 5, "snmp-server community S3cr3tC0mm RO " + new string('x', 400)));

        var line = FakeLibreNms.Parse(await Tools().SearchLogs(hours: 1)).GetProperty("data").GetProperty("lines")[0].GetProperty("text").GetString()!;

        Assert.DoesNotContain("S3cr3tC0mm", line, StringComparison.Ordinal);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
        Assert.True(line.Length < 320);
    }

    [Fact]
    public async Task Not_configured_is_an_error_naming_the_settings()
    {
        _graylog.IsConfigured.Returns(false);

        var root = FakeLibreNms.Parse(await Tools().SearchLogs());

        Assert.Contains("GRAYLOG_URL", root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(51, null)]
    [InlineData(-1, null)]
    [InlineData(20, 8)]
    public async Task Bad_limit_or_level_is_an_error(int limit, int? level)
    {
        var root = FakeLibreNms.Parse(await Tools().SearchLogs(limit: limit, level: level));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }
}
