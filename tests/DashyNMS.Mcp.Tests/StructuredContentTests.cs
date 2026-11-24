using System.Text.Json;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// Output schemas and structured content as a client sees them (#40): every
/// tool lists one, and every answer, good or bad, is the same JSON twice over
/// and fits the schema listed.
/// </summary>
public sealed class StructuredContentTests
{
    private static readonly Dictionary<string, string> Arguments = new(StringComparer.Ordinal)
    {
        ["find_device"] = """{"query":"core"}""",
        ["alert_details"] = """{"alert_id":1}""",
        ["graph"] = """{"device":"core-sw1","type":"device_bits"}""",
        ["port_status"] = """{"device":"core-sw1","port":"1/1/1"}""",
        ["trace_endpoint"] = """{"address":"aa:bb:cc:dd:ee:ff"}""",
        ["config_backup"] = """{"device":"core-sw1"}""",
        ["config_diff"] = """{"device":"core-sw1"}""",
        ["device_details"] = """{"device":"core-sw1"}""",
        ["device_events"] = """{"device":"core-sw1"}""",
        ["device_health"] = """{"device":"core-sw1"}""",
        ["device_neighbours"] = """{"device":"core-sw1"}""",
        ["device_sensors"] = """{"device":"core-sw1"}""",
        ["what_changed"] = """{"device":"core-sw1"}""",
    };

    private static Answer? LibreNms(string path) => path switch
    {
        "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        "devices" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.net","sysName":"core-sw1","status":1,"location":"Lab"}]}"""),
        "devices/1" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.net","sysName":"core-sw1","status":1,"location":"Lab"}]}"""),
        "faults" or "alerts" => new(200, """{"status":"ok","faults":[],"alerts":[]}"""),
        "rules" => new(200, """{"status":"ok","rules":[]}"""),
        "logs/eventlog" or "logs/alertlog" or "logs/eventlog/1" or "logs/alertlog/1" => new(200, """{"status":"ok","logs":[]}"""),
        "devices/1/ports" or "ports" => new(200, """{"status":"ok","ports":[{"port_id":7,"device_id":1,"ifName":"1/1/1","ifAdminStatus":"up","ifOperStatus":"up"}]}"""),
        "resources/sensors" => new(200, """{"status":"ok","sensors":[{"sensor_id":3,"device_id":1,"sensor_class":"temperature","sensor_descr":"Inlet","sensor_current":21}]}"""),
        "resources/links" => new(200, """{"status":"ok","links":[]}"""),
        _ => null,
    };

    [Fact]
    public async Task Every_tool_lists_an_output_schema_and_its_answer_fits_it_in_both_forms()
    {
        using var librenms = new FixtureServer(LibreNms);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
        });
        await server.InitializeAsync(cts.Token);
        var listed = (await server.RequestAsync(2, "tools/list", "{}", cts.Token)).GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("outputSchema"), StringComparer.Ordinal);
        Assert.Equal(ToolCatalogue.Names.Order(StringComparer.Ordinal), listed.Keys.Order(StringComparer.Ordinal));

        var id = 10;
        var answered = new List<string>();
        foreach (var (tool, schema) in listed)
        {
            Assert.Equal("object", schema.GetProperty("type").GetString());
            var response = await server.RequestAsync(id++, "tools/call", $$"""{"name":"{{tool}}","arguments":{{Arguments.GetValueOrDefault(tool, "{}")}}}""", cts.Token);
            AssertFits(tool, schema, response.GetProperty("result"));
            if (response.GetProperty("result").GetProperty("structuredContent").GetProperty("status").GetString() == "ok")
            {
                answered.Add(tool);
            }
        }

        // A check that only ever saw errors would prove little. Unimus and Graylog aren't set up here.
        Assert.True(answered.Count >= 12, string.Join(", ", answered));
        Assert.Contains("find_device", answered);
        Assert.Contains("health_overview", answered);

        // Another shape of the same tool, and a refusal of its arguments.
        AssertFits("active_alerts", listed["active_alerts"], (await server.RequestAsync(id++, "tools/call", """{"name":"active_alerts","arguments":{"hours":24}}""", cts.Token)).GetProperty("result"));
        AssertFits("find_device", listed["find_device"], (await server.RequestAsync(id++, "tools/call", """{"name":"find_device","arguments":{"limit":0}}""", cts.Token)).GetProperty("result"));
    }

    [Fact]
    public async Task A_rate_limited_call_has_both_forms_too()
    {
        using var librenms = new FixtureServer(LibreNms);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.RateLimitPerMinuteKey] = "1",
        });
        await server.InitializeAsync(cts.Token);
        var schema = (await server.RequestAsync(2, "tools/list", "{}", cts.Token)).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "find_device").GetProperty("outputSchema");

        await server.CallToolAsync(3, "find_device", "{}", cts.Token);
        var refused = (await server.RequestAsync(4, "tools/call", """{"name":"find_device","arguments":{}}""", cts.Token)).GetProperty("result");

        Assert.True(refused.GetProperty("isError").GetBoolean());
        AssertFits("find_device", schema, refused);
    }

    private static void AssertFits(string tool, JsonElement schema, JsonElement result)
    {
        var structured = result.GetProperty("structuredContent");
        using var text = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.True(JsonElement.DeepEquals(structured, text.RootElement), $"{tool}: the two forms differ");
        var problems = JsonSchemaCheck.Problems(schema, structured);
        Assert.True(problems.Count == 0, $"{tool}: {string.Join('\n', problems)}\n{structured}");
    }
}
