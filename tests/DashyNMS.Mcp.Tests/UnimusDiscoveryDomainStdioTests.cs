using System.Text.Json;
using DashyNMS.Agent.Tests;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// <c>UNIMUS_MY_DOMAIN</c> end to end: the real server over stdio, a stand-in
/// LibreNMS that knows the switch only by its short name, and a stand-in Unimus
/// that knows it only by its full name. Every config path is checked with the
/// setting on and off, in each <c>MCP_CONFIG_TEXT</c> mode, so the setting is
/// shown to change matching and nothing else.
/// </summary>
public sealed class UnimusDiscoveryDomainStdioTests
{
    private const string UnimusToken = "synthetic-fixture-token";

    // Around backup 901 in the shared fixture, which starts at 2026-10-17 08:00 UTC
    // and follows backup 900, so each window holds exactly one config change.
    private const string Window = """ "since":"2026-10-17T00:00:00Z","until":"2026-10-18T00:00:00Z" """;

    private static readonly string Routes = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConfigLookups", "routes.json");

    [Theory]
    [InlineData(null, "full")]
    [InlineData(null, "summary")]
    [InlineData(null, "off")]
    [InlineData("example.net", "full")]
    [InlineData("example.net", "summary")]
    [InlineData("example.net", "off")]
    public async Task Discovery_domain_matches_a_short_LibreNMS_hostname_across_config_tools(string? domain, string configText)
    {
        using var librenms = new FixtureServer(LibreNms);
        using var unimus = StandInUnimus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.UnimusUrl] = unimus.Root.ToString(),
            [McpOptions.UnimusToken] = UnimusToken,
            [McpOptions.UnimusMyDomainKey] = domain,
            [McpOptions.ConfigTextKey] = configText,
        });
        await server.InitializeAsync(cts.Token);

        var backup = await server.CallToolAsync(10, "config_backup", """{"device":"core-sw1"}""", cts.Token);
        var diff = await server.CallToolAsync(11, "config_diff", """{"device":"core-sw1"}""", cts.Token);
        var changes = await server.CallToolAsync(12, "recent_config_changes", $$"""{"location":"Lab",{{Window}}}""", cts.Token);
        var changed = await server.CallToolAsync(13, "what_changed", $$"""{"device":"core-sw1",{{Window}}}""", cts.Token);

        if (configText == "off")
        {
            // Off refuses before either service is asked, so the domain can't matter.
            foreach (var refused in new[] { backup, diff, changes })
            {
                Assert.Equal("error", refused.GetProperty("status").GetString());
                Assert.Contains("MCP_CONFIG_TEXT=off", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
            }

            Assert.Equal("ok", changed.GetProperty("status").GetString());
            Assert.True(ConfigPart(changed).TryGetProperty("turned_off", out _), changed.ToString());
            Assert.False(ConfigPart(changed).TryGetProperty("device_found", out _), changed.ToString());
            Assert.Empty(unimus.HttpRequests);
        }
        else if (domain is null)
        {
            // Today's behaviour, unchanged: Unimus is asked, but the short name doesn't match the full one.
            foreach (var unmatched in new[] { backup, diff })
            {
                Assert.Equal("error", unmatched.GetProperty("status").GetString());
                Assert.Contains("Unimus doesn't back up this device", unmatched.GetProperty("message").GetString(), StringComparison.Ordinal);
            }

            Assert.Contains("/api/v2/devices", unimus.HttpRequests.Select(r => r.Split('?')[0]));
            Assert.Equal("ok", changes.GetProperty("status").GetString());
            Assert.Equal(0, changes.GetProperty("data").GetProperty("changes").GetArrayLength());
            Assert.Equal("ok", changed.GetProperty("status").GetString());
            Assert.False(ConfigPart(changed).GetProperty("device_found").GetBoolean(), changed.ToString());
            Assert.DoesNotContain(unimus.HttpRequests, r => r.StartsWith("/api/v2/devices/40/backups", StringComparison.Ordinal));
        }
        else
        {
            var full = configText == "full";
            Assert.Equal("ok", backup.GetProperty("status").GetString());
            Assert.Equal(901, backup.GetProperty("data").GetProperty("backup").GetProperty("id").GetInt32());
            Assert.Equal(full, backup.GetProperty("data").TryGetProperty("config", out var config));
            if (full) Assert.Contains("vlan 120", config.GetString(), StringComparison.Ordinal);

            Assert.Equal("ok", diff.GetProperty("status").GetString());
            Assert.Equal(900, diff.GetProperty("data").GetProperty("from").GetProperty("id").GetInt32());
            Assert.Equal(901, diff.GetProperty("data").GetProperty("to").GetProperty("id").GetInt32());
            Assert.Equal(full, diff.GetProperty("data").TryGetProperty("config_diff", out _));

            Assert.Equal("ok", changes.GetProperty("status").GetString());
            var change = Assert.Single(changes.GetProperty("data").GetProperty("changes").EnumerateArray());
            Assert.Equal(1, change.GetProperty("device").GetProperty("device_id").GetInt32());
            Assert.Equal(901, change.GetProperty("newest_backup_id").GetInt32());

            Assert.Equal("ok", changed.GetProperty("status").GetString());
            Assert.True(ConfigPart(changed).GetProperty("device_found").GetBoolean(), changed.ToString());
            Assert.True(ConfigPart(changed).GetProperty("config_changed").GetBoolean(), changed.ToString());
            Assert.Equal(full, ConfigPart(changed).TryGetProperty("config_diff", out _));

            Assert.Contains(unimus.HttpRequests, r => r.StartsWith("/api/v2/devices/40/backups", StringComparison.Ordinal));
        }

        // Redaction and the summary policy hold whichever way the device matched.
        var everything = string.Join('\n', new[] { backup, diff, changes, changed }.Select(r => r.GetRawText()));
        Assert.DoesNotContain("SyntheticCommunity", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("SyntheticOld", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("SyntheticNew", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-key-material", everything, StringComparison.Ordinal);
        if (configText != "full") Assert.DoesNotContain("vlan 120", everything, StringComparison.Ordinal);

        var stderr = await server.StopAsync(cts.Token);
        Assert.DoesNotContain(FixtureServer.Token, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(UnimusToken, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_discovery_domain_stops_startup_without_echoing_the_value()
    {
        const string value = "synthetic-secret-value=abc";
        using var librenms = new FixtureServer(LibreNms);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var server = StdioServer.Start(new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = librenms.Root.ToString(),
            [McpOptions.LibreNmsToken] = FixtureServer.Token,
            [McpOptions.UnimusMyDomainKey] = value,
        });

        var (exitCode, stdout) = await server.WaitForExitAsync(cts.Token);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(McpOptions.UnimusMyDomainKey + " isn't a domain name.", server.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-value", server.Stderr, StringComparison.Ordinal);
        Assert.Empty(librenms.Requests);
    }

    private static JsonElement ConfigPart(JsonElement whatChanged) => whatChanged.GetProperty("data").GetProperty("unimus");

    /// <summary>
    /// LibreNMS's view: the switch by its short hostname only, as it is when
    /// LibreNMS's discovery adds devices under a <c>mydomain</c> suffix. Event
    /// sources <c>what_changed</c> also reads are left unanswered; the tool
    /// reports them as unavailable and carries on, which is all it needs here.
    /// </summary>
    private static Answer? LibreNms(string path) => path switch
    {
        "system" => new(200, """{"status":"ok","system":[{"local_ver":"26.10.0"}]}"""),
        "devices" => new(200, """{"status":"ok","devices":[{"device_id":1,"hostname":"core-sw1","ip":"192.0.2.1","location":"Lab","status":1,"disabled":0}]}"""),
        _ => null,
    };

    /// <summary>
    /// Unimus's view, from the shared config fixture, but with the switch known
    /// only by its full name, so only the discovery-domain candidate finds it.
    /// </summary>
    private static FixtureServer StandInUnimus()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Routes));
        var routes = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => new Answer(200, p.Value.GetRawText()));
        routes["/api/v2/devices"] = new(200, """{"data":[{"id":40,"address":"core-sw1.example.net","description":"Backed-up switch"}],"paginator":{"totalCount":1,"totalPages":1}}""");
        return new(_ => null, requestAnswer: request => routes.GetValueOrDefault(request.Url!.AbsolutePath),
            authenticated: request => request.Headers["Authorization"] == "Bearer " + UnimusToken);
    }
}
