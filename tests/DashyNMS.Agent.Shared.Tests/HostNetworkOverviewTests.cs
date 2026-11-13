using System.Net;
using DashyNMS.Agent.Overview;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Shared.Tests;

public sealed class HostNetworkOverviewTests
{
    private static ILibreNmsClient Client()
    {
        var client = Substitute.For<ILibreNmsClient>();
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<Device>)[new() { DeviceId = 1, Status = true, Disabled = false, Ignore = false }]);
        client.Alerts.ListAsync(Arg.Any<AlertQuery?>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<Alert>)[]);
        client.Ports.ListAllStatusAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<Port>)[]);
        client.System.GetAsync(Arg.Any<CancellationToken>()).Returns(new SystemInfo());
        return client;
    }

    [Fact]
    public async Task Optional_sources_have_safe_categories_and_never_become_zero_successes()
    {
        var client = Client();
        client.Ports.ListAllStatusAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<Port>>>(_ => throw new LibreNmsApiException("password=synthetic-secret", HttpStatusCode.Forbidden));
        using var overview = HostNetworkOverview.Create(client, TimeProvider.System, true);
        var result = (await overview.ReadAsync()).Result!;
        Assert.Null(result.Ports);
        Assert.Equal(new OverviewUnavailable("ports", HostFailure.Refused), Assert.Single(result.Unavailable));
        Assert.NotNull(result.Alerts);
    }

    [Fact]
    public async Task Required_source_failure_has_no_server_details()
    {
        var client = Client();
        client.Devices.ListAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<Device>>>(_ => throw new LibreNmsApiException("password=synthetic-secret", HttpStatusCode.Forbidden));
        using var overview = HostNetworkOverview.Create(client, TimeProvider.System, true);
        var result = await overview.ReadAsync();
        Assert.Null(result.Result);
        Assert.Equal(HostFailure.Refused, result.Error!.Code);
        Assert.DoesNotContain("synthetic-secret", result.Error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retirement_and_cancellation_bound_uncooperative_sources(bool retire)
    {
        var client = Client();
        var pending = new TaskCompletionSource<IReadOnlyList<Port>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Ports.ListAllStatusAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var overview = HostNetworkOverview.Create(client, TimeProvider.System, true);
        using var cancel = new CancellationTokenSource();
        var call = overview.ReadAsync(cancel.Token);
        if (retire) overview.Dispose(); else cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        pending.SetException(new InvalidOperationException("synthetic-secret"));
        if (retire) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => overview.ReadAsync());
    }
}
