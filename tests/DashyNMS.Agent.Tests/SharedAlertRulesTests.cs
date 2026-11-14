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
            Assert.True(JsonElement.DeepEquals(rows[i].GetProperty("applies_to"), JsonSerializer.SerializeToElement(row.AppliesTo.Targets, ToolResult.Json)));
        }
        Assert.Equal(expected.GetProperty("summary").GetString(), result.Summary);
        Assert.Equal(mcpServer.Requests.Order(), hostServer.Requests.Order());
        operation.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ReadRulesAsync(new()));
    }
}
