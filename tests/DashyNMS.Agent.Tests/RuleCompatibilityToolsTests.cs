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
        Assert.Contains("1 rule uses columns this LibreNMS version removed", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Before_26_10_nothing_is_removed_yet()
    {
        _nms.Transport.On("system", """{"status":"ok","system":[{"local_ver":"26.9.1.1"}]}""");

        var rules = await RulesAsync();

        Assert.False(rules[7].TryGetProperty("removed_columns", out _));
    }

    [Fact]
    public async Task On_26_11_the_fdb_vlan_change_and_snmp_columns_are_flagged()
    {
        _nms.Transport.On("system", """{"status":"ok","system":[{"local_ver":"26.11.0"}]}""");
        _nms.Transport.On("rules", """
            {"status":"ok","rules":[
              {"id":9,"name":"Camera VLAN","severity":"warning","query":"SELECT * FROM devices,ports_fdb WHERE (devices.device_id = ? AND ports_fdb.vlan_id = 50)","devices":[],"groups":[],"locations":[],"disabled":0},
              {"id":10,"name":"SNMP v1","severity":"warning","query":"SELECT * FROM devices WHERE devices.snmpver = 'v1'","devices":[],"groups":[],"locations":[],"disabled":0},
              {"id":11,"name":"VLAN table","severity":"warning","query":"SELECT * FROM devices,vlans WHERE vlans.vlan_id = 50","devices":[],"groups":[],"locations":[],"disabled":0}
            ]}
            """);

        var rules = await RulesAsync();

        var vlan = Assert.Single(rules[9].GetProperty("removed_columns").EnumerateArray());
        Assert.Equal("ports_fdb.vlan_id", vlan.GetProperty("column").GetString());
        Assert.Equal("ports_fdb.vlan", vlan.GetProperty("replacement").GetString());
        Assert.Contains("VLAN number", vlan.GetProperty("advice").GetString(), StringComparison.Ordinal);
        Assert.Equal("devices.snmpver", Assert.Single(rules[10].GetProperty("removed_columns").EnumerateArray()).GetProperty("column").GetString());
        Assert.False(rules[11].TryGetProperty("removed_columns", out _)); // another table's vlan_id
    }

    [Fact]
    public async Task On_26_10_the_26_11_removals_are_not_flagged_yet()
    {
        _nms.Transport.On("system", """{"status":"ok","system":[{"local_ver":"26.10.1"}]}""");
        _nms.Transport.On("rules", """
            {"status":"ok","rules":[
              {"id":9,"name":"Camera VLAN","severity":"warning","query":"SELECT * FROM devices,ports_fdb WHERE ports_fdb.vlan_id = 50","devices":[],"groups":[],"locations":[],"disabled":0}
            ]}
            """);

        Assert.False((await RulesAsync())[9].TryGetProperty("removed_columns", out _));
    }

    [Fact]
    public async Task An_unreadable_version_flags_what_may_be_removed_and_says_so()
    {
        _nms.Transport.On("system", """{"status":"ok","system":[{"local_ver":"master"}]}""");
        _nms.Transport.On("rules", """
            {"status":"ok","rules":[
              {"id":9,"name":"Camera VLAN","severity":"warning","query":"SELECT * FROM devices,ports_fdb WHERE ports_fdb.vlan_id = 50","devices":[],"groups":[],"locations":[],"disabled":0},
              {"id":10,"name":"SNMP v1","severity":"warning","query":"SELECT * FROM devices WHERE devices.snmpver = 'v1'","devices":[],"groups":[],"locations":[],"disabled":0},
              {"id":11,"name":"VLAN table","severity":"warning","query":"SELECT * FROM devices,vlans WHERE vlans.vlan_id = 50","devices":[],"groups":[],"locations":[],"disabled":0}
            ]}
            """);

        var root = FakeLibreNms.Parse(await Rules().AlertRules(cancellationToken: CancellationToken.None));
        var rules = ById(root);

        Assert.True(rules[9].TryGetProperty("removed_columns", out _));
        Assert.True(rules[10].TryGetProperty("removed_columns", out _));
        Assert.False(rules[11].TryGetProperty("removed_columns", out _));
        Assert.Contains("version couldn't be read", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    private async Task<Dictionary<int, JsonElement>> RulesAsync()
        => ById(FakeLibreNms.Parse(await Rules().AlertRules(cancellationToken: CancellationToken.None)));

    private static Dictionary<int, JsonElement> ById(JsonElement root)
        => root.GetProperty("data").GetProperty("rules").EnumerateArray().ToDictionary(r => r.GetProperty("rule_id").GetInt32());
}
