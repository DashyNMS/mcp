using DashyNMS.Agent.Devices;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

public sealed class DeviceDirectoryTests
{
    private readonly IDevicesApi _api = Substitute.For<IDevicesApi>();
    private readonly FakeTimeProvider _time = new();
    private readonly DeviceDirectory _directory;

    public DeviceDirectoryTests()
    {
        _api.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Device { DeviceId = 1 } });
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.Returns(_api);
        _directory = new DeviceDirectory(client, _time);
    }

    [Fact]
    public async Task Fetches_once_within_a_minute()
    {
        await _directory.GetAllAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(59));
        await _directory.GetAllAsync(CancellationToken.None);

        await _api.Received(1).ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fetches_again_after_a_minute()
    {
        await _directory.GetAllAsync(CancellationToken.None);
        _time.Advance(DeviceDirectory.Lifetime);
        await _directory.GetAllAsync(CancellationToken.None);

        await _api.Received(2).ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Parallel_callers_share_one_fetch()
    {
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _directory.GetAllAsync(CancellationToken.None)));

        await _api.Received(1).ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_fetch_is_not_cached()
    {
        _api.ListAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new LibreNmsApiException("down"),
            _ => new[] { new Device { DeviceId = 7 } });

        await Assert.ThrowsAsync<LibreNmsApiException>(() => _directory.GetAllAsync(CancellationToken.None));
        var devices = await _directory.GetAllAsync(CancellationToken.None);

        Assert.Equal(7, Assert.Single(devices).DeviceId);
    }
}
