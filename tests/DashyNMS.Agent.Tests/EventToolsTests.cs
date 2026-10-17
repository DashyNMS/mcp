using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Tests;

public sealed class EventToolsTests
{
    private readonly FakeLibreNms _nms = new();
    private readonly IUnimusApi _unimus = Substitute.For<IUnimusApi>();
    private readonly IGraylogApi _graylog = Substitute.For<IGraylogApi>();

    private EventTools Tools() => new(_unimus, _graylog, _nms.Resolver, _nms.Timeline, _nms.Runner, _nms.Options, _nms.Time);

    [Fact]
    public async Task Events_merge_the_logs_newest_first_inside_the_window()
    {
        var root = FakeLibreNms.Parse(await Tools().DeviceEvents("core-sw1", 24, CancellationToken.None));

        Assert.Equal("In the last 24 hours: 1 event, 1 alert change, 1 outage.", root.GetProperty("summary").GetString());
        var entries = root.GetProperty("data").GetProperty("entries");
        string[] expected = ["alertlog", "eventlog", "outage", "outage"];
        Assert.Equal(expected, entries.EnumerateArray().Select(e => e.GetProperty("source").GetString()).ToArray());

        Assert.Equal("Port down", entries[0].GetProperty("rule").GetString());
        Assert.Equal("active", entries[0].GetProperty("kind").GetString());
        Assert.Equal("Interface 1/1/7 went down", entries[1].GetProperty("text").GetString());
        Assert.Equal("warning", entries[1].GetProperty("severity").GetString());
        Assert.Equal("came_back", entries[2].GetProperty("kind").GetString());
        Assert.Equal(10, entries[2].GetProperty("duration_minutes").GetInt64());
        Assert.Equal("2026-10-17T09:00:00Z", entries[3].GetProperty("time").GetString());
    }

    [Fact]
    public async Task A_longer_window_reaches_older_events()
    {
        var root = FakeLibreNms.Parse(await Tools().DeviceEvents("core-sw1", 168, CancellationToken.None));

        Assert.StartsWith("In the last 168 hours: 2 events", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public async Task Hours_out_of_range_is_an_error(int hours)
    {
        var root = FakeLibreNms.Parse(await Tools().DeviceEvents("core-sw1", hours, CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task What_changed_says_which_sources_are_not_configured()
    {
        var root = FakeLibreNms.Parse(await Tools().WhatChanged("core-sw1", 24, CancellationToken.None));

        Assert.EndsWith("Unimus isn't configured. Graylog isn't configured.", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("data").GetProperty("unimus").GetProperty("configured").GetBoolean());
        Assert.False(root.GetProperty("data").GetProperty("graylog").GetProperty("configured").GetBoolean());
        Assert.Equal(4, root.GetProperty("data").GetProperty("librenms").GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task What_changed_returns_a_redacted_unimus_diff_and_graylog_count()
    {
        _unimus.IsConfigured.Returns(true);
        _unimus.ListAllDevicesAsync(Arg.Any<CancellationToken>()).Returns([new UnimusDevice { Id = 40, Address = "10.0.0.1", Description = "core-sw1" }]);
        _unimus.GetBackupsAsync(40, 0, 10, Arg.Any<CancellationToken>()).Returns(new UnimusBackupPage
        {
            Backups =
            [
                new UnimusBackup { Id = 900, ValidSince = FakeLibreNms.Now.AddHours(-30).ToUnixTimeSeconds(), ValidUntil = FakeLibreNms.Now.AddHours(-2).ToUnixTimeSeconds() },
                new UnimusBackup { Id = 901, ValidSince = FakeLibreNms.Now.AddHours(-2).ToUnixTimeSeconds(), ValidUntil = FakeLibreNms.Now.ToUnixTimeSeconds() },
            ],
        });
        _unimus.GetDiffAsync(900, 901, Arg.Any<CancellationToken>()).Returns(new UnimusBackupDiff
        {
            LineGroups =
            [
                new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new UnimusDiffLine { Text = "hostname core-sw1" }] },
                new UnimusDiffLineGroup
                {
                    Type = "CHANGED",
                    OriginalLines = [new UnimusDiffLine { Text = "snmp-server community OldC0mm RO" }],
                    RevisedLines = [new UnimusDiffLine { Text = "snmp-server community NewC0mm RO" }],
                },
            ],
        });

        _graylog.IsConfigured.Returns(true);
        _graylog.SearchAsync(Arg.Any<string>(), 24 * 3600, 10, 0, "timestamp:desc", null, Arg.Any<CancellationToken>())
            .Returns(new GraylogSearchResult { TotalResults = 42 });

        var root = FakeLibreNms.Parse(await Tools().WhatChanged("core-sw1", 24, CancellationToken.None));
        var unimus = root.GetProperty("data").GetProperty("unimus");

        Assert.True(unimus.GetProperty("config_changed").GetBoolean());
        Assert.Equal(
            $"- snmp-server community {Redactor.Marker} RO\n+ snmp-server community {Redactor.Marker} RO",
            unimus.GetProperty("config_diff").GetString());
        Assert.Equal(42, root.GetProperty("data").GetProperty("graylog").GetProperty("message_count").GetInt64());
        Assert.Contains("Unimus shows a config change (2 changed lines). Graylog has 42 messages from it.", root.GetProperty("summary").GetString(), StringComparison.Ordinal);

        // The Graylog search matches the device's addresses in the source field, as LibreNMS does.
        await _graylog.Received(1).SearchAsync(
            Arg.Is<string>(q => q.StartsWith("source:", StringComparison.Ordinal) && q.Contains("10.0.0.1", StringComparison.Ordinal)),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_diff_when_the_newest_backup_is_older_than_the_window()
    {
        _unimus.IsConfigured.Returns(true);
        _unimus.ListAllDevicesAsync(Arg.Any<CancellationToken>()).Returns([new UnimusDevice { Id = 40, Address = "10.0.0.1" }]);
        _unimus.GetBackupsAsync(40, 0, 10, Arg.Any<CancellationToken>()).Returns(new UnimusBackupPage
        {
            Backups =
            [
                new UnimusBackup { Id = 900, ValidSince = FakeLibreNms.Now.AddDays(-9).ToUnixTimeSeconds() },
                new UnimusBackup { Id = 901, ValidSince = FakeLibreNms.Now.AddDays(-3).ToUnixTimeSeconds() },
            ],
        });

        var unimus = FakeLibreNms.Parse(await Tools().WhatChanged("core-sw1", 24, CancellationToken.None)).GetProperty("data").GetProperty("unimus");

        Assert.False(unimus.GetProperty("config_changed").GetBoolean());
        await _unimus.DidNotReceive().GetDiffAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_source_that_fails_says_so_and_the_rest_stands()
    {
        _unimus.IsConfigured.Returns(true);
        _unimus.ListAllDevicesAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<UnimusDevice>>(_ => throw new UnimusApiException("Unimus is down."));

        var root = FakeLibreNms.Parse(await Tools().WhatChanged("core-sw1", 24, CancellationToken.None));

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.StartsWith("Unimus:", root.GetProperty("data").GetProperty("unimus").GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(4, root.GetProperty("data").GetProperty("librenms").GetProperty("entries").GetArrayLength());
    }
}
