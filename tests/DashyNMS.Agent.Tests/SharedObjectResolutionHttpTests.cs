using System.Text.Json;
using DashyNMS.Agent.Devices;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

/// <summary>Exercise the pinned Core client's source detection, routes and state serialisation.</summary>
public sealed class SharedObjectResolutionHttpTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Exact_identity_survives_real_source_routing_in_every_state(bool faults, int state)
    {
        var route = faults ? "faults" : "alerts";
        using var server = new FixtureServer(_ => null, requestAnswer: request =>
        {
            var path = request.Url!.AbsolutePath;
            if (path == "/api/v0/faults" && request.QueryString["state"] == "-1")
                return faults ? new Answer(200, """{"status":"ok","alerts":[]}""")
                    : new Answer(404, """{"status":"error"}""");
            if (path != $"/api/v0/{route}/12" && path != $"/api/v0/{route}/99") return null;
            // Model the server's state filter rather than returning a row unconditionally.
            var states = request.QueryString["state"]?.Split(',');
            var visible = path.EndsWith("/12", StringComparison.Ordinal)
                && states?.Contains(state.ToString(System.Globalization.CultureInfo.InvariantCulture)) == true;
            return new Answer(200, JsonSerializer.Serialize(new
            {
                status = "ok",
                alerts = visible ? new[] { new { id = 12, device_id = 3, rule_id = 4, state } }
                    : Array.Empty<object>()
            }));
        });
        using var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        var client = provider.GetRequiredService<ILibreNmsClient>();
        client.Connect(new LibreNmsConnection(server.Root, FixtureServer.Token));
        using var resolver = HostObjectResolver.Create(client);
        var target = new IntelligenceObjectReference(faults ? "fault" : "alert", 12, 3, 4);

        var outcome = await resolver.ResolveAsync(target);

        Assert.Null(outcome.Failure);
        Assert.Equal(target, outcome.Result!.Identity);
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(target with { Kind = faults ? "alert" : "fault" })).Failure);
        Assert.Equal(HostFailure.NotFound, (await resolver.ResolveAsync(target with { Id = 99 })).Failure);
        Assert.Contains("/api/v0/faults?state=-1", server.HttpRequests);
        Assert.Contains($"/api/v0/{route}/12?state=0%2C1%2C2", server.HttpRequests);
        Assert.DoesNotContain(server.Requests, p => p.StartsWith(faults ? "alerts" : "faults/", StringComparison.Ordinal));
    }
}
