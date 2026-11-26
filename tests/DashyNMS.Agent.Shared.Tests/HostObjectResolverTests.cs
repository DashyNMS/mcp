using System.Net;
using DashyNMS.Agent.Devices;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Shared.Tests;

public sealed class HostObjectResolverTests
{
    [Fact]
    public async Task Devices_are_fresh_exact_and_redacted()
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.GetAsync("12", Arg.Any<CancellationToken>()).Returns(new Device
            { DeviceId = 12, Display = "password=synthetic-secret" });
        using var resolver = HostObjectResolver.Create(client);
        var target = new IntelligenceObjectReference("device", 12);
        var outcome = await resolver.ResolveAsync(target);
        Assert.Null(outcome.Failure);
        Assert.Equal(target, outcome.Result!.Identity);
        Assert.DoesNotContain("synthetic-secret", outcome.Result.Name);
        client.Devices.GetAsync("12", Arg.Any<CancellationToken>()).Returns(new Device { DeviceId = 13 });
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(target)).Failure);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("location")]
    public async Task Scope_objects_resolve_by_id(string kind)
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.DeviceGroups.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new DeviceGroup { Id = 12, Name = "Example" } });
        client.Locations.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Location { Id = 12, Name = "Example" } });
        using var resolver = HostObjectResolver.Create(client);
        Assert.Equal("Example", (await resolver.ResolveAsync(new(kind, 12))).Result!.Name);
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(new(kind, 13))).Failure);
    }

    [Theory]
    [InlineData("sensor")]
    [InlineData("port")]
    public async Task Child_objects_require_the_exact_parent(string kind)
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.Sensors.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { new Sensor { SensorId = 12, DeviceId = 3 } });
        client.Ports.ListForDeviceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { new Port { PortId = 12, DeviceId = 3 } });
        using var resolver = HostObjectResolver.Create(client);
        Assert.NotNull((await resolver.ResolveAsync(new(kind, 12, 3))).Result);
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(new(kind, 12, 4))).Failure);
    }

    [Theory]
    [InlineData("alert", AlertSource.Alert)]
    [InlineData("fault", AlertSource.Fault)]
    public async Task Alerts_keep_source_parent_and_rule_in_all_states(string kind, AlertSource source)
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.Alerts.ListAsync(Arg.Any<AlertQuery>(), Arg.Any<CancellationToken>()).Returns(new[]
            { new Alert { Id = 12, DeviceId = 3, RuleId = 4, Source = source, StateValue = 0 } });
        using var resolver = HostObjectResolver.Create(client);
        Assert.NotNull((await resolver.ResolveAsync(new(kind, 12, 3, 4))).Result);
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(new(kind, 12, 3, 5))).Failure);
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(new(kind, 12, 9, 4))).Failure);
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(new(kind == "fault" ? "alert" : "fault", 12, 3, 4))).Failure);
        await client.Alerts.Received().ListAsync(Arg.Is<AlertQuery>(q => q.Id == 12 && q.States != null && q.States.SequenceEqual(AlertQuery.All.States!)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unknown", 12, null)]
    [InlineData("device", 0, null)]
    [InlineData("sensor", 12, null)]
    public async Task Invalid_references_make_no_requests(string kind, int id, int? deviceId)
    {
        var client = Substitute.For<ILibreNmsClient>();
        using var resolver = HostObjectResolver.Create(client);
        Assert.Equal(HostFailure.InvalidRequest, (await resolver.ResolveAsync(new(kind, id, deviceId))).Failure);
        Assert.Empty(client.ReceivedCalls());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, HostFailure.Refused)]
    [InlineData(HttpStatusCode.InternalServerError, HostFailure.Unavailable)]
    public async Task Source_failures_do_not_escape(HttpStatusCode status, HostFailure expected)
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.GetAsync("12", Arg.Any<CancellationToken>()).Returns<Task<Device?>>(
            _ => throw new LibreNmsApiException("synthetic-secret", status));
        using var resolver = HostObjectResolver.Create(client);
        var outcome = await resolver.ResolveAsync(new("device", 12));
        Assert.Null(outcome.Result);
        Assert.Equal(expected, outcome.Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_discards_uncooperative_late_results(bool retire)
    {
        var client = Substitute.For<ILibreNmsClient>();
        var pending = new TaskCompletionSource<Device?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Devices.GetAsync("12", Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var resolver = HostObjectResolver.Create(client);
        using var cancel = new CancellationTokenSource();
        var call = resolver.ResolveAsync(new("device", 12), cancel.Token);
        if (retire) resolver.Dispose(); else cancel.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            if (retire) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(new("device", 12)));
        }
        finally { pending.TrySetResult(new Device { DeviceId = 12 }); }
    }
}
