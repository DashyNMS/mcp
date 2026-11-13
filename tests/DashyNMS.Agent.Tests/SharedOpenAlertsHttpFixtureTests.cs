using System.Globalization;
using System.Text.Json;
using DashyNMS.Agent.Alerts;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The shared open alerts fixture served over loopback HTTP to the MCP server's own wiring (its
/// active_alerts tool over Core's real LibreNmsClient) and to <see cref="HostOpenAlerts"/> over the
/// same kind of client. DashyNMS/mobile serves the same files to its Intelligence active_alerts.
/// </summary>
/// <remarks>
/// <c>faults.json</c> is a LibreNMS 26.10 server: eight faults on one rule with two acknowledged, a
/// fault numbered 12, a recovered fault served beside its open siblings (the fixture server ignores
/// the state filter, so the operation's own filter is what drops it), a fully acknowledged group and
/// a group whose only fault has recovered. <c>alerts.json</c> is an older server whose alert 12 is a
/// different thing from fault 12. <c>transport.json</c> fails the alert routes while the device list
/// answers. Request counts include Core's source probe and retries.
/// </remarks>
public sealed class SharedOpenAlertsHttpFixtureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenAlerts");

    private static JsonElement Load(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, file)));
        return json.RootElement.Clone();
    }

    private static readonly JsonElement Cases = Load("cases.json");
    private static readonly JsonElement Transport = Load("transport.json");

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static string[] Strings(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(r => r.GetString()!).ToArray();

    private static int[] Ints(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(i => i.GetInt32()).ToArray();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> TransportFailures()
    {
        var data = new TheoryData<string>();
        foreach (var f in Transport.GetProperty("failures").EnumerateArray())
        {
            data.Add(f.GetProperty("name").GetString()!);
        }

        return data;
    }

    /// <summary>One server's routes from <c>cases.json</c>, as LibreNMS would answer them; any other path is a 404.</summary>
    private static Dictionary<string, Answer> Routes(string server)
    {
        var routes = new Dictionary<string, Answer>(StringComparer.Ordinal);
        foreach (var route in Cases.GetProperty("routes").GetProperty(server).EnumerateObject())
        {
            routes[route.Name] = new(200, File.ReadAllText(Path.Combine(FixtureDirectory, route.Value.GetString()!)));
        }

        return routes;
    }

    private static ServiceProvider Connected(Uri root)
    {
        var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        provider.GetRequiredService<ILibreNmsClient>().Connect(
            new LibreNmsConnection(root, FixtureServer.Token, timeoutSeconds: Transport.GetProperty("timeoutSeconds").GetInt32()));
        return provider;
    }

    /// <summary>active_alerts as the MCP SDK activates it from the server's services.</summary>
    private static AlertTools Tools(IServiceProvider provider) => ActivatorUtilities.CreateInstance<AlertTools>(provider);

    private static HostOpenAlerts Host(IServiceProvider provider)
        => HostOpenAlerts.Create(provider.GetRequiredService<ILibreNmsClient>(), TimeProvider.System, serverTimestampsAreUtc: true);

    private static string StateText(OpenAlertState state) => state switch
    {
        OpenAlertState.PartlyAcknowledged => "partly_acknowledged",
        OpenAlertState.Acknowledged => "acknowledged",
        _ => "active",
    };

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Active_alerts_and_the_host_operation_group_the_fixture_identically_over_HTTP(string name)
    {
        var c = Cases.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var server = c.GetProperty("server").GetString()!;
        var routes = Routes(server);
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var alerts = Host(host);
        var (device, severity, location) = (Number(c, "device"), Text(c, "severity"), Text(c, "location"));
        var limit = Number(c, "limit") ?? OpenAlerts.DefaultLimit;

        var root = FakeLibreNms.Parse(await Tools(mcp).ActiveAlerts(severity,
            device?.ToString(CultureInfo.InvariantCulture), location: location, limit: limit, cancellationToken: CancellationToken.None));
        var outcome = await alerts.ListAsync(new(device, severity, Location: location, Limit: limit));

        var requests = c.TryGetProperty("requests", out var own) ? own : Cases.GetProperty("requests").GetProperty(server);
        Assert.Equal(requests.EnumerateArray().Select(r => r.GetString()), mcpServer.Requests);
        Assert.Equal(requests.EnumerateArray().Select(r => r.GetString()), hostServer.Requests);

        if (Text(c, "code") is { } code)
        {
            Assert.Equal("error", root.GetProperty("status").GetString());
            Assert.False(root.TryGetProperty("data", out _));
            Assert.Null(outcome.Result);
            Assert.Equal(Enum.Parse<HostFailure>(code), outcome.Error!.Code);
            return;
        }

        Assert.Equal("ok", root.GetProperty("status").GetString());
        var data = root.GetProperty("data");
        var shown = data.GetProperty("devices").EnumerateArray().SelectMany(d => d.GetProperty("alerts").EnumerateArray()).ToList();
        var result = outcome.Result!;
        Assert.Null(outcome.Error);

        Assert.Equal(Strings(c, "groups"), shown.Select(a => a.GetProperty("group_id").GetString()));
        Assert.Equal(Strings(c, "groups"), result.Groups.Select(g => g.Key.ToString()));
        Assert.Equal(Strings(c, "representatives"), shown.Select(a => a.GetProperty("source").GetString() + ":" + a.GetProperty("alert_id").GetInt32().ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(Strings(c, "representatives"), result.Groups.Select(g => g.Representative.ToString()));
        Assert.Equal(Ints(c, "faultCounts"), shown.Select(a => a.GetProperty("fault_count").GetInt32()));
        Assert.Equal(Ints(c, "faultCounts"), result.Groups.Select(g => g.FaultCount));
        // Each group's faults in order: all of them from the host; MCP's brief list names the
        // first few of a group of several and counts the rest.
        var faults = c.GetProperty("faults").EnumerateArray().Select(g => g.EnumerateArray().Select(f => f.GetInt32()).ToArray()).ToArray();
        Assert.Equal(faults, result.Groups.Select(g => g.Faults.Select(f => f.Key.Id).ToArray()));
        Assert.All(result.Groups, g => Assert.Equal(0, g.FaultsOmitted));
        for (var i = 0; i < shown.Count; i++)
        {
            if (faults[i].Length == 1)
            {
                Assert.False(shown[i].TryGetProperty("faults", out var none) && none.ValueKind == JsonValueKind.Array);
                continue;
            }

            var listed = shown[i].GetProperty("faults").EnumerateArray().Select(f => f.GetProperty("fault_id").GetInt32()).ToArray();
            Assert.Equal(faults[i].Take(AlertTools.BriefFaults), listed);
            Assert.Equal(faults[i].Length > AlertTools.BriefFaults ? faults[i].Length - AlertTools.BriefFaults : null, Number(shown[i], "faults_omitted"));
        }
        Assert.Equal(Strings(c, "states"), shown.Select(a => a.GetProperty("state").GetString()));
        Assert.Equal(Strings(c, "states"), result.Groups.Select(g => StateText(g.State)));
        // MCP names an acknowledged count only for a partly acknowledged group of several.
        Assert.Equal(Ints(c, "acknowledgedCounts"), shown.Select(a =>
            a.TryGetProperty("acknowledged_count", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32()
            : a.GetProperty("state").GetString() == "acknowledged" ? a.GetProperty("fault_count").GetInt32() : 0));
        Assert.Equal(Ints(c, "acknowledgedCounts"), result.Groups.Select(g => g.AcknowledgedCount));

        var totals = c.GetProperty("totals");
        var mcpTotals = data.GetProperty("totals");
        foreach (var total in totals.EnumerateObject())
        {
            Assert.Equal(total.Value.GetInt32(), mcpTotals.GetProperty(total.Name).GetInt32());
        }

        Assert.Equal(totals.GetProperty("alert_groups").GetInt32(), result.Totals.AlertGroups);
        Assert.Equal(totals.GetProperty("faults").GetInt32(), result.Totals.Faults);
        Assert.Equal(totals.GetProperty("devices").GetInt32(), result.Totals.Devices);
        Assert.Equal(totals.GetProperty("critical").GetInt32(), result.Totals.Critical);
        Assert.Equal(totals.GetProperty("warning").GetInt32(), result.Totals.Warning);
        Assert.Equal(totals.GetProperty("acknowledged").GetInt32(), result.Totals.Acknowledged);
        Assert.Equal(totals.GetProperty("partly_acknowledged").GetInt32(), result.Totals.PartlyAcknowledged);

        var next = Number(c, "nextOffset");
        Assert.Equal(next, Number(data.GetProperty("page"), "next_offset"));
        Assert.Equal(next, result.Page.NextOffset);
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task Active_alerts_and_the_host_operation_fail_alike_when_the_alert_routes_fail(string name)
    {
        var failure = Transport.GetProperty("failures").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
        var devices = new Answer(200, File.ReadAllText(Path.Combine(FixtureDirectory, "devices.json")));
        var failing = Text(failure, "behaviour") == "hang" ? Answer.Hang
            : new Answer(failure.GetProperty("status").GetInt32(),
                Transport.GetProperty("bodies").GetProperty(failure.GetProperty("body").GetString()!).GetString()!);
        using var mcpServer = new FixtureServer(path => path == "devices" ? devices : failing);
        using var hostServer = new FixtureServer(path => path == "devices" ? devices : failing);
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var alerts = Host(host);

        // Side by side: a request that never gets an answer costs the full timeout on each.
        var mcpCall = Tools(mcp).ActiveAlerts(cancellationToken: CancellationToken.None);
        var hostCall = alerts.ListAsync(new());
        await Task.WhenAll(mcpCall, hostCall);
        var text = await mcpCall;
        var outcome = await hostCall;

        // MCP: an error in its own words, with no alert data and nothing secret-shaped left.
        var root = FakeLibreNms.Parse(text);
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        // Host: the shared category, no result and no server text.
        Assert.Null(outcome.Result);
        Assert.Equal(Enum.Parse<HostFailure>(failure.GetProperty("code").GetString()!), outcome.Error!.Code);
        Assert.DoesNotContain("synthetic-secret", outcome.Error.Message, StringComparison.Ordinal);

        Assert.Equal(Strings(failure, "requests"), mcpServer.Requests);
        Assert.Equal(Strings(failure, "requests"), hostServer.Requests);
    }
}
