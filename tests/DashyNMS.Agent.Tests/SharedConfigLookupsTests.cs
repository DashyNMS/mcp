using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Tools;
using DashyNMS.Agent.Unimus;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace DashyNMS.Agent.Tests;

public sealed class SharedConfigLookupsTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConfigLookups");
    private static FixtureServer Server()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "routes.json")));
        var routes = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => new Answer(200, p.Value.GetRawText()));
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
