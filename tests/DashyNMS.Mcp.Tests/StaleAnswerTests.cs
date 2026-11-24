using System.Text.Json;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

/// <summary>The real server while its LibreNMS comes and goes (#39).</summary>
public sealed class StaleAnswerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dashynms-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(120));
    private readonly FixtureServer _librenms;

    private volatile string _state = "up";
    private volatile string _sysName = "core-sw1";

    public StaleAnswerTests()
    {
        // Any token: whether it's refused is the state's to say.
        _librenms = new FixtureServer(Answer, authenticated: _ => true);
    }

    public void Dispose()
    {
        _librenms.Dispose();
        _cts.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Answer? Answer(string path)
    {
        switch (_state)
        {
            case "down":
                return new(500, """{"status":"error","message":"Synthetic outage"}""");
            case "token refused":
                return new(401, """{"status":"error","message":"Unauthenticated."}""");
            case "forbidden" when path != "devices":
                return new(403, """{"status":"error","message":"Forbidden"}""");
        }

        return path switch
        {
            "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
            "devices" or "devices/1" => new(200, JsonSerializer.Serialize(new
            {
                status = "ok",
                devices = new[] { new { device_id = 1, hostname = "core-sw1.example.net", sysName = _sysName, status = 1, disabled = 0, ignore = 0 } },
            })),
            "alerts" or "faults" => new(200, """{"status":"ok","alerts":[],"faults":[]}"""),
            "rules" => new(200, """{"status":"ok","rules":[]}"""),
            "ports" or "devices/1/ports" => new(200, """{"status":"ok","ports":[]}"""),
            "devices/1/maintenance" => new(200, """{"status":"ok","maintenance":[],"is_under_maintenance":false}"""),
            _ => new(200, """{"status":"ok","graphs":[],"logs":[],"sensors":[],"links":[]}"""),
        };
    }

    private StdioServer Start(bool onDisk = false, string? on = null, string token = FixtureServer.Token) => StdioServer.Start(new Dictionary<string, string?>
    {
        [McpOptions.LibreNmsUrl] = _librenms.Root.ToString(),
        [McpOptions.LibreNmsToken] = token,
        [McpOptions.LibreNmsTimeout] = "5",
        [McpOptions.LastKnownGoodDirectoryKey] = onDisk ? _directory : null,
        [McpOptions.LastKnownGoodKey] = on,
    });

    private Task<JsonElement> Overview(StdioServer server, int id) => server.CallToolAsync(id, "network_overview", "{}", _cts.Token);

    [Fact]
    public async Task With_LibreNMS_unreachable_a_question_asked_before_is_answered_and_says_it_is_stale()
    {
        using var server = Start();
        await server.InitializeAsync(_cts.Token);
        var schema = (await server.RequestAsync(2, "tools/list", "{}", _cts.Token)).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "network_overview").GetProperty("outputSchema");
        var fresh = await Overview(server, 3);
        Assert.Equal("ok", fresh.GetProperty("status").GetString());
        Assert.False(fresh.TryGetProperty("stale", out _));

        _state = "down";
        var response = (await server.RequestAsync(4, "tools/call", """{"name":"network_overview","arguments":{}}""", _cts.Token)).GetProperty("result");
        var stale = response.GetProperty("structuredContent");

        Assert.Equal("ok", stale.GetProperty("status").GetString());
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.InRange(DateTimeOffset.Parse(stale.GetProperty("as_of").GetString()!, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow);
        Assert.NotEmpty(stale.GetProperty("stale_reason").GetString()!);
        Assert.StartsWith("LibreNMS can't be reached, so this is the last known answer", stale.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(JsonElement.DeepEquals(fresh.GetProperty("data"), stale.GetProperty("data")));
        Assert.Empty(JsonSchemaCheck.Problems(schema, stale));

        _state = "up";
        Assert.False((await Overview(server, 5)).TryGetProperty("stale", out _));

        var audit = await AuditAsync(server, 3);
        Assert.Equal(["ok", "stale", "ok"], audit);
    }

    [Fact]
    public async Task A_question_not_asked_before_and_one_never_kept_say_what_is_wrong()
    {
        using var server = Start();
        await server.InitializeAsync(_cts.Token);
        await Overview(server, 2);

        _state = "down";
        var events = await server.CallToolAsync(3, "network_events", "{}", _cts.Token);
        var logs = await server.CallToolAsync(4, "search_logs", """{"text":"x"}""", _cts.Token);

        Assert.Equal("error", events.GetProperty("status").GetString());
        Assert.Contains("LibreNMS", events.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("error", logs.GetProperty("status").GetString());
        Assert.False(events.TryGetProperty("stale", out _));
        Assert.False(logs.TryGetProperty("stale", out _));
    }

    [Fact]
    public async Task Turned_off_it_gives_what_it_can_now()
    {
        using var server = Start(on: "off");
        await server.InitializeAsync(_cts.Token);
        await Overview(server, 2);

        _state = "down";

        Assert.False((await Overview(server, 3)).TryGetProperty("stale", out _));
    }

    [Fact]
    public async Task A_refused_token_forgets_everything()
    {
        using var server = Start();
        await server.InitializeAsync(_cts.Token);
        await Overview(server, 2);

        _state = "token refused";
        Assert.False((await Overview(server, 3)).TryGetProperty("stale", out _));

        // Out of reach after that, there's nothing left to give.
        _state = "down";
        Assert.False((await Overview(server, 4)).TryGetProperty("stale", out _));
    }

    [Fact]
    public async Task What_the_account_may_no_longer_see_is_not_given_from_before()
    {
        using var server = Start();
        await server.InitializeAsync(_cts.Token);
        Assert.Equal("ok", (await server.CallToolAsync(2, "network_events", "{}", _cts.Token)).GetProperty("status").GetString());

        _state = "forbidden";
        Assert.Equal("error", (await server.CallToolAsync(3, "network_events", "{}", _cts.Token)).GetProperty("status").GetString());

        _state = "down";
        Assert.False((await server.CallToolAsync(4, "network_events", "{}", _cts.Token)).TryGetProperty("stale", out _));
    }

    [Fact]
    public async Task An_answer_anything_was_redacted_from_is_not_kept()
    {
        _sysName = "password=synthetic-secret";
        using (var server = Start(onDisk: true))
        {
            await server.InitializeAsync(_cts.Token);
            var found = await server.CallToolAsync(2, "find_device", """{"query":"core","verbose":true}""", _cts.Token);
            Assert.Contains("‹redacted›", found.ToString(), StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task With_a_folder_a_restart_while_LibreNMS_is_down_still_answers()
    {
        using (var server = Start(onDisk: true))
        {
            await server.InitializeAsync(_cts.Token);
            await server.CallToolAsync(2, "find_device", """{"query":"core"}""", _cts.Token);
        }

        _state = "down";
        using (var server = Start(onDisk: true))
        {
            await server.InitializeAsync(_cts.Token);
            var stale = await server.CallToolAsync(2, "find_device", """{"query":"core"}""", _cts.Token);
            Assert.True(stale.GetProperty("stale").GetBoolean());
            Assert.Equal("core-sw1.example.net", stale.GetProperty("data").GetProperty("devices")[0].GetProperty("hostname").GetString());

            // And once it's back, what it says now.
            _state = "up";
            Assert.False((await server.CallToolAsync(3, "find_device", """{"query":"core"}""", _cts.Token)).TryGetProperty("stale", out _));
        }
    }

    [Theory]
    [InlineData("token refused", false)]
    [InlineData("down", true)]
    public async Task A_restart_with_a_refused_token_or_another_account_stops_as_before(string state, bool anotherAccount)
    {
        using (var server = Start(onDisk: true))
        {
            await server.InitializeAsync(_cts.Token);
            await server.CallToolAsync(2, "find_device", """{"query":"core"}""", _cts.Token);
        }

        _state = state;
        using var again = Start(onDisk: true, token: anotherAccount ? "another-token" : FixtureServer.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => again.InitializeAsync(_cts.Token));
    }

    private static async Task<IReadOnlyList<string>> AuditAsync(StdioServer server, int count)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var entries = server.Stderr.Split('\n')
                .Where(line => line.StartsWith("""{"event":"tool_call",""", StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("status").GetString()!)
                .ToList();
            if (entries.Count >= count)
            {
                return entries;
            }

            await Task.Delay(50, patience.Token);
        }
    }
}
