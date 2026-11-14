using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Graylog;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DashyNMS.Agent.Tests;

public sealed class SharedLogSearchTests
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LogSearch", "messages.json");
    private static FixtureServer Server()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        var fixture = json.RootElement.Clone();
        var now = fixture.GetProperty("now").GetDateTimeOffset();
        return new(_ => null, requestAnswer: request =>
        {
            if (request.Url!.AbsolutePath == "/api/streams")
                return new(200, JsonSerializer.Serialize(new { streams = fixture.GetProperty("streams") }));
            if (request.Url.AbsolutePath != "/api/search/universal/relative") return null;
            var range = int.Parse(request.QueryString["range"]!, CultureInfo.InvariantCulture);
            var limit = int.Parse(request.QueryString["limit"]!, CultureInfo.InvariantCulture);
            var offset = int.Parse(request.QueryString["offset"]!, CultureInfo.InvariantCulture);
            var matched = fixture.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("timestamp").GetDateTimeOffset() >= now.AddSeconds(-range)).ToList();
            var sorted = request.QueryString["sort"] == "timestamp:asc"
                ? matched.OrderBy(m => m.GetProperty("timestamp").GetDateTimeOffset())
                : matched.OrderByDescending(m => m.GetProperty("timestamp").GetDateTimeOffset());
            return new(200, JsonSerializer.Serialize(new { total_results = matched.Count,
                messages = sorted.Skip(offset).Take(limit).Select(m => new { message = m }) }));
        }, authenticated: request => request.Headers["Authorization"] == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:synthetic-password")));
    }
    private static GraylogApi Client(Uri root)
    {
        var api = new GraylogApi(NullLogger<GraylogApi>.Instance);
        api.Configure(new GraylogConnection(root, GraylogSettings.Version21, null, "fixture", "synthetic-password"));
        return api;
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task Search_matches_independent_counts_and_MCP_over_HTTP(bool past, int expectedCount)
    {
        using var mcpServer = Server();
        using var hostServer = Server();
        using var mcpApi = Client(mcpServer.Root);
        using var hostApi = Client(hostServer.Root);
        var mcp = new FakeLibreNms();
        var host = new FakeLibreNms();
        using var operation = HostLogSearch.Create(host.Client, hostApi, host.Time, host.Options);
        var since = past ? "2026-10-16T07:00:00Z" : null;
        var until = past ? "2026-10-16T09:00:00Z" : null;
        var request = new SearchLogsRequest(Since: since, Until: until, Hours: past ? null : 24, Stream: "Network devices", Limit: 1);
        var result = (await operation.ReadAsync(request)).Result!;
        var expected = FakeLibreNms.Parse(await new LogTools(mcp.Client, mcpApi, mcp.Resolver, mcp.Runner, mcp.Options, mcp.Time)
            .SearchLogs(since: since, until: until, hours: past ? null : 24, stream: "Network devices", limit: 1));
        Assert.Equal(expectedCount, result.Data.Count);
        Assert.Equal(expectedCount, result.Data.OverTime!.Sum(b => b.Count));
        Assert.Equal(expectedCount - 1, result.LinesOmitted);
        Assert.Single(result.Data.Lines!);
        Assert.Equal(expected.GetProperty("summary").GetString(), result.Summary);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), JsonSerializer.SerializeToElement(result.Data, ToolResult.Json)));
        Assert.Equal(mcpServer.HttpRequests.Order(), hostServer.HttpRequests.Order());
        operation.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ReadAsync(new()));
    }

    [Fact]
    public async Task Private_key_blocks_are_redacted_before_the_line_limit_can_cut_off_the_footer()
    {
        var graylog = Substitute.For<IGraylogApi>();
        graylog.IsConfigured.Returns(true);
        var material = new string('Q', 500);
        var message = new GraylogMessage(new Dictionary<string, JsonElement>
        {
            ["timestamp"] = JsonSerializer.SerializeToElement(FakeLibreNms.Now),
            ["message"] = JsonSerializer.SerializeToElement("-----BEGIN PRIVATE KEY-----\n" + material + "\n-----END PRIVATE KEY-----"),
        });
        graylog.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new GraylogSearchResult { TotalResults = 1, Messages = [new GraylogMessageEnvelope { Message = message }] });
        var nms = new FakeLibreNms();
        using var host = HostLogSearch.Create(nms.Client, graylog, nms.Time, nms.Options);
        var result = (await host.ReadAsync(new())).Result!;
        var text = Assert.Single(result.Data.Lines!).Text!;
        Assert.DoesNotContain(new string('Q', 20), text);
        Assert.Contains(DashyNMS.Agent.Redaction.Redactor.Marker, text);
        Assert.True(text.Length <= LogSearch.MaxLineLength + 1);
    }

    [Theory]
    [InlineData(403, HostFailure.Refused)]
    [InlineData(404, HostFailure.NotFound)]
    [InlineData(500, HostFailure.Unavailable)]
    public async Task Service_failure_returns_a_fixed_code_without_server_text(int status, HostFailure code)
    {
        using var server = new FixtureServer(_ => new Answer(status, "password=synthetic-secret"), authenticated: _ => true);
        using var api = Client(server.Root);
        var nms = new FakeLibreNms();
        using var host = HostLogSearch.Create(nms.Client, api, nms.Time, nms.Options);
        var outcome = await host.ReadAsync(new());
        Assert.Null(outcome.Result);
        Assert.Equal(code, outcome.Error);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(outcome));
    }
}
