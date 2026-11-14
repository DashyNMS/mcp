using DashyNMS.Agent.Health;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Shared.Tests;

public sealed class HostHealthOverviewTests
{
    [Theory]
    [InlineData("temperature", 48)]
    [InlineData("fanspeed", 750)]
    [InlineData("signal", -75)]
    [InlineData("dbm", -13)]
    public async Task Captured_custom_thresholds_match_Core_for_every_supported_class(string type, double value)
    {
        var settings = new AppSettings
        {
            TemperatureThresholds = new() { LowCritical = -10, LowWarning = 0, HighWarning = 30, HighCritical = 40 },
            FanSpeedThresholds = new() { LowCritical = 1000, LowWarning = 1200, HighWarning = 20000, HighCritical = 25000 },
            SignalThresholds = new() { CriticalThreshold = -72, WarningThreshold = -70 },
            DbmThresholds = new() { CriticalThreshold = -12.5, WarningThreshold = -12 },
        };
        var options = new AgentOptions
        {
            TemperatureThresholds = settings.TemperatureThresholds, FanSpeedThresholds = settings.FanSpeedThresholds,
            SignalThresholds = settings.SignalThresholds, DbmThresholds = settings.DbmThresholds,
        };
        var sensor = new Sensor { SensorId = 1, DeviceId = 1, SensorClass = type, Current = value };
        var expected = SensorCategoryRegistry.Resolve(type)!.Thresholds(settings, sensor).Evaluate(value);
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Device { DeviceId = 1 } });
        client.Sensors.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { sensor });
        using var host = HostHealthOverview.Create(client, Substitute.For<ILibreNmsTransport>(), TimeProvider.System, options);
        // Mutation after construction must not alter the captured assessment.
        settings.TemperatureThresholds.HighCritical = 90;
        settings.FanSpeedThresholds.LowCritical = 0;
        settings.SignalThresholds.CriticalThreshold = -90;
        settings.DbmThresholds.CriticalThreshold = -30;
        var result = (await host.ReadAsync(new(Type: type, OnlyProblems: false))).Result!;
        Assert.Equal("critical", Assert.Single(result.Data.Readings).Reading.State);
        Assert.Equal(expected.ToString().ToLowerInvariant(), result.Data.Readings[0].Reading.State);
        host.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadAsync(new()));
    }

    [Theory]
    [InlineData(false, "ok")]
    [InlineData(true, "critical")]
    public async Task Sensor_limits_win_unless_the_host_overrides_them(bool overrideLimits, string expected)
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Device { DeviceId = 1 } });
        client.Sensors.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new Sensor { SensorId = 1, DeviceId = 1, SensorClass = "temperature", Current = 48, LimitHighWarn = 60, LimitHigh = 80 },
        });
        using var host = HostHealthOverview.Create(client, Substitute.For<ILibreNmsTransport>(), TimeProvider.System, new AgentOptions
        {
            TemperatureThresholds = new() { LowCritical = -10, LowWarning = 0, HighWarning = 30, HighCritical = 40 },
            OverrideSensorLimitsWithAppThresholds = overrideLimits,
        });
        var result = (await host.ReadAsync(new(Type: "temperature", OnlyProblems: false))).Result!;
        Assert.Equal(expected, Assert.Single(result.Data.Readings).Reading.State);
    }
}
