namespace DashyNMS.Mcp.Tests;

public sealed class HttpTokensTests
{
    private const string A = "synthetic-alice-0123456789abcdef0123456789";
    private const string B = "synthetic-bob+fedcba9876543210/fedcba98765432==";

    private static HttpTokens? Parse(string text, out List<string> problems) => HttpTokens.Parse(text, McpOptions.HttpTokensKey, problems = []);

    [Fact]
    public void Each_token_is_known_by_its_label()
    {
        var tokens = Parse($"alice:{A}, bob:{B}", out var problems);

        Assert.Empty(problems);
        Assert.Equal("alice", tokens!.LabelFor(A));
        Assert.Equal("bob", tokens.LabelFor(B));
        Assert.Equal(["alice", "bob"], tokens.Labels);
    }

    [Fact]
    public void A_token_without_a_label_is_numbered()
    {
        var tokens = Parse($"alice:{A}\n{B}", out var problems);

        Assert.Empty(problems);
        Assert.Equal("token-2", tokens!.LabelFor(B));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alice")]
    [InlineData("synthetic-alice-0123456789abcdef012345678")]
    [InlineData("synthetic-alice-0123456789abcdef01234567890")]
    [InlineData("SYNTHETIC-ALICE-0123456789ABCDEF0123456789")]
    public void Anything_else_is_nobodys(string? token)
    {
        Assert.Null(Parse($"alice:{A}", out _)!.LabelFor(token));
    }

    [Theory]
    [InlineData(",", "has no tokens in it")]
    [InlineData("alice:tooshort-secret", "entry 1 isn't a bearer token of at least 32")]
    [InlineData("alice:" + A + "!secret", "entry 1 isn't a bearer token")]
    [InlineData("al ice:" + A, "entry 1 isn't a bearer token")]
    [InlineData("a/b:" + A, "entry 1 has a label that isn't")]
    [InlineData(":" + A, "entry 1 has a label that isn't")]
    [InlineData("alice:" + A + ",Alice:" + B, "entry 2 has the same label as another")]
    [InlineData("alice:" + A + ",bob:" + A, "entry 2 has the same token as another")]
    [InlineData("token-2:" + A + "," + B, "entry 2 has the same label as another")]
    public void A_bad_list_is_refused_without_repeating_it(string text, string problem)
    {
        Assert.Null(Parse(text, out var problems));

        Assert.Contains(problems, p => p.Contains(problem, StringComparison.Ordinal));
        Assert.All(problems, p =>
        {
            Assert.DoesNotContain(A, p, StringComparison.Ordinal);
            Assert.DoesNotContain(B, p, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", p, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Http_is_off_until_it_is_asked_for()
    {
        var options = Load();

        Assert.Equal(McpTransport.Stdio, options!.Transport);
        Assert.Null(options.HttpTokens);
        Assert.DoesNotContain(options.Warnings, w => w.Contains("HTTP", StringComparison.Ordinal));
    }

    [Fact]
    public void Http_listens_on_this_machine_only_until_told_otherwise()
    {
        var options = Load((McpOptions.TransportKey, "HTTP"), (McpOptions.HttpTokensKey, A));

        Assert.Equal(McpTransport.Http, options!.Transport);
        Assert.Equal(System.Net.IPAddress.Loopback, options.HttpAddress);
        Assert.Equal(8080, options.HttpPort);
        Assert.Empty(options.HttpAllowedOrigins);
        Assert.Empty(options.Warnings);
        Assert.Contains(A, options.Secrets);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Listening_further_is_warned_about(string address)
    {
        var options = Load((McpOptions.TransportKey, "http"), (McpOptions.HttpTokensKey, A), (McpOptions.HttpAddressKey, address));

        Assert.Contains(options!.Warnings, w => w.Contains("plain HTTP", StringComparison.Ordinal));
    }

    [Fact]
    public void Http_settings_without_http_are_warned_about()
    {
        var options = Load((McpOptions.HttpTokensKey, A));

        Assert.Contains("do nothing", Assert.Single(options!.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(McpOptions.TransportKey, "sse", "should be stdio or http")]
    [InlineData(McpOptions.HttpPortKey, "0", "from 1 to 65535")]
    [InlineData(McpOptions.HttpPortKey, "65536", "from 1 to 65535")]
    [InlineData(McpOptions.HttpAddressKey, "mcp.example.net", "isn't an IP address")]
    [InlineData(McpOptions.HttpAllowedOriginsKey, "tools.example.net", "isn't an origin")]
    [InlineData(McpOptions.HttpAllowedOriginsKey, "https://tools.example.net/page", "isn't an origin")]
    [InlineData(McpOptions.HttpAllowedOriginsKey, "*", "isn't an origin")]
    public void A_setting_that_makes_no_sense_is_refused(string key, string value, string problem)
    {
        var values = Values((McpOptions.TransportKey, "http"), (McpOptions.HttpTokensKey, A), (key, value));

        Assert.Null(McpOptions.TryLoad(values.GetValueOrDefault, out var errors));
        Assert.Contains(errors, e => e.Contains(problem, StringComparison.Ordinal));
    }

    private static McpOptions? Load(params (string Key, string Value)[] settings)
    {
        var options = McpOptions.TryLoad(Values(settings).GetValueOrDefault, out var errors);
        Assert.Empty(errors);
        return options;
    }

    private static Dictionary<string, string?> Values(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsUrl] = "https://librenms.example.net",
            [McpOptions.LibreNmsToken] = "synthetic-token",
        };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        return values;
    }
}
