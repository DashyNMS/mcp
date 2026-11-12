using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Tools;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

/// <summary>
/// The shared device search fixture served over loopback HTTP to the MCP server's own wiring
/// (AddDashyNmsAgent, Core's real LibreNmsClient and LibreNmsTransport, the find_device tool as
/// the MCP SDK builds it) and to HostDeviceSearch over the same kind of client. DashyNMS/mobile
/// serves the same files to its Intelligence find_device through its own per-chat transports.
/// </summary>
/// <remarks>
/// Unlike <see cref="SharedSearchFixtureTests"/>, nothing here fakes Core: status codes, bodies,
/// retries, timeouts and an address with nothing listening reach each adapter through Core's
/// own parsing and retry policy. <c>scopes.json</c> holds group, location and status scopes,
/// ambiguous and missing scope names, and an invalid status. <c>transport.json</c> holds failed
/// device lists, including bodies that aren't LibreNMS JSON, a request that never gets an answer
/// and an address with nothing listening. Request counts include Core's retries.
/// </remarks>
public sealed class SharedSearchHttpFixtureTests
{
    private const string Token = "test-token";

    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceSearch");

    private static JsonElement Load(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, file)));
        return json.RootElement.Clone();
    }

    private static readonly JsonElement Scopes = Load("scopes.json");
    private static readonly JsonElement Transport = Load("transport.json");
    private static readonly JsonElement Basic = Load("cases.json");

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string[] Strings(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(r => r.GetString()!).ToArray();

    private static int[] Ints(JsonElement element, string name)
        => element.GetProperty(name).EnumerateArray().Select(i => i.GetInt32()).ToArray();

    public static TheoryData<int> ScopeCases()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Scopes.GetProperty("cases").GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    public static TheoryData<string> TransportFailures()
    {
        var data = new TheoryData<string>();
        foreach (var f in Transport.GetProperty("failures").EnumerateArray())
        {
            data.Add(f.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> BasicQueries()
    {
        var data = new TheoryData<string>();
        foreach (var c in Basic.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("query").GetString()!);
        }

        return data;
    }

    /// <summary>Every route in <c>scopes.json</c>, as LibreNMS would answer it.</summary>
    private static Dictionary<string, Answer> ScopeRoutes()
    {
        var routes = new Dictionary<string, Answer>(StringComparer.Ordinal);
        foreach (var route in Scopes.GetProperty("routes").EnumerateArray())
        {
            var body = Text(route, "file") is { } file
                ? File.ReadAllText(Path.Combine(FixtureDirectory, file))
                : route.GetProperty("body").GetString()!;
            routes[route.GetProperty("path").GetString()!] = new(route.GetProperty("status").GetInt32(), body);
        }

        return routes;
    }

    /// <summary>
    /// The MCP server's own services, connected to one address. Each adapter gets its own
    /// provider, transport and caches, so each server's request log is that adapter's alone.
    /// </summary>
    private static ServiceProvider Connected(Uri root)
    {
        var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();
        provider.GetRequiredService<ILibreNmsClient>().Connect(
            new LibreNmsConnection(root, Token, timeoutSeconds: Transport.GetProperty("timeoutSeconds").GetInt32()));
        return provider;
    }

    /// <summary>The find_device tool as the MCP SDK activates it from the server's services.</summary>
    private static DeviceTools Tools(IServiceProvider provider) => ActivatorUtilities.CreateInstance<DeviceTools>(provider);

    private static HostDeviceSearch Host(IServiceProvider provider)
        => HostDeviceSearch.Create(provider.GetRequiredService<ILibreNmsClient>(), TimeProvider.System, serverTimestampsAreUtc: false);

    [Theory]
    [MemberData(nameof(BasicQueries))]
    public async Task Find_device_and_the_host_search_rank_the_fixture_identically_over_HTTP(string query)
    {
        var expected = Basic.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("query").GetString() == query);
        var routes = ScopeRoutes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var search = Host(host);

        var text = await Tools(mcp).FindDevice(query, limit: DeviceSearch.MaxMatches, cancellationToken: CancellationToken.None);
        var outcome = await search.FindDevicesAsync(new(query, Limit: DeviceSearch.MaxMatches));

        var data = FakeLibreNms.Parse(text).GetProperty("data");
        Assert.Equal(Ints(expected, "ids"), data.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("device_id").GetInt32()));
        Assert.Equal(expected.GetProperty("total").GetInt32(), data.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(Ints(expected, "ids"), outcome.Result!.Matches.Select(m => m.Device.DeviceId));
        Assert.Equal(expected.GetProperty("total").GetInt32(), outcome.Result.Page.Total);
        Assert.Equal(Strings(Basic, "requests"), mcpServer.Requests);
        Assert.Equal(Strings(Basic, "requests"), hostServer.Requests);
    }

    [Theory]
    [MemberData(nameof(ScopeCases))]
    public async Task Find_device_and_the_host_search_apply_the_same_scope_boundaries(int index)
    {
        var c = Scopes.GetProperty("cases")[index];
        var (query, status, group, location) = (Text(c, "query"), Text(c, "status"), Text(c, "group"), Text(c, "location"));
        var routes = ScopeRoutes();
        using var mcpServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var hostServer = new FixtureServer(path => routes.GetValueOrDefault(path));
        using var mcp = Connected(mcpServer.Root);
        using var host = Connected(hostServer.Root);
        using var search = Host(host);

        var root = FakeLibreNms.Parse(await Tools(mcp).FindDevice(query, status, group, location,
            limit: DeviceSearch.MaxMatches, cancellationToken: CancellationToken.None));
        var outcome = await search.FindDevicesAsync(new(query, status, group, location, Limit: DeviceSearch.MaxMatches));

        if (Text(c, "code") is { } code)
        {
            Assert.Equal("error", root.GetProperty("status").GetString());
            Assert.False(root.TryGetProperty("data", out _));
            Assert.Null(outcome.Result);
            Assert.Equal(Enum.Parse<DeviceSearchFailure>(code), outcome.Error!.Code);
        }
        else
        {
            Assert.Equal("ok", root.GetProperty("status").GetString());
            var data = root.GetProperty("data");
            Assert.Equal(Ints(c, "ids"), data.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("device_id").GetInt32()));
            Assert.Equal(c.GetProperty("total").GetInt32(), data.GetProperty("page").GetProperty("total").GetInt32());
            Assert.Null(outcome.Error);
            Assert.Equal(Ints(c, "ids"), outcome.Result!.Matches.Select(m => m.Device.DeviceId));
            Assert.Equal(c.GetProperty("total").GetInt32(), outcome.Result.Page.Total);
        }

        Assert.Equal(Strings(c, "requests"), mcpServer.Requests);
        Assert.Equal(Strings(c, "requests"), hostServer.Requests);
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task Find_device_and_the_host_search_fail_alike_on_the_shared_transport_failures(string name)
    {
        var failure = Transport.GetProperty("failures").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
        var query = Transport.GetProperty("query").GetString()!;
        using var mcpServer = FailingServer(failure);
        using var hostServer = FailingServer(failure);
        using var mcp = Connected(mcpServer?.Root ?? NothingListening());
        using var host = Connected(hostServer?.Root ?? NothingListening());
        using var search = Host(host);

        // Side by side: a request that never gets an answer costs the full timeout on each.
        var mcpCall = Tools(mcp).FindDevice(query, cancellationToken: CancellationToken.None);
        var hostCall = search.FindDevicesAsync(new(query));
        await Task.WhenAll(mcpCall, hostCall);
        var text = await mcpCall;
        var outcome = await hostCall;

        // MCP: an error in its own words, with no device data and nothing secret-shaped left.
        var root = FakeLibreNms.Parse(text);
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.DoesNotContain("synthetic-secret", text, StringComparison.Ordinal);
        // Host: the shared category, no result.
        Assert.Null(outcome.Result);
        Assert.Equal(Enum.Parse<DeviceSearchFailure>(failure.GetProperty("code").GetString()!), outcome.Error!.Code);

        var requests = Enumerable.Repeat("devices", failure.GetProperty("requests").GetInt32());
        Assert.Equal(requests, mcpServer?.Requests ?? []);
        Assert.Equal(requests, hostServer?.Requests ?? []);
    }

    /// <summary>A server answering the device list as this failure describes; null when nothing should listen.</summary>
    private static FixtureServer? FailingServer(JsonElement failure)
    {
        switch (Text(failure, "behaviour"))
        {
            case "unreachable":
                return null;
            case "hang":
                return new FixtureServer(_ => Answer.Hang);
            default:
                var answer = new Answer(failure.GetProperty("status").GetInt32(),
                    Transport.GetProperty("bodies").GetProperty(failure.GetProperty("body").GetString()!).GetString()!);
                return new FixtureServer(_ => answer);
        }
    }

    /// <summary>A loopback address that was free a moment ago, so a connection is refused.</summary>
    private static Uri NothingListening()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    private sealed record Answer(int Status, string Body)
    {
        /// <summary>Accept the request and never answer it.</summary>
        public static readonly Answer Hang = new(0, "");
    }

    /// <summary>
    /// A loopback LibreNMS answering from the fixture, recording each request's path under
    /// <c>/api/v0/</c>. Requests without the connection's token are refused, as LibreNMS would.
    /// </summary>
    private sealed class FixtureServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly ConcurrentQueue<string> requests = new();
        private readonly Func<string, Answer?> answer;
        private readonly CancellationTokenSource stopping = new();

        public FixtureServer(Func<string, Answer?> answer)
        {
            this.answer = answer;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Root = new Uri($"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/");
                probe.Stop();
            }

            listener.Prefixes.Add(Root.ToString());
            listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public Uri Root { get; }

        public string[] Requests => requests.ToArray();

        private async Task ServeAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => RespondAsync(context));
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            const string api = "/api/v0/";
            var relative = path.StartsWith(api, StringComparison.Ordinal) ? path[api.Length..] : path;
            requests.Enqueue(relative);
            var reply = context.Request.Headers["X-Auth-Token"] == Token
                ? answer(relative) ?? new Answer(404, """{"status":"error","message":"No fixture for this path"}""")
                : new Answer(401, """{"status":"error","message":"Unauthenticated."}""");
            try
            {
                if (reply == Answer.Hang)
                {
                    await Task.Delay(Timeout.Infinite, stopping.Token);
                    return;
                }

                var body = Encoding.UTF8.GetBytes(reply.Body);
                context.Response.StatusCode = reply.Status;
                context.Response.ContentType = reply.Body.StartsWith('<') ? "text/html" : "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception)
            {
                // The client gave up (a timeout) or the server is stopping.
            }
        }

        public void Dispose()
        {
            stopping.Cancel();
            listener.Stop();
            listener.Close();
        }
    }
}
