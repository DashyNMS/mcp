using DashyNMS.Agent.Health;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Shared.Tests;

public sealed class HostThresholdTests
{
    [Fact]
    public async Task Host_captures_thresholds_before_any_reads_and_ignores_later_mutations()
    {
        var thresholds = BandThresholdSettings.TemperatureDefaults();
        var client = Substitute.For<ILibreNmsClient>();
        var pending = new TaskCompletionSource<IReadOnlyList<Sensor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Device { DeviceId = 1 } });
        client.Sensors.ListAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var host = HostDeviceSensors.Create(client, Substitute.For<ILibreNmsTransport>(), TimeProvider.System,
            new AgentOptions { TemperatureThresholds = thresholds });
        thresholds.HighCritical = 40;
        var reading = host.ReadAsync(new(1));
        thresholds.HighWarning = 30;
        pending.SetResult(new[] { new Sensor { SensorId = 1, DeviceId = 1, SensorClass = "temperature", Current = 48 } });
        Assert.Equal("ok", Assert.Single((await reading).Result!.Readings).State);
    }

    [Theory]
    [InlineData(false, false, "ok")]
    [InlineData(true, false, "warning")]
    [InlineData(true, true, "critical")]
    public async Task Host_thresholds_match_Core_and_respect_sensor_limits(bool changed, bool overrideLimits, string expected)
    {
        var sensor = new Sensor { SensorId = 1, DeviceId = 1, SensorClass = "temperature", Current = 48, LimitHighWarn = changed ? 45 : 60, LimitHigh = 80 };
        var settings = new AppSettings
        {
            TemperatureThresholds = changed
                ? new BandThresholdSettings { LowCritical = -10, LowWarning = 0, HighWarning = 30, HighCritical = 40 }
                : BandThresholdSettings.TemperatureDefaults(),
            OverrideSensorLimitsWithAppThresholds = overrideLimits,
        };
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Device { DeviceId = 1 } });
        client.Sensors.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { sensor });
        using var host = HostDeviceSensors.Create(client, Substitute.For<ILibreNmsTransport>(), TimeProvider.System,
            new AgentOptions { TemperatureThresholds = settings.TemperatureThresholds, OverrideSensorLimitsWithAppThresholds = overrideLimits });
        var reading = Assert.Single((await host.ReadAsync(new(1))).Result!.Readings);
        Assert.Equal(expected, reading.State);
        var core = SensorCategoryRegistry.Resolve("temperature")!.Thresholds(settings, sensor).Evaluate(sensor.Current);
        Assert.Equal(core.ToString().ToLowerInvariant(), reading.State);
    }

    [Theory]
    [InlineData("dbm", -13)]
    [InlineData("signal", -75)]
    [InlineData("fanspeed", 750)]
    public void Unconfigured_options_preserve_default_assessment(string type, double value)
    {
        var sensor = new Sensor { SensorClass = type, Current = value };
        Assert.Equal(SensorReading.From(sensor), SensorReading.From(sensor, new AgentOptions().SensorSettings()));
    }
}
