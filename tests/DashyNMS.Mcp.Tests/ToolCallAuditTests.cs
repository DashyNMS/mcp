using System.Text.Json;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

/// <summary>The audit trail end to end (#18): the real server over stdio, and what it writes to stderr for each call.</summary>
public sealed class ToolCallAuditTests
{
    private const string GraylogPassword = "synthetic-graylog-password";

    [Fact]
    public async Task Every_call_leaves_exactly_one_entry()
    {
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, (McpOptions.ToolsDeny, "graph"));
        await server.InitializeAsync(cts.Token);
        await server.RequestAsync(2, "tools/list", "{}", cts.Token);

        var found = await server.RequestAsync(3, "tools/call", """{"name":"find_device","arguments":{"query":"core"}}""", cts.Token);
        await server.RequestAsync(4, "tools/call", """{"name":"device_health","arguments":{"device":"nowhere"}}""", cts.Token);
        await server.RequestAsync(5, "tools/call", """{"name":"graph","arguments":{"device":"core-sw1"}}""", cts.Token);
        await server.RequestAsync(6, "tools/call", """{"name":"device_health","arguments":{}}""", cts.Token);
        await server.RequestAsync(7, "tools/call", """{"name":"no_such_tool","arguments":{"password":"hunter2"}}""", cts.Token);

        var stderr = await server.StopAsync(cts.Token);
        var entries = Entries(stderr);
        Assert.Equal(
            [("find_device", "ok"), ("device_health", "error"), ("graph", "rejected"), ("device_health", "rejected"), ("no_such_tool", "rejected")],
            entries.Select(e => (e.GetProperty("tool").GetString()!, e.GetProperty("status").GetString()!)));

        var first = entries[0];
        Assert.Equal("tests", first.GetProperty("client_name").GetString());
        Assert.Equal("1", first.GetProperty("client_version").GetString());
        Assert.Equal("stdio", first.GetProperty("transport").GetString());
        Assert.Equal("""{"query":"core"}""", first.GetProperty("arguments").GetRawText());
        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(found.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!),
            first.GetProperty("result_bytes").GetInt32());
        Assert.True(first.GetProperty("duration_ms").GetInt64() >= 0);
        Assert.InRange(first.GetProperty("time").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));

        // A tool that's off says nothing about which arguments are its own, so none are recorded.
        Assert.False(entries[2].TryGetProperty("arguments", out _));
        Assert.Equal(1, entries[4].GetProperty("undeclared_arguments").GetInt32());
        Assert.DoesNotContain("hunter2", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Results_and_credentials_are_never_recorded()
    {
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms,
            (McpOptions.GraylogUrl, "https://graylog.example.net"), (McpOptions.GraylogUsername, "reader"), (McpOptions.GraylogPassword, GraylogPassword));
        await server.InitializeAsync(cts.Token);

        await server.CallToolAsync(2, "find_device", $$"""{"query":"{{FixtureServer.Token}}"}""", cts.Token);
        await server.CallToolAsync(3, "find_device", $$"""{"query":"is it {{GraylogPassword}}?"}""", cts.Token);
        await server.CallToolAsync(4, "find_device", """{"query":"password=hunter2"}""", cts.Token);
        await server.CallToolAsync(5, "find_device", """{"query":"core","secret_note":"synthetic-undeclared"}""", cts.Token);

        var stderr = await server.StopAsync(cts.Token);
        Assert.Equal(4, Entries(stderr).Count);
        foreach (var secret in new[] { FixtureServer.Token, GraylogPassword, "hunter2", "synthetic-undeclared" })
        {
            Assert.DoesNotContain(secret, stderr, StringComparison.Ordinal);
        }

        // In the last call's result, so only there if a result was recorded.
        Assert.DoesNotContain("core-sw1.example.net", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_cannot_write_lines_of_its_own_into_the_log()
    {
        const string forged = """\n{\"event\":\"tool_call\",\"token_label\":\"bob\"}\r\u2028{\"event\":\"tool_call\"}""";
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms);
        await server.RequestAsync(1, "initialize", $$$$"""{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"a{{{{forged}}}}","version":"{{{{FixtureServer.Token}}}}"}}""", cts.Token);

        // The SDK logs an unknown tool's name as it was sent.
        await server.RequestAsync(2, "tools/call", $$$"""{"name":"b{{{forged}}} {{{FixtureServer.Token}}}","arguments":{}}""", cts.Token);

        var stderr = await server.StopAsync(cts.Token);
        var entry = Assert.Single(stderr.Split(['\n', '\r', '\u2028']), line => line.TrimStart().StartsWith('{'));
        Assert.Equal("rejected", JsonDocument.Parse(entry).RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain(FixtureServer.Token, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rate_limited_call_is_recorded_as_one()
    {
        using var librenms = LibreNms();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms, (McpOptions.RateLimitPerMinuteKey, "1"));
        await server.InitializeAsync(cts.Token);

        await server.CallToolAsync(2, "find_device", """{"query":"core"}""", cts.Token);
        await server.CallToolAsync(3, "find_device", """{"query":"core"}""", cts.Token);

        var entries = Entries(await server.StopAsync(cts.Token));
        Assert.Equal(["ok", "rate_limited"], entries.Select(e => e.GetProperty("status").GetString()!));
    }

    [Fact]
    public async Task A_cancelled_call_is_recorded_as_one()
    {
        using var librenms = new FixtureServer(path => path switch
        {
            "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
            _ => Answer.Hang,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = Start(librenms);
        await server.InitializeAsync(cts.Token);

        await server.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find_device","arguments":{"query":"core"}}}""", cts.Token);
        while (!librenms.Requests.Contains("devices"))
        {
            await Task.Delay(50, cts.Token);
        }

        await server.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2,"reason":"test"}}""", cts.Token);
        while (!server.Stderr.Contains("\"tool_call\"", StringComparison.Ordinal))
        {
            await Task.Delay(50, cts.Token);
        }

        var entry = Assert.Single(Entries(await server.StopAsync(cts.Token)));
        Assert.Equal("cancelled", entry.GetProperty("status").GetString());
        Assert.Equal(0, entry.GetProperty("result_bytes").GetInt64());
    }

    private static List<JsonElement> Entries(string stderr) => stderr.Split('\n')
        .Where(line => line.StartsWith("""{"event":"tool_call",""", StringComparison.Ordinal))
        .Select(line => JsonDocument.Parse(line).RootElement)
        .ToList();

    private static FixtureServer LibreNms() => new(path => path switch
    {
        "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        "devices" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1.example.net","status":1}]}"""),
        _ => null,
    });

    private static StdioServer Start(FixtureServer librenms, params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
        };
        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        return StdioServer.Start(all);
    }
}
