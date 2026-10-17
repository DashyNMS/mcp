using DashyNMS.Agent.Tools;

namespace DashyNMS.Agent.Tests;

public sealed class PortAndAlertToolsTests
{
    private readonly FakeLibreNms _nms = new();

    private PortTools Ports() => new(_nms.Client, _nms.Resolver, _nms.Runner, _nms.Options);

    private AlertTools Alerts() => new(_nms.Client, _nms.Directory, _nms.Resolver, _nms.Runner, _nms.Options, _nms.Time);

    [Fact]
    public async Task Port_status_shows_state_rates_and_neighbour()
    {
        var root = FakeLibreNms.Parse(await Ports().PortStatus("core-sw1", "1/1/1", CancellationToken.None));

        Assert.Equal("Port is up.", root.GetProperty("summary").GetString());
        var port = root.GetProperty("data").GetProperty("port");
        Assert.Equal(10_000, port.GetProperty("speed_mbps").GetInt64());
        Assert.Equal(100, port.GetProperty("in_mbps").GetDouble());
        Assert.Equal("Uplink to edge-sw3", port.GetProperty("alias").GetString());

        var neighbour = root.GetProperty("data").GetProperty("neighbours")[0];
        Assert.Equal("lldp", neighbour.GetProperty("protocol").GetString());
        Assert.Equal("edge-sw3", neighbour.GetProperty("remote_hostname").GetString());
    }

    [Theory]
    [InlineData("Camera 7", "Port is down while enabled.")]
    [InlineData("107", "Port is down while enabled.")]
    [InlineData("1/1/8", "Port is up, counting errors.")]
    [InlineData("spare", "Port is administratively down.")]
    public async Task Port_is_found_by_name_alias_or_id(string port, string summary)
    {
        var root = FakeLibreNms.Parse(await Ports().PortStatus("1", port, CancellationToken.None));

        Assert.Equal(summary, root.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Ambiguous_port_lists_candidates()
    {
        var root = FakeLibreNms.Parse(await Ports().PortStatus("1", "camera", CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("details").GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public async Task Alerts_are_grouped_by_device_newest_first()
    {
        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts(cancellationToken: CancellationToken.None));

        Assert.Equal("3 open alerts on 2 devices (2 critical, 1 warning).", root.GetProperty("summary").GetString());
        var devices = root.GetProperty("data").GetProperty("devices");
        Assert.Equal(1, devices[0].GetProperty("device_id").GetInt32());
        Assert.Equal(20, devices[0].GetProperty("alerts")[0].GetProperty("age_minutes").GetInt64());
        Assert.Equal("acknowledged", devices[0].GetProperty("alerts")[1].GetProperty("state").GetString());
        Assert.Equal("down", devices[1].GetProperty("device_status").GetString());
    }

    [Fact]
    public async Task Alerts_filter_by_severity_and_device()
    {
        var critical = FakeLibreNms.Parse(await Alerts().ActiveAlerts("critical", "core-sw1", CancellationToken.None));

        Assert.Equal("1 open alert on 1 device (1 critical, 0 warning).", critical.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Unknown_severity_is_an_error()
    {
        var root = FakeLibreNms.Parse(await Alerts().ActiveAlerts("urgent", cancellationToken: CancellationToken.None));

        Assert.Equal("error", root.GetProperty("status").GetString());
    }
}
