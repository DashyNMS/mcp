using System.Text.Json;
using DashyNMS.Agent.Alerts;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

public sealed class SharedAlertRulesTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AlertRules");
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

    [Fact]
    public async Task Failed_history_never_claims_a_first_firing_or_a_known_zero_count()
    {
        var nms = new FakeLibreNms();
        nms.Transport.Fails("logs/alertlog/1", System.Net.HttpStatusCode.Forbidden);
        using var host = HostAlertRules.Create(nms.Client, nms.Time, nms.Options);
        var result = (await host.ReadDetailsAsync(new(10))).Result!;
        Assert.False(result.Data.History.Available);
        Assert.Null(result.Data.History.FiredBeforeIn30Days);
        Assert.Null(result.Data.History.AtLeast);
        Assert.Contains("previous firings are unknown", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("first time", result.Summary, StringComparison.Ordinal);
        Assert.Contains(result.Data.Unavailable!, u => u.Source == "history");
        var mcp = FakeLibreNms.Parse(await new RuleTools(nms.Client, nms.Fleet, nms.Directory, nms.Scope, nms.Runner, nms.Options, nms.Time).AlertDetails(10));
        Assert.False(mcp.GetProperty("data").GetProperty("history").TryGetProperty("fired_before_in_30_days", out _));
        Assert.Contains("unknown", mcp.GetProperty("summary").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_target_names_preserve_distinct_typed_ids_and_MCP_names()
    {
        var nms = new FakeLibreNms();
        nms.Transport.On("rules", """{"status":"ok","rules":[{"id":3,"name":"Example rule","devices":[1,2],"groups":[1,2],"locations":[1,2]}]}""")
            .On("devices", """{"status":"ok","devices":[{"device_id":1,"hostname":"same.example.net"},{"device_id":2,"hostname":"same.example.net"}]}""")
            .On("devicegroups", """{"status":"ok","groups":[{"id":1,"name":"Same group"},{"id":2,"name":"Same group"}]}""")
            .On("resources/locations", """{"status":"ok","locations":[{"id":1,"location":"Same site"},{"id":2,"location":"Same site"}]}""");
        using var host = HostAlertRules.Create(nms.Client, nms.Time, nms.Options);
        var targets = Assert.Single((await host.ReadRulesAsync(new())).Result!.Data.Rules).AppliesTo.Targets!;
        Assert.Equal(new[] { 1, 2 }, targets.Devices!.Select(t => t.Id));
        Assert.Equal(new[] { 1, 2 }, targets.Groups!.Select(t => t.Id));
        Assert.Equal(new[] { 1, 2 }, targets.Locations!.Select(t => t.Id));
        Assert.Equal(new[] { "same.example.net", "same.example.net" }, targets.Devices!.Select(t => t.Name));
        var mcp = FakeLibreNms.Parse(await new RuleTools(nms.Client, nms.Fleet, nms.Directory, nms.Scope, nms.Runner, nms.Options, nms.Time).AlertRules());
        var names = mcp.GetProperty("data").GetProperty("rules")[0].GetProperty("applies_to").GetProperty("devices");
        Assert.Equal(new[] { "same.example.net", "same.example.net" }, names.EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task Details_match_independent_facts_and_MCP_over_HTTP()
    {
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var operation = HostAlertRules.Create(host.GetRequiredService<ILibreNmsClient>(), host.GetRequiredService<TimeProvider>(), new());
        var result = (await operation.ReadDetailsAsync(new(10, AlertSource.Alert))).Result!;
        var expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<RuleTools>(mcp).AlertDetails(10));
        Assert.Equal(10, result.Identity.Id);
        Assert.Equal(AlertSource.Alert, result.Identity.Source);
        Assert.Equal(1, result.Data.Device!.DeviceId);
        Assert.Equal(3, result.Data.Rule.RuleId);
        Assert.Equal(1, result.Data.Why.Matched);
        Assert.Equal(1, result.Data.History.FiredBeforeIn30Days);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("data"), JsonSerializer.SerializeToElement(result.Data, ToolResult.Json)));
        Assert.Equal(expected.GetProperty("summary").GetString(), result.Summary);
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
        var wrongSource = await operation.ReadDetailsAsync(new(10, AlertSource.Fault));
        Assert.Null(wrongSource.Result);
        Assert.Equal(HostFailure.InvalidRequest, wrongSource.Error);
    }

    [Fact]
    public async Task Rules_keep_MCP_sorts_counts_and_typed_applicability_over_HTTP()
    {
        var routes = Routes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var operation = HostAlertRules.Create(host.GetRequiredService<ILibreNmsClient>(), host.GetRequiredService<TimeProvider>(), new());
        var result = (await operation.ReadRulesAsync(new())).Result!;
        var expected = FakeLibreNms.Parse(await ActivatorUtilities.CreateInstance<RuleTools>(mcp).AlertRules());
        Assert.Equal(new[] { 4, 5, 3 }, result.Data.Rules.Select(r => r.RuleId));
        var rows = expected.GetProperty("data").GetProperty("rules");
        for (var i = 0; i < result.Data.Rules.Count; i++)
        {
            var row = result.Data.Rules[i];
            var actual = JsonSerializer.SerializeToElement(row, ToolResult.Json);
            foreach (var property in rows[i].EnumerateObject().Where(p => p.Name != "applies_to"))
                Assert.True(JsonElement.DeepEquals(property.Value, actual.GetProperty(property.Name)), property.Name);
            Assert.False(row.AppliesTo.AllDevices);
            Assert.True(JsonElement.DeepEquals(rows[i].GetProperty("applies_to"), JsonSerializer.SerializeToElement(new
            {
                row.AppliesTo.Targets!.Except,
                devices = row.AppliesTo.Targets.Devices?.Select(t => t.Name),
                groups = row.AppliesTo.Targets.Groups?.Select(t => t.Name),
                locations = row.AppliesTo.Targets.Locations?.Select(t => t.Name),
            }, ToolResult.Json)));
        }
        Assert.Equal(expected.GetProperty("summary").GetString(), result.Summary);
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
        operation.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ReadRulesAsync(new()));
    }
}
