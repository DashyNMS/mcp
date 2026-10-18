namespace DashyNMS.Mcp.Tests;

public sealed class McpOptionsTests
{
    private const string Fingerprint = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89";

    private static McpOptions? Load(Dictionary<string, string?> values, out IReadOnlyList<string> errors)
        => McpOptions.TryLoad(key => values.GetValueOrDefault(key), out errors);

    private static Dictionary<string, string?> Minimal() => new()
    {
        [McpOptions.LibreNmsUrl] = "librenms.example.com",
        [McpOptions.LibreNmsToken] = "secret-token",
    };

    [Fact]
    public void Minimal_settings_use_defaults()
    {
        var options = Load(Minimal(), out var errors);

        Assert.Empty(errors);
        Assert.NotNull(options);
        Assert.Equal(new Uri("https://librenms.example.com/"), options.Connection.WebRoot);
        Assert.Equal("secret-token", options.Connection.ApiToken);
        Assert.Equal(McpOptions.DefaultTimeoutSeconds, options.Connection.TimeoutSeconds);
        Assert.Equal(McpOptions.DefaultMaxResultBytes, options.MaxResultBytes);
        Assert.Null(options.Connection.BackupWebRoot);
        Assert.Empty(options.Connection.TrustedCertificates);
    }

    [Fact]
    public void Pinning_is_always_on_so_rejections_carry_a_fingerprint()
    {
        var options = Load(Minimal(), out _);

        Assert.True(options!.Connection.AllowUntrustedCertificate);
    }

    [Fact]
    public void Api_url_is_accepted_as_the_site_root()
    {
        var values = Minimal();
        values[McpOptions.LibreNmsUrl] = "https://nms.example.com/librenms/api/v0";

        var options = Load(values, out _);

        Assert.Equal(new Uri("https://nms.example.com/librenms/"), options!.Connection.WebRoot);
    }

    [Fact]
    public void Optional_settings_are_read()
    {
        var values = Minimal();
        values[McpOptions.LibreNmsBackupUrl] = "https://192.0.2.20";
        values[McpOptions.LibreNmsTrustedCerts] = $" {Fingerprint} , {Fingerprint.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant()}";
        values[McpOptions.LibreNmsTimeout] = "45";
        values[McpOptions.MaxResultBytesKey] = "32768";

        var options = Load(values, out var errors);

        Assert.Empty(errors);
        Assert.Equal(new Uri("https://192.0.2.20/"), options!.Connection.BackupWebRoot);
        Assert.Equal(2, options.Connection.TrustedCertificates.Count);
        Assert.Equal(45, options.Connection.TimeoutSeconds);
        Assert.Equal(32768, options.MaxResultBytes);
    }

    [Fact]
    public void Blank_values_count_as_unset()
    {
        var values = Minimal();
        values[McpOptions.LibreNmsBackupUrl] = "  ";
        values[McpOptions.LibreNmsTimeout] = "";

        var options = Load(values, out var errors);

        Assert.Empty(errors);
        Assert.Null(options!.Connection.BackupWebRoot);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var values = new Dictionary<string, string?>
        {
            [McpOptions.LibreNmsTrustedCerts] = "not-a-fingerprint",
            [McpOptions.LibreNmsTimeout] = "soon",
            [McpOptions.MaxResultBytesKey] = "10",
        };

        var options = Load(values, out var errors);

        Assert.Null(options);
        Assert.Equal(5, errors.Count);
        Assert.Contains(errors, e => e.StartsWith(McpOptions.LibreNmsUrl, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith(McpOptions.LibreNmsToken, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith(McpOptions.LibreNmsTrustedCerts, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith(McpOptions.LibreNmsTimeout, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith(McpOptions.MaxResultBytesKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, DashyNMS.Agent.ConfigText.Full)]
    [InlineData("full", DashyNMS.Agent.ConfigText.Full)]
    [InlineData("Summary", DashyNMS.Agent.ConfigText.Summary)]
    [InlineData("off", DashyNMS.Agent.ConfigText.Off)]
    public void Config_text_setting(string? value, DashyNMS.Agent.ConfigText expected)
    {
        var values = Minimal();
        values[McpOptions.ConfigTextKey] = value;

        Assert.Equal(expected, Load(values, out _)!.ConfigText);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Graylog_match_any_address_setting(string? value, bool expected)
    {
        var values = Minimal();
        values[McpOptions.GraylogMatchAnyAddressKey] = value;

        Assert.Equal(expected, Load(values, out _)!.GraylogMatchAnyAddress);
    }

    [Fact]
    public void Unreadable_match_any_address_setting_is_an_error()
    {
        var values = Minimal();
        values[McpOptions.GraylogMatchAnyAddressKey] = "sometimes";

        Assert.Null(Load(values, out var errors));
        Assert.StartsWith(McpOptions.GraylogMatchAnyAddressKey, Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_config_text_setting_is_an_error()
    {
        var values = Minimal();
        values[McpOptions.ConfigTextKey] = "some";

        Assert.Null(Load(values, out var errors));
        Assert.StartsWith(McpOptions.ConfigTextKey, Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_url_scheme_is_rejected()
    {
        var values = Minimal();
        values[McpOptions.LibreNmsUrl] = "ftp://librenms.example.com";

        Assert.Null(Load(values, out var errors));
        Assert.Single(errors);
    }

    [Fact]
    public void Errors_never_echo_the_token()
    {
        var values = Minimal();
        values[McpOptions.LibreNmsUrl] = "ftp://bad";
        values[McpOptions.LibreNmsTimeout] = "x";

        Load(values, out var errors);

        Assert.DoesNotContain(errors, e => e.Contains("secret-token", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Fingerprint, true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("AB CD EF 01 23 45 67 89 AB CD EF 01 23 45 67 89 AB CD EF 01 23 45 67 89 AB CD EF 01 23 45 67 89", true)]
    [InlineData("AB:CD", false)]
    [InlineData("zz:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89", false)]
    public void Fingerprint_forms(string text, bool expected)
        => Assert.Equal(expected, McpOptions.IsSha256Fingerprint(text));
}
