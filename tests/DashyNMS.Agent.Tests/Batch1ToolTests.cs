using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>Paging, filters, verbose and time windows on the existing tools (#12, #34, #35, #57).</summary>
public sealed class Batch1ToolTests
{
    private readonly FakeLibreNms _nms = new();

    private DeviceTools Devices() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options);

    private AlertTools Alerts() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    private EventTools Events() => new(
        NSubstitute.Substitute.For<DesktopNMS.Core.Api.IUnimusApi>(),
        NSubstitute.Substitute.For<DesktopNMS.Core.Api.IGraylogApi>(),
        new DashyNMS.Agent.Devices.UnimusDirectory(NSubstitute.Substitute.For<DesktopNMS.Core.Api.IUnimusApi>(), _nms.Options, _nms.Time),
        _nms.Resolver,
        _nms.Timeline,
        _nms.Runner,
        _nms.Options,
        _nms.Time);

    private ScopeTools Scopes() => new(_nms.Directory, _nms.Scope, _nms.Runner);

    [Fact]
    public async Task Find_pages_through_every_match()
    {
        var first = FakeLibreNms.Parse(await Devices().FindDevice("sw", limit: 1, cancellationToken: CancellationToken.None));
        var second = FakeLibreNms.Parse(await Devices().FindDevice("sw", offset: 1, limit: 1, cancellationToken: CancellationToken.None));

        Assert.Equal("Showing 1–1 of 2 matching devices; pass offset 1 for the next page.", first.GetProperty("summary").GetString());
        Assert.Equal(1, first.GetProperty("data").GetProperty("page").GetProperty("next_offset").GetInt32());
        Assert.Equal("Showing 2–2 of 2 matching devices (the last page).", second.GetProperty("summary").GetString());
        Assert.NotEqual(
            first.GetProperty("data").GetProperty("devices")[0].GetProperty("device_id").GetInt32(),
            second.GetProperty("data").GetProperty("devices")[0].GetProperty("device_id").GetInt32());
    }

    [Fact]
    public async Task Find_returns_short_records_unless_verbose()
    {
        var brief = FakeLibreNms.Parse(await Devices().FindDevice("core-sw1", cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("devices")[0];
        var full = FakeLibreNms.Parse(await Devices().FindDevice("core-sw1", verbose: true, cancellationToken: CancellationToken.None)).GetProperty("data").GetProperty("devices")[0];

        Assert.False(brief.TryGetProperty("os", out _));
        Assert.False(brief.TryGetProperty("last_polled", out _));
        Assert.Equal("Paddock", brief.GetProperty("location").GetString());
        Assert.Equal("arubaos-cx", full.GetProperty("os").GetString());
        Assert.True(full.TryGetProperty("last_polled", out _));
    }

    [Theory]
    [InlineData(null, "down", null, null, 2)]
    [InlineData(null, null, "core switches", null, 1)]
    [InlineData(null, null, "edge", null, 2)]
    [InlineData(null, null, null, "pit", 2)]
    [InlineData("sw", "up", null, null, 1)]
    public async Task Find_filters_by_status_group_and_location(string? query, string? status, string? group, string? location, int expectedId)
    {
        var root = FakeLibreNms.Parse(await Devices().FindDevice(query, status, group, location, cancellationToken: CancellationToken.None));
        var devices = root.GetProperty("data").GetProperty("devices");

        Assert.Equal(1, devices.GetArrayLength());
        Assert.Equal(expectedId, devices[0].GetProperty("device_id").GetInt32());
    }

    [Fact]
    public async Task Find_needs_a_query_or_a_filter()
    {
        var root = FakeLibreNms.Parse(await Devices().FindDevice(cancellationToken: CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("switches", "2 device groups match.")]
    [InlineData("nowhere", "No device group matches.")]
    public async Task Ambiguous_or_unknown_groups_are_errors(string group, string message)
    {
        var root = FakeLibreNms.Parse(await Devices().FindDevice(group: group, cancellationToken: CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.StartsWith(message, root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_status_is_an_error()
        => Assert.Equal("error", FakeLibreNms.Parse(await Devices().FindDevice(status: "sleeping", cancellationToken: CancellationToken.None)).GetProperty("status").GetString());

    [Fact]
    public async Task Groups_and_locations_are_listed_with_counts()
    {
        var root = FakeLibreNms.Parse(await Scopes().ListGroupsAndLocations(CancellationToken.None));
        var data = root.GetProperty("data");

        Assert.Equal("3 device groups and 2 locations.", root.GetProperty("summary").GetString());
        Assert.Equal("Access", data.GetProperty("groups")[0].GetProperty("name").GetString());
        var pit = data.GetProperty("locations")[1];
        Assert.Equal("Pit lane", pit.GetProperty("name").GetString());
        Assert.Equal(1, pit.GetProperty("devices").GetInt32());
        Assert.Equal(1, pit.GetProperty("down").GetInt32());
    }

    [Fact]
    public async Task Open_alerts_filter_by_group_and_page()
    {
        var core = FakeLibreNms.Parse(await Alerts().ActiveAlerts(group: "core switches", cancellationToken: CancellationToken.None));
        var paged = FakeLibreNms.Parse(await Alerts().ActiveAlerts(limit: 1, cancellationToken: CancellationToken.None));

        Assert.StartsWith("2 open alerts on 1 device", core.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Showing 1–1 of 3 alerts; pass offset 1 for the next page.", paged.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_alerts_hide_notes_unless_verbose()
    {
        var brief = FakeLibreNms.Parse(await Alerts().ActiveAlerts(device: "core-sw1", cancellationToken: CancellationToken.None));
        var full = FakeLibreNms.Parse(await Alerts().ActiveAlerts(device: "core-sw1", verbose: true, cancellationToken: CancellationToken.None));

        Assert.False(brief.GetProperty("data").GetProperty("devices")[0].GetProperty("alerts")[1].TryGetProperty("note", out _));
        Assert.Equal("Looking into it", full.GetProperty("data").GetProperty("devices")[0].GetProperty("alerts")[1].GetProperty("note").GetString());
    }

    [Fact]
    public async Task Alerts_in_a_window_include_recovered_ones()
    {
        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(hours: 24, cancellationToken: CancellationToken.None));
        var data = root.GetProperty("data");

        Assert.Equal("In the last 24 hours: 2 alerts raised on 2 devices (1 critical, 1 warning); 1 still open.", root.GetProperty("summary").GetString());
        Assert.True(data.GetProperty("window").GetProperty("window_complete").GetBoolean());

        var port = data.GetProperty("devices")[0].GetProperty("alerts")[0];
        Assert.Equal("Port down", port.GetProperty("rule").GetString());
        Assert.True(port.GetProperty("still_open").GetBoolean());

        var overnight = data.GetProperty("devices")[1].GetProperty("alerts")[0];
        Assert.Equal("2026-10-17T02:00:00Z", overnight.GetProperty("raised_at").GetString());
        Assert.Equal("2026-10-17T03:30:00Z", overnight.GetProperty("recovered_at").GetString());
        Assert.Equal(90, overnight.GetProperty("lasted_minutes").GetInt64());
        Assert.False(overnight.GetProperty("still_open").GetBoolean());
    }

    [Fact]
    public async Task Alerts_in_a_past_window()
    {
        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(since: "2026-10-10T00:00:00Z", until: "2026-10-11T00:00:00Z", cancellationToken: CancellationToken.None));

        Assert.Equal("In the 24 hours to 2026-10-11 00:00 UTC: 1 alert raised on 1 device (0 critical, 1 warning); 0 still open.", root.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Events_in_a_past_window_and_paged()
    {
        var past = FakeLibreNms.Parse(await Events().DeviceEvents("core-sw1", since: "2026-10-17T09:00:00Z", until: "2026-10-17T09:35:00Z", cancellationToken: CancellationToken.None));
        var paged = FakeLibreNms.Parse(await Events().DeviceEvents("core-sw1", limit: 2, cancellationToken: CancellationToken.None));

        Assert.StartsWith("In the 35 minutes to 2026-10-17 09:35 UTC: 1 event, 0 alert changes, 1 outage.", past.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("2026-10-17T09:00:00Z", past.GetProperty("data").GetProperty("window").GetProperty("since").GetString());
        Assert.EndsWith("Showing 1–2 of 4 entries; pass offset 2 for the next page.", paged.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hours_and_since_together_are_an_error()
    {
        var root = FakeLibreNms.Parse(await Events().DeviceEvents("core-sw1", hours: 6, since: "6h", cancellationToken: CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task What_changed_skips_graylog_for_a_past_window()
    {
        var graylog = NSubstitute.Substitute.For<DesktopNMS.Core.Api.IGraylogApi>();
        graylog.IsConfigured.Returns(true);
        var unimus = NSubstitute.Substitute.For<DesktopNMS.Core.Api.IUnimusApi>();
        var tools = new EventTools(unimus, graylog, new DashyNMS.Agent.Devices.UnimusDirectory(unimus, _nms.Options, _nms.Time), _nms.Resolver, _nms.Timeline, _nms.Runner, _nms.Options, _nms.Time);

        var root = FakeLibreNms.Parse(await tools.WhatChanged("core-sw1", since: "2026-10-16T00:00:00Z", until: "2026-10-16T06:00:00Z", cancellationToken: CancellationToken.None));

        Assert.StartsWith("Graylog is only counted", root.GetProperty("data").GetProperty("graylog").GetProperty("skipped").GetString(), StringComparison.Ordinal);
        await graylog.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default, default, default);
    }
}
