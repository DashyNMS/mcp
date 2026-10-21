using System.Text.Json;
using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

/// <summary>alert_rules on LibreNMS 26.10: per-entity settings and removed columns (#78).</summary>
public sealed class RuleCompatibilityToolsTests
{
    private readonly FakeLibreNms _nms = new();

    public RuleCompatibilityToolsTests()
    {
        _nms.Transport.On("rules", """
            {"status":"ok","rules":[
              {"id":3,"name":"Port down","severity":"critical","rule":"ports.ifOperStatus = \"down\"","devices":[],"groups":[],"locations":[],"disabled":0,
               "notify_per_entity":true,"max_entities":null},
              {"id":5,"name":"High CPU","severity":"warning","rule":"processors.processor_usage >= 90","devices":[],"groups":[],"locations":[],"disabled":0,
               "notify_per_entity":true,"max_entities":8},
              {"id":7,"name":"Slow ping","severity":"warning","query":"SELECT * FROM devices WHERE devices.last_ping_timetaken > 200","devices":[],"groups":[],"locations":[],"disabled":0,
               "notify_per_entity":false,"max_entities":null}
            ]}
            """);
    }

    private RuleTools Rules() => new(_nms.Client, _nms.Fleet, _nms.Directory, _nms.Scope, _nms.Runner, _nms.Options, _nms.Time);

    [Fact]
    public async Task Rules_say_how_acknowledging_one_fault_works()
    {
        var rules = await RulesAsync();

        var port = rules[3];
        Assert.True(port.GetProperty("notify_per_entity").GetBoolean());
        Assert.False(port.TryGetProperty("max_entities", out _));
        Assert.True(port.GetProperty("max_entities_inherited").GetBoolean());
        Assert.Contains("server's default limit", port.GetProperty("acknowledging").GetString(), StringComparison.Ordinal);

        Assert.Equal(8, rules[5].GetProperty("max_entities").GetInt32());
        Assert.Contains("no more than 8", rules[5].GetProperty("acknowledging").GetString(), StringComparison.Ordinal);

        Assert.Contains("acknowledges every open fault", rules[7].GetProperty("acknowledging").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removed_columns_are_flagged_with_their_replacement()
    {
        var root = FakeLibreNms.Parse(await Rules().AlertRules(cancellationToken: CancellationToken.None));
        var rules = ById(root);

        var removed = Assert.Single(rules[7].GetProperty("removed_columns").EnumerateArray());
        Assert.Equal("devices.last_ping_timetaken", removed.GetProperty("column").GetString());
        Assert.Equal("device_stats.ping_rtt_last", removed.GetProperty("replacement").GetString());
        Assert.False(rules[3].TryGetProperty("removed_columns", out _));
        Assert.Contains("1 rule uses device columns LibreNMS 26.10 removed", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Before_26_10_nothing_is_removed_yet()
    {
        _nms.Transport.On("system", """{"status":"ok","system":[{"local_ver":"26.9.1.1"}]}""");

        var rules = await RulesAsync();

        Assert.False(rules[7].TryGetProperty("removed_columns", out _));
    }

    private async Task<Dictionary<int, JsonElement>> RulesAsync()
        => ById(FakeLibreNms.Parse(await Rules().AlertRules(cancellationToken: CancellationToken.None)));

    private static Dictionary<int, JsonElement> ById(JsonElement root)
        => root.GetProperty("data").GetProperty("rules").EnumerateArray().ToDictionary(r => r.GetProperty("rule_id").GetInt32());
}
