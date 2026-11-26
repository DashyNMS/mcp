using System.Net;
using System.Text.Json;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// The HTTP transport end to end (#17): the real server process on a loopback
/// port, what it refuses, and that it's the same server as over stdio.
/// </summary>
public sealed class HttpTransportTests : IDisposable
{
    private const string Alice = HttpMcpServer.AliceToken;
    private const string Bob = HttpMcpServer.BobToken;

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"tests","version":"1"}}}""";

    private const string FindDevice = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find_device","arguments":{"query":"core"}}}""";

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(120));

    private readonly FixtureServer _librenms = new(path => path switch
    {
        "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        "devices" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.net","status":1}]}"""),
        _ => null,
    });

    public void Dispose()
    {
        _librenms.Dispose();
        _cts.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer " + Alice + "x")]
    [InlineData("Bearer alice:" + Alice)]
    [InlineData("Basic " + Alice)]
    [InlineData(Alice)]
    public async Task A_request_without_a_valid_token_is_refused_and_runs_nothing(string? authorization)
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token);
        var session = await server.InitializeAsync(Alice, "tests", _cts.Token);
        var asked = _librenms.Requests.Length;

        // With someone else's session too: it's no substitute for a token.
        foreach (var (method, body, inSession) in new (HttpMethod, string?, string?)[]
        {
            (HttpMethod.Post, Initialize, null),
            (HttpMethod.Post, FindDevice, null),
            (HttpMethod.Post, FindDevice, session),
            (HttpMethod.Get, null, session),
            (HttpMethod.Delete, null, session),
        })
        {
            using var refused = await server.SendAsync(method, "mcp", authorization, _cts.Token, body, inSession);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("Bearer", Assert.Single(refused.Headers.WwwAuthenticate).Scheme);
            Assert.Empty(await refused.Content.ReadAsStringAsync(_cts.Token));
        }

        Assert.Equal(asked, _librenms.Requests.Length);
        Assert.DoesNotContain("\"tool_call\"", server.Stderr, StringComparison.Ordinal);

        // The session the refused DELETE named is still there.
        var answer = await server.RequestAsync(Alice, session, 3, "tools/call", """{"name":"find_device","arguments":{"query":"core"}}""", _cts.Token);
        Assert.True(answer.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sse")]
    [InlineData("mcp/")]
    [InlineData("health/x")]
    [InlineData("nothing-here")]
    public async Task Every_path_but_the_probes_needs_a_token(string path)
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token);

        using var refused = await server.SendAsync(HttpMethod.Get, path, authorization: null, _cts.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Probes_need_no_token_and_say_nothing_else()
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token);

        foreach (var (path, word) in new[] { ("health", "ok"), ("ready", "ready") })
        {
            using var answer = await server.SendAsync(HttpMethod.Get, path, authorization: null, _cts.Token);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal(word, await answer.Content.ReadAsStringAsync(_cts.Token));
            Assert.False(answer.Headers.Contains("Server"));
        }

        // Asking again and again doesn't ask LibreNMS again and again.
        var asked = _librenms.Requests.Length;
        for (var i = 0; i < 20; i++)
        {
            using var answer = await server.SendAsync(HttpMethod.Get, "ready", authorization: null, _cts.Token);
        }

        Assert.InRange(_librenms.Requests.Length - asked, 0, 2);

        using var posted = await server.SendAsync(HttpMethod.Post, "health", authorization: null, _cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, posted.StatusCode);
    }

    [Fact]
    public async Task One_server_answers_several_clients_with_the_same_tools_as_stdio()
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token);
        var alice = await server.InitializeAsync(Alice, "alices-client", _cts.Token);
        var bob = await server.InitializeAsync(Bob, "bobs-client", _cts.Token);
        Assert.NotEqual(alice, bob);

        foreach (var (token, session) in new[] { (Alice, alice), (Bob, bob) })
        {
            var list = await server.RequestAsync(token, session, 2, "tools/list", "{}", _cts.Token);
            ListedTools.AssertUnchanged(list.GetProperty("result").GetProperty("tools"));
            var call = await server.RequestAsync(token, session, 3, "tools/call", """{"name":"find_device","arguments":{"query":"core"}}""", _cts.Token);
            var result = JsonDocument.Parse(call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
            Assert.Equal("core-sw1.example.net", result.GetProperty("data").GetProperty("devices")[0].GetProperty("hostname").GetString());
        }

        var entries = await server.AuditAsync(2, _cts.Token);
        Assert.Equal(
            [("alice", "alices-client"), ("bob", "bobs-client")],
            entries.Select(e => (e.GetProperty("token_label").GetString()!, e.GetProperty("client_name").GetString()!)));
        Assert.All(entries, e => Assert.Equal("http", e.GetProperty("transport").GetString()));
        foreach (var secret in new[] { Alice, Bob, FixtureServer.Token })
        {
            Assert.DoesNotContain(secret, server.Stderr, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-06-18")]
    public async Task The_SDKs_client_works_on_the_newest_protocol_and_an_older_one(string? protocolVersion)
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token);

        await using (var client = await server.ConnectAsync(Alice, "sdk-client", protocolVersion, _cts.Token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: _cts.Token);
            Assert.Equal(ToolCatalogue.Names.Order(), tools.Select(t => t.Name).Order());
            var result = await client.CallToolAsync("find_device", new Dictionary<string, object?> { ["query"] = "core" }, cancellationToken: _cts.Token);
            Assert.NotEqual(true, result.IsError);
        }

        var entry = Assert.Single(await server.AuditAsync(1, _cts.Token));
        Assert.Equal("alice", entry.GetProperty("token_label").GetString());
        Assert.Equal("sdk-client", entry.GetProperty("client_name").GetString());
        Assert.Equal("ok", entry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_session_belongs_to_the_token_that_started_it()
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token);
        var session = await server.InitializeAsync(Alice, "tests", _cts.Token);
        var asked = _librenms.Requests.Length;

        using var refused = await server.PostAsync(Bob, FindDevice, _cts.Token, session);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(asked, _librenms.Requests.Length);
    }

    [Fact]
    public async Task A_browser_is_refused_unless_its_origin_is_allowed()
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token, (McpOptions.HttpAllowedOriginsKey, "https://tools.example.net"));

        using var refused = await server.PostAsync(Alice, Initialize, _cts.Token, origin: "https://evil.example.net");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var opaque = await server.PostAsync(Alice, Initialize, _cts.Token, origin: "null");
        Assert.Equal(HttpStatusCode.Forbidden, opaque.StatusCode);
        using var allowed = await server.PostAsync(Alice, Initialize, _cts.Token, origin: "https://tools.example.net");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // An origin alone is no way in.
        using var tokenless = await server.SendAsync(HttpMethod.Post, "mcp", authorization: null, _cts.Token, Initialize, origin: "https://tools.example.net");
        Assert.Equal(HttpStatusCode.Unauthorized, tokenless.StatusCode);
    }

    [Fact]
    public async Task A_tool_that_is_off_cannot_be_called_over_http_either()
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token, (McpOptions.ToolsDeny, "device_health"));
        var session = await server.InitializeAsync(Alice, "tests", _cts.Token);
        var asked = _librenms.Requests.Length;

        var answer = await server.RequestAsync(Alice, session, 2, "tools/call", """{"name":"device_health","arguments":{"device":"core-sw1"}}""", _cts.Token);

        Assert.Contains("Unknown tool", answer.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(asked, _librenms.Requests.Length);
        Assert.Equal("rejected", Assert.Single(await server.AuditAsync(1, _cts.Token)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_rate_limit_is_each_tokens_own_whatever_session_it_uses()
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token, (McpOptions.RateLimitPerMinuteKey, "1"));
        const string call = """{"name":"find_device","arguments":{"query":"core"}}""";
        async Task<bool> IsLimitedAsync(string token)
        {
            // A new session each time: starting another one isn't a way round it.
            var session = await server.InitializeAsync(token, "tests", _cts.Token);
            var answer = await server.RequestAsync(token, session, 2, "tools/call", call, _cts.Token);
            return answer.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean();
        }

        Assert.False(await IsLimitedAsync(Alice));
        Assert.True(await IsLimitedAsync(Alice));
        Assert.False(await IsLimitedAsync(Bob));

        var entries = await server.AuditAsync(3, _cts.Token);
        Assert.Equal(
            [("alice", "ok"), ("alice", "rate_limited"), ("bob", "ok")],
            entries.Select(e => (e.GetProperty("token_label").GetString()!, e.GetProperty("status").GetString()!)));
    }

    [Fact]
    public async Task A_body_too_big_for_any_tool_call_is_refused()
    {
        using var server = await HttpMcpServer.StartAsync(_librenms, _cts.Token);
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "find_device", arguments = new { query = new string('x', 300 * 1024) } },
        });

        HttpStatusCode? status = null;
        try
        {
            using var refused = await server.PostAsync(Alice, body, _cts.Token);
            status = refused.StatusCode;
        }
        catch (HttpRequestException)
        {
            // Hung up on part way through sending it, which is a refusal too.
        }

        Assert.True(status is null or HttpStatusCode.RequestEntityTooLarge, status?.ToString());
    }

    [Theory]
    [InlineData(null, "MCP_HTTP_TOKENS is not set")]
    [InlineData("alice:synthetic-short", "MCP_HTTP_TOKENS: entry 1 isn't a bearer token")]
    public async Task Without_a_good_token_it_does_not_start(string? tokens, string problem)
    {
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = _librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.TransportKey] = "http",
            [McpOptions.HttpTokensKey] = tokens,
        });

        var (exitCode, stdout) = await server.WaitForExitAsync(_cts.Token);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(problem, server.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-short", server.Stderr, StringComparison.Ordinal);
        Assert.Empty(_librenms.Requests);
    }

    [Fact]
    public async Task A_port_already_in_use_is_one_plain_line()
    {
        using var first = await HttpMcpServer.StartAsync(_librenms, _cts.Token);
        using var second = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = _librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.TransportKey] = "http",
            [McpOptions.HttpPortKey] = first.Root.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [McpOptions.HttpTokensKey] = Alice,
        });

        var (exitCode, stdout) = await second.WaitForExitAsync(_cts.Token);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Couldn't listen on", second.Stderr, StringComparison.Ordinal);
    }
}
