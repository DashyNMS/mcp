using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Events;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Tools;
using DashyNMS.Agent.Unimus;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DashyNMS.Agent.Tests;

public sealed class SharedConfigLookupsTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConfigLookups");
    private static FixtureServer Server(bool requiresDomain = false)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "routes.json")));
        var routes = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => new Answer(200, p.Value.GetRawText()));
        if (requiresDomain) routes["/api/v2/devices"] = new(200, """{"data":[{"id":40,"address":"core-sw1.example.net","description":"Backed-up switch"}],"paginator":{"totalCount":1,"totalPages":1}}""");
        return new(_ => null, requestAnswer: request => routes.GetValueOrDefault(request.Url!.AbsolutePath),
            authenticated: request => request.Headers["Authorization"] == "Bearer synthetic-fixture-token");
    }
    private static UnimusApi Client(Uri root)
    {
        var api = new UnimusApi(NullLogger<UnimusApi>.Instance);
        api.Configure(new UnimusConnection(root, "synthetic-fixture-token"));
        return api;
    }
    private static ConfigTools Tools(FakeLibreNms nms, IUnimusApi api) => new(api, new UnimusDirectory(api, nms.Options, nms.Time),
        nms.Directory, nms.Resolver, nms.Scope, new Redactor(), nms.Runner, nms.Options, nms.Time);

    [Theory]
    [InlineData("backup", ConfigText.Full)]
    [InlineData("backup", ConfigText.Summary)]
    [InlineData("diff", ConfigText.Full)]
    [InlineData("diff", ConfigText.Summary)]
    [InlineData("changes", ConfigText.Full)]
    public async Task Config_reads_match_MCP_over_HTTP_and_keep_text_policy(string mode, ConfigText policy)
    {
        using var mcpServer = Server();
        using var hostServer = Server();
        using var mcpApi = Client(mcpServer.Root);
        using var hostApi = Client(hostServer.Root);
        var mcp = new FakeLibreNms(new AgentOptions { ConfigText = policy });
        var host = new FakeLibreNms(new AgentOptions { ConfigText = policy });
        using var operation = HostConfigLookups.Create(host.Client, hostApi, host.Time, host.Options);
        JsonElement expected;
        JsonElement actual;
        string summary;
        switch (mode)
        {
            case "backup":
                expected = FakeLibreNms.Parse(await Tools(mcp, mcpApi).ConfigBackup("1"));
                var backup = (await operation.ReadBackupAsync(new(1))).Result!;
                Assert.Equal(901, backup.Data.Backup.Id);
                Assert.True(backup.Data.Backup.Newest);
                Assert.Equal(4, backup.Data.Lines.Total);
                if (policy == ConfigText.Full) Assert.Contains(Redactor.Marker, backup.Data.Config!);
                else Assert.Null(backup.Data.Config);
                actual = JsonSerializer.SerializeToElement(backup.Data, ToolResult.Json);
                summary = backup.Summary;
                break;
            case "diff":
                expected = FakeLibreNms.Parse(await Tools(mcp, mcpApi).ConfigDiffTool("1"));
                var diff = (await operation.ReadDiffAsync(new(1))).Result!;
                Assert.Equal(900, diff.Data.From.Id);
                Assert.Equal(901, diff.Data.To.Id);
                Assert.Equal(2, diff.Data.ChangedLines);
                if (policy == ConfigText.Full) Assert.Contains(Redactor.Marker, diff.Data.ConfigDiff!);
                else Assert.Null(diff.Data.ConfigDiff);
                actual = JsonSerializer.SerializeToElement(diff.Data, ToolResult.Json);
                summary = diff.Summary;
                break;
            default:
                expected = FakeLibreNms.Parse(await Tools(mcp, mcpApi).RecentConfigChanges());
                var changes = (await operation.ReadChangesAsync(new())).Result!;
                Assert.Equal(1, changes.DevicesChecked);
                Assert.Equal(901, Assert.Single(changes.Data.Changes).NewestBackupId);
                Assert.Equal(0, changes.UnreadableDevices);
                actual = JsonSerializer.SerializeToElement(changes.Data, ToolResult.Json);
                summary = changes.Summary;
                break;
        }
        Assert.Equal(expected.GetProperty("summary").GetString(), summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), actual));
        Assert.Equal(mcpServer.HttpRequests.Order(), hostServer.HttpRequests.Order());
        Assert.Equal(mcp.Transport.Requests.Order(), host.Transport.Requests.Order());
        var rendered = actual.GetRawText();
        Assert.DoesNotContain("SyntheticCommunity", rendered);
        Assert.DoesNotContain("SyntheticOld", rendered);
        Assert.DoesNotContain("SyntheticNew", rendered);
        Assert.DoesNotContain("synthetic-key-material", rendered);
        operation.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ReadChangesAsync(new()));
    }

    [Theory]
    [InlineData("backup", null)]
    [InlineData("backup", "example.net")]
    [InlineData("diff", null)]
    [InlineData("diff", "example.net")]
    [InlineData("changes", null)]
    [InlineData("changes", "example.net")]
    [InlineData("what_changed", null)]
    [InlineData("what_changed", "example.net")]
    public async Task Host_discovery_domain_matches_short_names_for_config_reads_and_scoped_changes(string mode, string? domain)
    {
        using var server = Server(requiresDomain: true);
        using var api = Client(server.Root);
        using var graylog = new GraylogApi(NullLogger<GraylogApi>.Instance);
        var nms = new FakeLibreNms(new AgentOptions { ConfigText = ConfigText.Summary, UnimusMyDomain = domain });
        nms.Transport.On("devices", """{"devices":[{"device_id":1,"hostname":"core-sw1","ip":"192.0.2.1","location":"Lab","status":1,"disabled":0}]}""");
        using var host = HostConfigLookups.Create(nms.Client, api, nms.Time, nms.Options);
        var matched = domain is not null;
        switch (mode)
        {
            case "backup":
                var backup = await host.ReadBackupAsync(new(1));
                Assert.Equal(matched, backup.Result is not null);
                if (matched) Assert.Equal(901, backup.Result!.Data.Backup.Id);
                else Assert.Equal(HostFailure.InvalidRequest, backup.Error);
                break;
            case "diff":
                var diff = await host.ReadDiffAsync(new(1));
                Assert.Equal(matched, diff.Result is not null);
                if (matched) Assert.Equal(901, diff.Result!.Data.To.Id);
                else Assert.Equal(HostFailure.InvalidRequest, diff.Error);
                break;
            case "changes":
                // A scope must correlate with LibreNMS before it checks Unimus backups.
                var changes = (await host.ReadChangesAsync(new(Location: "Lab"))).Result!;
                Assert.Equal(matched ? 1 : 0, changes.DevicesChecked);
                Assert.Equal(matched ? 1 : 0, changes.Data.Changes.Count);
                if (matched) Assert.Equal(1, Assert.Single(changes.Data.Changes).Device!.DeviceId);
                break;
            default:
                using var events = HostEventLookups.Create(nms.Client, api, graylog, nms.Time, nms.Options);
                var context = (await events.ReadChangesAsync(new(1))).Result!;
                Assert.Equal(matched, context.Data.Unimus.DeviceFound);
                break;
        }
        Assert.Equal(matched, server.HttpRequests.Any(r => r.StartsWith("/api/v2/devices/40/backups", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("secret", 1)]
    [InlineData("privatekey", 2)]
    [InlineData("rocommunity", 200)]
    public void Xml_secret_bodies_stay_hidden_without_tags_and_across_the_cap(string tag, int cap)
    {
        var diff = new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = $"<{tag}>" }] },
            new UnimusDiffLineGroup { Type = "CHANGED",
                OriginalLines = [new() { Text = "SyntheticOldBody" }],
                RevisedLines = [new() { Text = "SyntheticNewBody" }] },
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = $"</{tag}>" }] },
            new UnimusDiffLineGroup { Type = "CHANGED", RevisedLines = [new() { Text = "vlan 120" }] },
        ] };
        var rendered = ConfigDiff.Render(diff, cap);
        Assert.DoesNotContain("Synthetic", rendered.Text);
        Assert.Contains(Redactor.Marker, rendered.Text);
        Assert.Equal(3, rendered.Lines);
        Assert.Equal(Math.Max(0, 3 - cap), rendered.Omitted);
        if (cap >= 3) Assert.Contains("+ vlan 120", rendered.Text);
    }

    [Theory]
    [InlineData("<!-- example: </secret> -->")]
    [InlineData("<!-- example: </secret>\nstill a comment -->")]
    [InlineData("<![CDATA[example: </secret>]]>")]
    [InlineData("<![CDATA[example: </secret>\nstill data ]]>")]
    [InlineData("<?note > </secret> ?>")]
    [InlineData("<?note >\n</secret>\n?>")]
    public void XML_region_closing_tags_cannot_expose_changed_secret_bodies(string context)
    {
        var common = new[] { "<secret>" }.Concat(context.Split('\n'));
        var diff = new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = common.Select(t => new UnimusDiffLine { Text = t }).ToList() },
            new UnimusDiffLineGroup { Type = "CHANGED",
                OriginalLines = [new() { Text = "SyntheticOldBody" }], RevisedLines = [new() { Text = "SyntheticNewBody" }] },
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = "</secret>" }] },
            new UnimusDiffLineGroup { Type = "CHANGED", RevisedLines = [new() { Text = "vlan 120" }] },
        ] };
        var rendered = ConfigDiff.Render(diff);
        Assert.DoesNotContain("Synthetic", rendered.Text);
        Assert.Equal($"- {Redactor.Marker}\n+ {Redactor.Marker}\n+ vlan 120", rendered.Text);
    }

    [Theory]
    [InlineData("<secret\n>")]
    [InlineData("<secret note=\"example\n>\ntext\"\n>")]
    public void Split_opening_tags_and_quoted_attributes_keep_both_secret_bodies_hidden(string opening)
    {
        var diff = new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = opening.Split('\n').Select(t => new UnimusDiffLine { Text = t }).ToList() },
            new UnimusDiffLineGroup { Type = "CHANGED",
                OriginalLines = [new() { Text = "SyntheticOldBody" }], RevisedLines = [new() { Text = "SyntheticNewBody" }] },
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = "</secret>" }] },
            new UnimusDiffLineGroup { Type = "CHANGED", RevisedLines = [new() { Text = "vlan 120" }] },
        ] };
        var rendered = ConfigDiff.Render(diff);
        Assert.Equal($"- {Redactor.Marker}\n+ {Redactor.Marker}\n+ vlan 120", rendered.Text);
    }

    [Theory]
    [InlineData("read mode < /etc/device-mode")]
    [InlineData("if (count < 10)")]
    public void Plain_text_less_than_operators_do_not_hide_subsequent_changes(string common)
    {
        var diff = new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = common }] },
            new UnimusDiffLineGroup { Type = "CHANGED", OriginalLines = [new() { Text = "hostname old-switch" }],
                RevisedLines = [new() { Text = "hostname new-switch" }] },
        ] };
        Assert.Equal("- hostname old-switch\n+ hostname new-switch", ConfigDiff.Render(diff).Text);
    }

    [Theory]
    [InlineData("<b")]
    [InlineData("<b note=\"unfinished")]
    public void Malformed_common_tags_cannot_consume_changed_secret_openers(string common)
    {
        var diff = new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = common }] },
            new UnimusDiffLineGroup { Type = "CHANGED",
                OriginalLines = [new() { Text = "<secret>" }, new() { Text = "SyntheticOldBody" }, new() { Text = "</secret>" }],
                RevisedLines = [new() { Text = "<secret>" }, new() { Text = "SyntheticNewBody" }, new() { Text = "</secret>" }] },
        ] };
        var rendered = ConfigDiff.Render(diff);
        Assert.Equal(6, rendered.Lines);
        Assert.All(rendered.Text.Split('\n'), line => Assert.EndsWith(Redactor.Marker, line));
        Assert.DoesNotContain("Synthetic", rendered.Text);
    }

    [Fact]
    public void Oversized_unfinished_XML_syntax_keeps_subsequent_text_hidden()
    {
        var diff = new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "COMMON", OriginalLines = [new() { Text = "<secret note=\"" + new string('x', 4096) }] },
            new UnimusDiffLineGroup { Type = "CHANGED", RevisedLines = [new() { Text = "SyntheticSecretBody" }] },
        ] };
        Assert.Equal("+ " + Redactor.Marker, ConfigDiff.Render(diff).Text);
    }

    [Fact]
    public void Xml_secret_context_is_independent_on_each_side()
    {
        var diff = new UnimusBackupDiff { LineGroups =
        [
            new UnimusDiffLineGroup { Type = "CHANGED",
                OriginalLines = [new() { Text = "<secret>" }, new() { Text = "SyntheticOldBody" }, new() { Text = "</secret>" }],
                RevisedLines = [new() { Text = "vlan 120" }] },
            new UnimusDiffLineGroup { Type = "CHANGED", RevisedLines = [new() { Text = "<SECRET>SyntheticNewBody</SECRET>" }] },
        ] };
        var rendered = ConfigDiff.Render(diff);
        Assert.DoesNotContain("Synthetic", rendered.Text);
        Assert.Contains("+ vlan 120", rendered.Text);
    }

    [Fact]
    public async Task Off_policy_stops_before_reading_either_service()
    {
        var api = Substitute.For<IUnimusApi>();
        api.IsConfigured.Returns(true);
        var nms = new FakeLibreNms(new AgentOptions { ConfigText = ConfigText.Off });
        using var host = HostConfigLookups.Create(nms.Client, api, nms.Time, nms.Options);
        Assert.Equal(HostFailure.InvalidRequest, (await host.ReadBackupAsync(new(1))).Error);
        Assert.Equal(HostFailure.InvalidRequest, (await host.ReadDiffAsync(new(1))).Error);
        Assert.Equal(HostFailure.InvalidRequest, (await host.ReadChangesAsync(new())).Error);
        Assert.Empty(nms.Transport.Requests);
        await api.DidNotReceive().ListAllDevicesAsync(Arg.Any<CancellationToken>());
    }
}
