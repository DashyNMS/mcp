using DashyNMS.Agent.Devices;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

public sealed class DeviceResolverTests
{
    private static readonly Device[] Devices =
    [
        new() { DeviceId = 1, Hostname = "core-sw1.example.com", SysName = "core-sw1", Display = "Core switch 1", Ip = "10.0.0.1" },
        new() { DeviceId = 2, Hostname = "core-sw2.example.com", SysName = "core-sw2", Display = "Core switch 2", Ip = "10.0.0.2" },
        new() { DeviceId = 3, Hostname = "10.0.5.9", SysName = "fw-edge", Display = null, Ip = "10.0.5.9" },
        new() { DeviceId = 4, Hostname = "access-sw7.example.com", SysName = "access-sw7", Display = "Pit lane 7", Ip = "2001:db8::7" },
    ];

    private readonly IDevicesApi _api = Substitute.For<IDevicesApi>();
    private readonly DeviceResolver _resolver;

    public DeviceResolverTests()
    {
        _api.ListAsync(Arg.Any<CancellationToken>()).Returns(Devices);
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.Returns(_api);
        _resolver = new DeviceResolver(new DeviceDirectory(client, new FakeTimeProvider()));
    }

    private async Task<int> Resolve(string query) => (await _resolver.ResolveAsync(query, CancellationToken.None)).DeviceId;

    [Theory]
    [InlineData("2", 2)]
    [InlineData("core-sw1.example.com", 1)]
    [InlineData("CORE-SW1.EXAMPLE.COM", 1)]
    [InlineData("fw-edge", 3)]
    [InlineData("Pit lane 7", 4)]
    [InlineData("10.0.0.2", 2)]
    [InlineData("2001:0db8:0000::7", 4)]
    [InlineData("  core-sw2  ", 2)]
    public async Task Exact_matches(string query, int expected)
        => Assert.Equal(expected, await Resolve(query));

    [Fact]
    public async Task Short_hostname_matches_before_partial_names()
        => Assert.Equal(1, await Resolve("core-sw1"));

    [Fact]
    public async Task A_unique_part_of_a_name_matches()
        => Assert.Equal(4, await Resolve("access"));

    [Fact]
    public async Task Several_matches_list_the_candidates()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => Resolve("core"));

        Assert.StartsWith("2 devices match.", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Details);
    }

    [Fact]
    public async Task Device_text_stays_out_of_the_message()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => Resolve("core"));

        Assert.DoesNotContain("example.com", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nothing-like-this")]
    [InlineData("99")]
    public async Task No_match_is_an_error(string query)
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => Resolve(query));

        Assert.StartsWith("No device visible to this account matches.", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_input_is_an_error(string query)
        => await Assert.ThrowsAsync<ToolException>(() => Resolve(query));
}
