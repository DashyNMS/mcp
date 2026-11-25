using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

/// <summary>Turning tools off by name (#37): the lists, and that a tool that's off can be neither seen nor called.</summary>
public sealed class ToolPolicyTests
{
    private static ToolPolicy Parse(string? allow, string? deny, out List<string> problems)
        => ToolPolicy.Parse(allow, deny, problems = []);

    [Fact]
    public void Every_tool_is_on_by_default()
    {
        var policy = Parse(null, null, out var problems);

        Assert.Empty(problems);
        Assert.Equal(ToolCatalogue.Names, policy.On);
        Assert.Empty(policy.Off);
    }

    [Fact]
    public void Denied_tools_are_off_and_the_rest_stay_on()
    {
        var policy = Parse(null, "what_changed, graph", out var problems);

        Assert.Empty(problems);
        Assert.False(policy.IsOn("what_changed"));
        Assert.False(policy.IsOn("graph"));
        Assert.Equal(ToolCatalogue.Names.Count - 2, policy.On.Count);
        Assert.Equal(2, policy.Off.Count);
    }

    [Fact]
    public void Only_allowed_tools_are_on()
    {
        var policy = Parse("find_device,device_health", null, out var problems);

        Assert.Empty(problems);
        Assert.Equal(2, policy.On.Count);
        Assert.True(policy.IsOn("find_device"));
        Assert.True(policy.IsOn("device_health"));
    }

    [Fact]
    public void Deny_wins_over_allow()
    {
        var policy = Parse("find_device,graph", "graph", out var problems);

        Assert.Empty(problems);
        Assert.Equal(["find_device"], policy.On);
    }

    [Fact]
    public void Case_and_spaces_are_forgiven()
    {
        var policy = Parse(null, " Graph , ", out var problems);

        Assert.Empty(problems);
        Assert.Equal(["graph"], policy.Off);
    }

    [Theory]
    [InlineData("grpah", null, McpOptions.ToolsAllow)]
    [InlineData(null, "grpah", McpOptions.ToolsDeny)]
    public void A_name_that_is_not_a_tool_is_refused(string? allow, string? deny, string key)
    {
        Parse(allow, deny, out var problems);

        var problem = Assert.Single(problems);
        Assert.StartsWith($"{key}: 'grpah' isn't a tool.", problem, StringComparison.Ordinal);
        Assert.Contains("find_device", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_of_nothing_is_refused()
    {
        Parse(",", null, out var problems);

        Assert.Contains("has no tool names in it", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_no_tools_on_is_refused()
    {
        Parse("graph", "graph", out var problems);

        Assert.Contains("leave no tools on", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_that_is_off_is_not_listed_and_cannot_be_called()
    {
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, McpOptions.ToolsDeny, "device_health,graph");
        await server.InitializeAsync(cts.Token);

        var list = await server.RequestAsync(2, "tools/list", "{}", cts.Token);
        var listed = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        Assert.Equal(ToolCatalogue.Names.Except(["device_health", "graph"]).Order(), listed.Order());

        var asked = librenms.Requests.Length;
        var call = await server.RequestAsync(3, "tools/call", """{"name":"device_health","arguments":{"device":"core-sw1"}}""", cts.Token);
        Assert.Contains("Unknown tool", call.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(asked, librenms.Requests.Length);

        var allowed = await server.CallToolAsync(4, "find_device", """{"query":"core"}""", cts.Token);
        Assert.Equal("ok", allowed.GetProperty("status").GetString());

        var stderr = await server.StopAsync(cts.Token);
        Assert.Contains("23 of 25 tools are on", stderr, StringComparison.Ordinal);
        Assert.Contains("Off: ", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_allow_list_offers_only_its_tools()
    {
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, McpOptions.ToolsAllow, "find_device");
        await server.InitializeAsync(cts.Token);

        var list = await server.RequestAsync(2, "tools/list", "{}", cts.Token);
        Assert.Equal("find_device", Assert.Single(list.GetProperty("result").GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        var call = await server.RequestAsync(3, "tools/call", """{"name":"active_alerts","arguments":{}}""", cts.Token);
        Assert.True(call.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("dashynms_help", null, false)]
    [InlineData("dashynms_help,find_device", "find_device", false)]
    [InlineData("dashynms_help,find_device", null, true)]
    [InlineData(null, null, true)]
    [InlineData("not_a_tool", null, true)]
    public void Only_help_needs_no_LibreNMS(string? allow, string? deny, bool needs)
        => Assert.Equal(needs, Parse(allow, deny, out _).NeedsLibreNms);

    [Fact]
    public async Task Help_alone_runs_without_LibreNMS()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = StdioServer.Start(new Dictionary<string, string?> { [McpOptions.ToolsAllow] = "dashynms_help" });
        await server.InitializeAsync(cts.Token);

        var list = await server.RequestAsync(2, "tools/list", "{}", cts.Token);
        Assert.Equal("dashynms_help", Assert.Single(list.GetProperty("result").GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        var answer = await server.CallToolAsync(3, "dashynms_help", "{\"query\":\"choose which alerts notify me\",\"product\":\"desktop\"}", cts.Token);
        Assert.Equal("ok", answer.GetProperty("status").GetString());
        Assert.Equal("desktop", answer.GetProperty("data").GetProperty("matches")[0].GetProperty("product").GetString());
        Assert.Contains("LibreNMS isn't set up", await server.StopAsync(cts.Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_misspelt_tool_stops_startup()
    {
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, McpOptions.ToolsDeny, "config_backups");

        var (exitCode, stdout) = await server.WaitForExitAsync(cts.Token);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("MCP_TOOLS_DENY: 'config_backups' isn't a tool.", server.Stderr, StringComparison.Ordinal);
        Assert.Empty(librenms.Requests);
    }

    private static FixtureServer LibreNms() => new(path => path switch
    {
        "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        "devices" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.net","status":1}]}"""),
        _ => null,
    });

    private static StdioServer Start(FixtureServer librenms, string key, string value) => StdioServer.Start(new Dictionary<string, string?>
    {
        [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
        [McpOptions.LibreNmsToken] = FixtureServer.Token,
        [key] = value,
    });
}
