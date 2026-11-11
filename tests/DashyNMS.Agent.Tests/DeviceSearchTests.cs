using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

public sealed class DeviceSearchTests
{
    [Theory]
    [InlineData("sw", null, null, null)]
    [InlineData(null, "down", null, null)]
    [InlineData(null, null, "Core switches", "Paddock")]
    [InlineData("aa:bb:cc:dd:ee:ff", null, null, null)]
    public async Task Typed_and_MCP_adapters_preserve_membership_and_paging(string? query, string? status, string? group, string? location)
    {
        var nms = new FakeLibreNms();
        var search = DeviceSearch.Create(nms.Client, new FakeTimeProvider(), nms.Options);
        var typed = await search.FindDevicesAsync(new(query, status, group, location, Limit: 1));
        var tools = new DeviceTools(nms.Client, nms.Directory, nms.Resolver, nms.Scope, nms.Runner, nms.Options);
        var json = FakeLibreNms.Parse(await tools.FindDevice(query, status, group, location, limit: 1));
        var data = json.GetProperty("data");
        var rows = data.GetProperty(typed.IsMacSearch ? "arp_matches" : "devices");
        Assert.Equal(typed.Matches.Select(m => m.Device.DeviceId), rows.EnumerateArray().Select(r =>
            (typed.IsMacSearch ? r.GetProperty("device") : r).GetProperty("device_id").GetInt32()));
        Assert.Equal(typed.Page.Total, data.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(typed.Summary, json.GetProperty("summary").GetString());
        if (typed.Page.NextOffset is { } next)
        {
            var second = await search.FindDevicesAsync(new(query, status, group, location, next, 1));
            Assert.DoesNotContain(second.Matches[0].Device.DeviceId, typed.Matches.Select(m => m.Device.DeviceId));
        }
    }

    [Fact]
    public async Task Ranking_precedes_paging_and_typed_rows_are_redacted()
    {
        var api = Substitute.For<IDevicesApi>();
        api.ListAsync(Arg.Any<CancellationToken>()).Returns(new Device[]
        {
            new() { DeviceId = 1, Hostname = "a-sw.example.net" },
            new() { DeviceId = 2, Hostname = "sw-prefix.example.net", Display = "password=synthetic-secret" },
            new() { DeviceId = 3, Hostname = "sw" },
        });
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.Returns(api);
        var search = DeviceSearch.Create(client, new FakeTimeProvider(), new AgentOptions());
        var result = await search.FindDevicesAsync(new(" sw "));
        Assert.Equal(new[] { 3, 2, 1 }, result.Matches.Select(m => m.Device.DeviceId));
        Assert.DoesNotContain("synthetic-secret", result.Matches[1].Device.Display!);
    }

    [Fact]
    public async Task Factory_caches_are_isolated_even_with_the_same_device_ids()
    {
        var a = new FakeLibreNms();
        var b = new FakeLibreNms();
        b.Transport.On("devices", """{"status":"ok","devices":[{"device_id":1,"hostname":"other.example.net"}]}""");
        var first = DeviceSearch.Create(a.Client, new FakeTimeProvider(), a.Options);
        var second = DeviceSearch.Create(b.Client, new FakeTimeProvider(), b.Options);
        await first.FindDevicesAsync(new("sw"));
        var result = await second.FindDevicesAsync(new("other"));
        Assert.Equal("other.example.net", Assert.Single(result.Matches).Device.Hostname);
    }

    [Fact]
    public async Task Cancellation_rejects_cached_results_and_makes_no_new_request()
    {
        var nms = new FakeLibreNms();
        var search = DeviceSearch.Create(nms.Client, new FakeTimeProvider(), nms.Options);
        await search.FindDevicesAsync(new("sw"));
        var count = nms.Transport.Requests.Count;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.FindDevicesAsync(new("sw"), cancel.Token));
        Assert.Equal(count, nms.Transport.Requests.Count);
    }

    [Fact]
    public async Task Late_cancelled_fetch_is_not_published_or_cached()
    {
        var api = Substitute.For<IDevicesApi>();
        var pending = new TaskCompletionSource<IReadOnlyList<Device>>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.ListAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.Returns(api);
        var search = DeviceSearch.Create(client, new FakeTimeProvider(), new AgentOptions());
        using var cancel = new CancellationTokenSource();
        var call = search.FindDevicesAsync(new("sw"), cancel.Token);
        cancel.Cancel();
        pending.SetResult(new[] { new Device { DeviceId = 1, Hostname = "sw" } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await search.FindDevicesAsync(new("sw"));
        await api.Received(2).ListAsync(Arg.Any<CancellationToken>());
        await api.Received(1).ListAsync(cancel.Token);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 51)]
    public async Task Invalid_paging_fails_before_transport(int offset, int limit)
    {
        var nms = new FakeLibreNms();
        var search = DeviceSearch.Create(nms.Client, new FakeTimeProvider(), nms.Options);
        await Assert.ThrowsAsync<ToolException>(() => search.FindDevicesAsync(new("sw", Offset: offset, Limit: limit)));
        Assert.Empty(nms.Transport.Requests);
    }

    [Theory]
    [InlineData(null, null, 0, 10)]
    [InlineData("sw", "broken", 0, 10)]
    [InlineData("sw", null, 0, 0)]
    public async Task Invalid_search_never_uses_transport(string? query, string? status, int offset, int limit)
    {
        var nms = new FakeLibreNms();
        var search = DeviceSearch.Create(nms.Client, nms.Time, nms.Options);
        await Assert.ThrowsAsync<ToolException>(() => search.FindDevicesAsync(new(query, status, Offset: offset, Limit: limit)));
        Assert.Empty(nms.Transport.Requests);
    }

    [Fact]
    public async Task Ranking_applies_to_each_page_including_beyond_end()
    {
        var nms = new FakeLibreNms();
        nms.Transport.On("devices", """
            {"devices":[{"device_id":1,"hostname":"a-sw.example.net"},
            {"device_id":2,"hostname":"sw-prefix.example.net"},{"device_id":3,"hostname":"sw"}]}
            """);
        var search = DeviceSearch.Create(nms.Client, nms.Time, nms.Options);
        foreach (var (offset, id) in new[] { (0, 3), (1, 2), (2, 1) })
        {
            var page = await search.FindDevicesAsync(new("sw", Offset: offset, Limit: 1));
            Assert.Equal(id, Assert.Single(page.Matches).Device.DeviceId);
            Assert.Equal(new PageInfo(offset, 1, 1, 3, offset < 2 ? offset + 1 : null), page.Page);
        }
        var end = await search.FindDevicesAsync(new("sw", Offset: 9, Limit: 1));
        Assert.Empty(end.Matches);
        Assert.Equal(new PageInfo(9, 1, 0, 3, null), end.Page);
    }

    [Fact]
    public async Task Group_cache_is_isolated_between_factories()
    {
        var a = new FakeLibreNms();
        var b = new FakeLibreNms();
        b.Transport.On("devicegroups", """{"groups":[{"id":1,"name":"Other group"}]}""");
        b.Transport.On("devicegroups/1", """{"devices":[{"device_id":2}]}""");
        var first = DeviceSearch.Create(a.Client, a.Time, a.Options);
        var second = DeviceSearch.Create(b.Client, b.Time, b.Options);
        Assert.Equal(1, Assert.Single((await first.FindDevicesAsync(new(Group: "Core switches"))).Matches).Device.DeviceId);
        Assert.Equal(2, Assert.Single((await second.FindDevicesAsync(new(Group: "Other group"))).Matches).Device.DeviceId);
        Assert.Contains("devicegroups", b.Transport.Requests);
    }

    [Fact]
    public async Task Scope_cancellation_rejects_warm_and_unfiltered_results()
    {
        var nms = new FakeLibreNms();
        await nms.Scope.ListGroupsAsync(default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var count = nms.Transport.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nms.Scope.ListGroupsAsync(cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nms.Scope.ResolveAsync(null, null, cancel.Token));
        Assert.Equal(count, nms.Transport.Requests.Count);
    }

    [Fact]
    public async Task Scope_queued_cancellation_and_late_response_do_not_populate_cache()
    {
        var client = Substitute.For<ILibreNmsClient>();
        var pending = new TaskCompletionSource<IReadOnlyList<DeviceGroup>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DeviceGroups.ListAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        var time = new FakeTimeProvider();
        var scope = new DeviceScope(client, new DeviceDirectory(client, time), time);
        using var late = new CancellationTokenSource();
        using var queued = new CancellationTokenSource();
        var first = scope.ListGroupsAsync(late.Token);
        var second = scope.ListGroupsAsync(queued.Token);
        queued.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        late.Cancel();
        pending.SetResult([]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await scope.ListGroupsAsync(default);
        await client.DeviceGroups.Received(2).ListAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MCP_contract_has_independent_expected_fields_and_redaction(bool verbose)
    {
        var nms = new FakeLibreNms();
        nms.Transport.On("devices", """
            {"devices":[{"device_id":7,"hostname":"sw.example.net","display":"password=synthetic-secret",
            "sysName":"switch","ip":"192.0.2.7","os":"test-os","hardware":"test-model","location":"Lab","status":1}]}
            """);
        var tools = new DeviceTools(nms.Client, nms.Directory, nms.Resolver, nms.Scope, nms.Runner, nms.Options);
        var raw = await tools.FindDevice("sw", verbose: verbose);
        Assert.DoesNotContain("synthetic-secret", raw);
        var root = FakeLibreNms.Parse(raw);
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("1 matching device.", root.GetProperty("summary").GetString());
        var row = Assert.Single(root.GetProperty("data").GetProperty("devices").EnumerateArray());
        Assert.Equal(7, row.GetProperty("device_id").GetInt32());
        Assert.Equal("sw.example.net", row.GetProperty("hostname").GetString());
        Assert.Equal("192.0.2.7", row.GetProperty("ip").GetString());
        Assert.Equal("Lab", row.GetProperty("location").GetString());
        Assert.Equal("up", row.GetProperty("status").GetString());
        Assert.Equal(verbose, row.TryGetProperty("sys_name", out _));
        Assert.Equal(verbose, row.TryGetProperty("os", out _));
        if (verbose) Assert.Equal("test-model", row.GetProperty("hardware").GetString());
    }

    [Fact]
    public async Task MCP_ARP_contract_preserves_observation_and_page()
    {
        var nms = new FakeLibreNms();
        var tools = new DeviceTools(nms.Client, nms.Directory, nms.Resolver, nms.Scope, nms.Runner, nms.Options);
        var root = FakeLibreNms.Parse(await tools.FindDevice("aa:bb:cc:dd:ee:ff"));
        var data = root.GetProperty("data");
        var match = Assert.Single(data.GetProperty("arp_matches").EnumerateArray());
        Assert.Equal(1, match.GetProperty("device").GetProperty("device_id").GetInt32());
        var sighting = match.GetProperty("seen_as")[0];
        Assert.Equal("10.0.10.50", sighting.GetProperty("ip").GetString());
        Assert.Equal("aabbccddeeff", sighting.GetProperty("mac").GetString());
        Assert.Equal(101, sighting.GetProperty("port_id").GetInt32());
        Assert.Equal(1, data.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("page").GetProperty("returned").GetInt32());
    }

    [Fact]
    public async Task Directory_queued_cancellation_does_not_interrupt_owner_or_fetch_again()
    {
        var client = Substitute.For<ILibreNmsClient>();
        var pending = new TaskCompletionSource<IReadOnlyList<Device>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        var directory = new DeviceDirectory(client, new FakeTimeProvider());
        var owner = directory.GetAllAsync(default);
        using var cancel = new CancellationTokenSource();
        var queued = directory.GetAllAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        pending.SetResult([]);
        await owner;
        await directory.GetAllAsync(default);
        await client.Devices.Received(1).ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Supplied_redactor_cleans_typed_ARP_fields_and_MCP_output()
    {
        var nms = new FakeLibreNms();
        nms.Transport.On("resources/ip/arp/aa:bb:cc:dd:ee:ff", """
            {"arp":[{"device_id":1,"port_id":107,
            "ipv4_address":"password=synthetic-ip","mac_address":"token=synthetic-mac"}]}
            """);
        var redactor = new Redactor();
        var runner = new ToolRunner(redactor, nms.Options, NullLogger<ToolRunner>.Instance);
        Assert.Same(redactor, runner.Redactor);
        var search = DeviceSearch.Create(nms.Client, nms.Time, nms.Options, redactor);
        var match = Assert.Single((await search.FindDevicesAsync(new("aa:bb:cc:dd:ee:ff"))).Matches);
        var sighting = Assert.Single(match.SeenAs);
        Assert.Equal(new ArpSighting($"password={Redactor.Marker}", $"token={Redactor.Marker}", 107), sighting);

        var tools = new DeviceTools(nms.Client, nms.Directory, nms.Resolver, nms.Scope, runner, nms.Options);
        var raw = await tools.FindDevice("aa:bb:cc:dd:ee:ff");
        Assert.DoesNotContain("synthetic-ip", raw);
        Assert.DoesNotContain("synthetic-mac", raw);
        var row = Assert.Single(FakeLibreNms.Parse(raw).GetProperty("data").GetProperty("arp_matches").EnumerateArray());
        var observation = Assert.Single(row.GetProperty("seen_as").EnumerateArray());
        Assert.Equal(107, observation.GetProperty("port_id").GetInt32());
        Assert.Equal(sighting.Ip, observation.GetProperty("ip").GetString());
        Assert.Equal(sighting.Mac, observation.GetProperty("mac").GetString());
    }

    [Fact]
    public async Task Cancellation_after_ARP_fetch_stops_before_device_fetch()
    {
        var client = Substitute.For<ILibreNmsClient>();
        var pending = new TaskCompletionSource<IReadOnlyList<ArpEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Arp.FindByMacAsync("aa:bb:cc:dd:ee:ff", Arg.Any<CancellationToken>()).Returns(pending.Task);
        var search = DeviceSearch.Create(client, new FakeTimeProvider(), new AgentOptions());
        using var cancel = new CancellationTokenSource();
        var call = search.FindDevicesAsync(new("aa:bb:cc:dd:ee:ff"), cancel.Token);
        cancel.Cancel();
        pending.SetResult([new() { DeviceId = 1, PortId = 107 }]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await client.Arp.Received(1).FindByMacAsync("aa:bb:cc:dd:ee:ff", cancel.Token);
        await client.Devices.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_after_group_members_fetch_stops_before_device_fetch()
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.DeviceGroups.ListAsync(Arg.Any<CancellationToken>()).Returns(new DeviceGroup[] { new() { Id = 1, Name = "Lab" } });
        var pending = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DeviceGroups.GetMemberDeviceIdsAsync(1, Arg.Any<CancellationToken>()).Returns(pending.Task);
        var search = DeviceSearch.Create(client, new FakeTimeProvider(), new AgentOptions());
        using var cancel = new CancellationTokenSource();
        var call = search.FindDevicesAsync(new(Group: "Lab"), cancel.Token);
        cancel.Cancel();
        pending.SetResult([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await client.DeviceGroups.Received(1).GetMemberDeviceIdsAsync(1, cancel.Token);
        await client.Devices.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

}
