using System.Text.Json;
using DashyNMS.Agent.Events;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

public sealed class SharedEventLookupsTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "EventHistory");
    private static Dictionary<string, Answer> Routes()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "routes.json")));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => new Answer(200, p.Value.GetRawText()));
    }
    private static ServiceProvider Connected(Uri root)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(FakeLibreNms.Now));
        var provider = services.AddDashyNmsAgent().BuildServiceProvider();
        provider.GetRequiredService<ILibreNmsClient>().Connect(new LibreNmsConnection(root, FixtureServer.Token));
        return provider;
    }
    private static HostEventLookups Host(ServiceProvider provider) => HostEventLookups.Create(
        provider.GetRequiredService<ILibreNmsClient>(), provider.GetRequiredService<IUnimusApi>(), provider.GetRequiredService<IGraylogApi>(),
        provider.GetRequiredService<TimeProvider>(), new());

    [Theory]
    [InlineData("device")]
    [InlineData("network")]
    [InlineData("raised")]
    [InlineData("changes")]
    public async Task History_matches_independent_facts_and_MCP_over_HTTP(string mode)
    {
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var operation = Host(host);
        JsonElement expected;
        JsonElement actual;
        string summary;
        switch (mode)
        {
            case "device":
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<EventTools>(mcp).DeviceEvents("1"));
                var device = (await operation.ReadDeviceAsync(new(1))).Result!;
                Assert.True(device.Data.Window.WindowComplete);
                Assert.Contains(device.Data.Entries, e => e.Source == "outage");
                Assert.Contains(device.Data.Entries, e => e.Text == "Interface 1/1/7 went down");
                actual = JsonSerializer.SerializeToElement(device.Data, ToolResult.Json);
                summary = device.Summary;
                break;
            case "network":
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<NetworkTools>(mcp).NetworkEvents(log: "both"));
                var network = (await operation.ReadNetworkAsync(new(Log: "both"))).Result!;
                Assert.True(network.Data.Window.WindowComplete);
                Assert.Contains(network.Data.Entries, e => e.Source == "eventlog");
                Assert.Contains(network.Data.Entries, e => e.Source == "alertlog");
                actual = JsonSerializer.SerializeToElement(network.Data, ToolResult.Json);
                summary = network.Summary;
                break;
            case "raised":
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<AlertTools>(mcp).ActiveAlerts(hours: 24));
                var raised = (await operation.ReadRaisedAlertsAsync(new(Hours: 24))).Result!;
                Assert.Equal(2, raised.Data.Page.Total);
                Assert.Contains(raised.Data.Devices.SelectMany(d => d.Alerts), a => a.RecoveredAt is not null && a.StillOpen == false);
                Assert.Contains(raised.Data.Devices.SelectMany(d => d.Alerts), a => a.StillOpen == true);
                actual = JsonSerializer.SerializeToElement(raised.Data, ToolResult.Json);
                summary = raised.Summary;
                break;
            default:
                expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<EventTools>(mcp).WhatChanged("1"));
                var changes = (await operation.ReadChangesAsync(new(1))).Result!;
                Assert.False(changes.Data.Unimus.Configured);
                Assert.False(changes.Data.Graylog.Configured);
                Assert.NotEmpty(changes.Data.Librenms.Entries);
                actual = JsonSerializer.SerializeToElement(changes.Data, ToolResult.Json);
                summary = changes.Summary;
                break;
        }
        Assert.Equal(expected.GetProperty("summary").GetString(), summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), actual));
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("warning")]
    public async Task Severity_filtered_history_fails_when_rules_are_unavailable(string severity)
    {
        var routes = Routes();
        routes["rules"] = new(403, "{}");
        using var server = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var provider = Connected(server.Root);
        using var host = Host(provider);
        var outcome = await host.ReadRaisedAlertsAsync(new(Severity: severity, Hours: 24));
        Assert.Equal(HostFailure.Unavailable, outcome.Error);
        Assert.Null(outcome.Result);
        var mcp = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<AlertTools>(provider).ActiveAlerts(severity: severity, hours: 24));
        Assert.Equal("error", mcp.GetProperty("status").GetString());
        Assert.Contains("severities couldn't be read", mcp.GetProperty("message").GetString()!);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unavailable_history_enrichment_reports_unknown_counts(bool rulesFailed, bool openFailed)
    {
        var routes = Routes();
        if (rulesFailed) routes["rules"] = new(403, "{}");
        if (openFailed) routes["alerts"] = new(403, "{}");
        using var server = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var provider = Connected(server.Root);
        using var host = Host(provider);
        var result = (await host.ReadRaisedAlertsAsync(new(Hours: 24))).Result!;
        Assert.Equal(2, result.Data.Page.Total);
        Assert.Equal(rulesFailed, result.RulesUnavailable);
        Assert.Equal(openFailed, result.OpenStatusUnavailable);
        if (rulesFailed)
        {
            Assert.Contains("severities unknown", result.Summary);
            Assert.DoesNotContain("0 critical", result.Summary);
        }
        if (openFailed)
        {
            Assert.Contains("open status unknown", result.Summary);
            Assert.DoesNotContain("0 still open", result.Summary);
            Assert.All(result.Data.Devices.SelectMany(d => d.Alerts), a => Assert.Null(a.StillOpen));
        }
    }

    [Theory]
    [InlineData("logs/eventlog/1", "event log")]
    [InlineData("logs/alertlog/1", "alert log")]
    [InlineData("devices/1/outages", "outages")]
    public async Task Every_failed_history_source_makes_common_coverage_unknown(string path, string source)
    {
        var routes = Routes();
        routes[path] = new(403, "{}");
        using var server = new FixtureServer(p => routes.GetValueOrDefault(p));
        using var provider = Connected(server.Root);
        using var host = Host(provider);
        var events = (await host.ReadDeviceAsync(new(1))).Result!;
        Assert.False(events.Data.Window.WindowComplete);
        Assert.Null(events.Data.Window.CoveredSince);
        Assert.Equal(source, Assert.Single(events.Data.Unavailable!).Source);
        Assert.Contains("coverage is unknown", events.Summary);
        var changes = (await host.ReadChangesAsync(new(1))).Result!;
        Assert.False(changes.Data.Window.WindowComplete);
        Assert.Null(changes.Data.Window.CoveredSince);
        Assert.Equal(source, Assert.Single(changes.Data.Librenms.Unavailable!).Source);
        Assert.Contains("coverage is unknown", changes.Summary);
        var mcp = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<EventTools>(provider).DeviceEvents("1"));
        Assert.False(mcp.GetProperty("data").GetProperty("window").GetProperty("window_complete").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Config_change_without_a_comparable_baseline_is_unknown(bool hasInWindowBackup)
    {
        var nms = new FakeLibreNms();
        var unimus = Substitute.For<IUnimusApi>();
        var graylog = Substitute.For<IGraylogApi>();
        unimus.IsConfigured.Returns(true);
        unimus.ListAllDevicesAsync(Arg.Any<CancellationToken>()).Returns(new[] { new UnimusDevice { Id = 40, Address = "10.0.0.1" } });
        unimus.GetBackupsAsync(40, 0, 10, Arg.Any<CancellationToken>()).Returns(new UnimusBackupPage
        {
            Backups = hasInWindowBackup
                ? [new UnimusBackup { Id = 901, ValidSince = FakeLibreNms.Now.AddHours(-2).ToUnixTimeSeconds() }]
                : [],
        });
        // The optional log source remains usable while the config comparison is unknown.
        graylog.IsConfigured.Returns(true);
        graylog.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new GraylogSearchResult { TotalResults = 4 });
        using var host = HostEventLookups.Create(nms.Client, unimus, graylog, nms.Time, nms.Options);
        var result = (await host.ReadChangesAsync(new(1, Hours: 24))).Result!;
        Assert.True(result.Data.Unimus.Configured);
        Assert.True(result.Data.Unimus.DeviceFound);
        Assert.Null(result.Data.Unimus.ConfigChanged);
        Assert.Null(result.Data.Unimus.ChangedLines);
        Assert.NotNull(result.Data.Unimus.Warning);
        Assert.Contains("Config change in Unimus is unknown", result.Summary);
        Assert.DoesNotContain("No config change", result.Summary);
        Assert.Equal(4L, result.Data.Graylog.MessageCount);
        Assert.NotEmpty(result.Data.Librenms.Entries);
        var tools = new EventTools(nms.Client, unimus, graylog, new DashyNMS.Agent.Devices.UnimusDirectory(unimus, nms.Options, nms.Time),
            nms.Resolver, nms.Timeline, nms.Runner, nms.Options, nms.Time);
        var mcp = FakeLibreNms.Parse(await tools.WhatChanged("1", hours: 24));
        Assert.False(mcp.GetProperty("data").GetProperty("unimus").TryGetProperty("config_changed", out _));
        Assert.Contains("Config change in Unimus is unknown", mcp.GetProperty("summary").GetString()!);
        await unimus.DidNotReceive().GetDiffAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Config_history_reads_later_pages_and_keeps_capped_searches_unknown(bool capped)
    {
        var nms = new FakeLibreNms();
        var unimus = Substitute.For<IUnimusApi>();
        unimus.IsConfigured.Returns(true);
        unimus.ListAllDevicesAsync(Arg.Any<CancellationToken>()).Returns(new[] { new UnimusDevice { Id = 40, Address = "10.0.0.1" } });
        unimus.GetBackupsAsync(40, Arg.Any<int>(), WhatChanged.BackupPageSize, Arg.Any<CancellationToken>()).Returns(call =>
        {
            var page = call.ArgAt<int>(1);
            return new UnimusBackupPage { Page = page, TotalPages = capped ? 100 : 2,
                Backups = page == 0 || capped
                    ? Enumerable.Range(0, 10).Select(i => new UnimusBackup { Id = 1000 + page * 10 + i,
                        ValidSince = FakeLibreNms.Now.AddMinutes(-page * 10 - i).ToUnixTimeSeconds() }).ToList()
                    : [new UnimusBackup { Id = 901, ValidSince = FakeLibreNms.Now.AddHours(-30).ToUnixTimeSeconds() },
                       new UnimusBackup { Id = 900, ValidSince = FakeLibreNms.Now.AddHours(-60).ToUnixTimeSeconds() }] };
        });
        unimus.GetDiffAsync(900, 901, Arg.Any<CancellationToken>()).Returns(new UnimusBackupDiff
        { LineGroups = [new UnimusDiffLineGroup { Type = "CHANGED", RevisedLines = [new() { Text = "vlan 120" }] }] });
        using var host = HostEventLookups.Create(nms.Client, unimus, Substitute.For<IGraylogApi>(), nms.Time, nms.Options);
        var result = (await host.ReadChangesAsync(new(1, Since: FakeLibreNms.Now.AddHours(-48).ToString("O"),
            Until: FakeLibreNms.Now.AddHours(-24).ToString("O")))).Result!;
        if (capped)
        {
            Assert.Null(result.Data.Unimus.ConfigChanged);
            Assert.Contains("bounded backup search", result.Data.Unimus.Warning!);
            await unimus.Received(WhatChanged.MaxBackupPages).GetBackupsAsync(40, Arg.Any<int>(), 10, Arg.Any<CancellationToken>());
            await unimus.DidNotReceive().GetDiffAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.True(result.Data.Unimus.ConfigChanged);
            Assert.Equal(1, result.Data.Unimus.ChangedLines);
            await unimus.Received(1).GetBackupsAsync(40, 1, 10, Arg.Any<CancellationToken>());
            await unimus.Received(1).GetDiffAsync(900, 901, Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData("<secret\n>")]
    [InlineData("<secret>\n<?note > </secret> ?>")]
    [InlineData("<secret>\n<?note >\n</secret>\n?>")]
    public async Task Change_history_masks_secrets_with_common_XML_context(string opening)
    {
        var nms = new FakeLibreNms();
        var unimus = Substitute.For<IUnimusApi>();
        var graylog = Substitute.For<IGraylogApi>();
        unimus.IsConfigured.Returns(true);
        unimus.ListAllDevicesAsync(Arg.Any<CancellationToken>()).Returns(new[] { new UnimusDevice { Id = 40, Address = "10.0.0.1" } });
        unimus.GetBackupsAsync(40, 0, 10, Arg.Any<CancellationToken>()).Returns(new UnimusBackupPage { Backups =
        [new UnimusBackup { Id = 901, ValidSince = FakeLibreNms.Now.AddHours(-2).ToUnixTimeSeconds() },
         new UnimusBackup { Id = 900, ValidSince = FakeLibreNms.Now.AddHours(-30).ToUnixTimeSeconds() }] });
        unimus.GetDiffAsync(900, 901, Arg.Any<CancellationToken>()).Returns(new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = opening.Split('\n').Select(t => new UnimusDiffLine { Text = t }).ToList() },
            new UnimusDiffLineGroup { Type = "CHANGED", OriginalLines = [new() { Text = "SyntheticOldBody" }],
                RevisedLines = [new() { Text = "SyntheticNewBody" }] },
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = "</secret>" }] },
        ] });
        using var host = HostEventLookups.Create(nms.Client, unimus, graylog, nms.Time, nms.Options);
        var result = (await host.ReadChangesAsync(new(1, Hours: 24))).Result!;
        var expected = $"- {DashyNMS.Agent.Redaction.Redactor.Marker}\n+ {DashyNMS.Agent.Redaction.Redactor.Marker}";
        Assert.Equal(expected, result.Data.Unimus.ConfigDiff);
        var tools = new EventTools(nms.Client, unimus, graylog, new DashyNMS.Agent.Devices.UnimusDirectory(unimus, nms.Options, nms.Time),
            nms.Resolver, nms.Timeline, nms.Runner, nms.Options, nms.Time);
        var mcp = FakeLibreNms.Parse(await tools.WhatChanged("1", hours: 24));
        Assert.Equal(expected, mcp.GetProperty("data").GetProperty("unimus").GetProperty("config_diff").GetString());
        Assert.DoesNotContain("Synthetic", mcp.GetRawText());
    }

    [Fact]
    public async Task A_busy_log_reports_an_incomplete_window_and_host_retirement_refuses_cached_reads()
    {
        var routes = Routes();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "incomplete.json")));
        var count = fixture.RootElement.GetProperty("events").GetInt32();
        var timestamp = fixture.RootElement.GetProperty("timestamp").GetString();
        routes["logs/eventlog"] = new(200, JsonSerializer.Serialize(new
        {
            status = "ok", logs = Enumerable.Range(1, count).Select(i => new { event_id = i, device_id = 1, datetime = timestamp, message = "Synthetic event" }),
        }));
        using var server = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var provider = Connected(server.Root);
        using var host = Host(provider);
        var result = (await host.ReadNetworkAsync(new(Hours: 24, Limit: 1))).Result!;
        Assert.False(result.Data.Window.WindowComplete);
        Assert.NotNull(result.Data.Window.CoveredSince);
        Assert.Contains("start of the window isn't covered", result.Summary, StringComparison.Ordinal);
        Assert.Equal(3, server.Requests.Count(p => p == "logs/eventlog"));
        Assert.Equal(count, result.Data.Page.Total);
        Assert.Single(result.Data.Entries);
        host.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadDeviceAsync(new(1)));
    }
}
