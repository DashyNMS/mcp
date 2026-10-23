using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

public sealed class ScopeIdentityTests
{
    [Fact]
    public async Task Scope_results_keep_group_ids_and_only_unambiguous_location_ids()
    {
        var nms = new FakeLibreNms();
        nms.Transport.On("devices", """
            {"status":"ok","devices":[
            {"device_id":1,"hostname":"a.example.net","location":"Site A","location_id":9,"status":1},
            {"device_id":2,"hostname":"b.example.net","location":"Duplicate","location_id":10,"status":1},
            {"device_id":3,"hostname":"c.example.net","location":"Duplicate","location_id":11,"status":1}]}
            """);
        var tool = new ScopeTools(nms.Directory, nms.Scope, nms.Runner);
        var data = FakeLibreNms.Parse(await tool.ListGroupsAndLocations(CancellationToken.None)).GetProperty("data");
        Assert.All(data.GetProperty("groups").EnumerateArray(), group => Assert.True(group.GetProperty("group_id").GetInt32() > 0));
        var site = data.GetProperty("locations").EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Site A");
        Assert.Equal(9, site.GetProperty("location_id").GetInt32());
        var ambiguous = data.GetProperty("locations").EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Duplicate");
        Assert.False(ambiguous.TryGetProperty("location_id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.Number);
    }
}
