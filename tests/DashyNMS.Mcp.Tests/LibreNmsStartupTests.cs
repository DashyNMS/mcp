using System.Net.Security;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace DashyNMS.Mcp.Tests;

public sealed class LibreNmsStartupTests
{
    private static readonly LibreNmsConnection Connection = new(
        new Uri("https://librenms.example.com/"), "secret-token", allowUntrustedCertificate: true);

    private readonly ILibreNmsClient _client = Substitute.For<ILibreNmsClient>();

    private LibreNmsStartup Startup() => new(_client, NullLogger<LibreNmsStartup>.Instance);

    [Fact]
    public async Task Success_connects_the_shared_client()
    {
        _client.TestAsync(Connection, Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success(new SystemInfo { LocalVersion = "26.9.0" }));

        var failure = await Startup().ConnectAsync(Connection, CancellationToken.None);

        Assert.Null(failure);
        _client.Received(1).Connect(Connection, false);
    }

    [Fact]
    public async Task Success_on_the_backup_address_starts_there()
    {
        _client.TestAsync(Connection, Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Success(new SystemInfo(), usedBackupAddress: true));

        await Startup().ConnectAsync(Connection, CancellationToken.None);

        _client.Received(1).Connect(Connection, true);
    }

    [Fact]
    public async Task Failure_does_not_connect()
    {
        _client.TestAsync(Connection, Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.Failure("The server did not answer."));

        var failure = await Startup().ConnectAsync(Connection, CancellationToken.None);

        Assert.Equal("Couldn't connect to LibreNMS at https://librenms.example.com/: The server did not answer.", failure);
        _client.DidNotReceive().Connect(Arg.Any<LibreNmsConnection>(), Arg.Any<bool>());
    }

    [Fact]
    public void Refused_token_names_the_variable_not_the_token()
    {
        var message = LibreNmsStartup.Describe(Connection, ConnectionTestResult.Failure("401", isAuthenticationFailure: true));

        Assert.Contains(McpOptions.LibreNmsToken, message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "isn't trusted by this machine")]
    [InlineData(true, "may have been renewed")]
    public void Untrusted_certificate_prints_the_fingerprint_to_pin(bool replacesTrusted, string expected)
    {
        var certificate = new CertificateDetails(
            "librenms.example.com",
            "AB:CD:EF",
            "CN=librenms.example.com",
            "CN=Internal CA",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SslPolicyErrors.RemoteCertificateChainErrors,
            replacesTrusted);

        var message = LibreNmsStartup.Describe(Connection, ConnectionTestResult.Failure("untrusted", untrustedCertificate: certificate));

        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.EndsWith($"{McpOptions.LibreNmsTrustedCerts}: AB:CD:EF", message, StringComparison.Ordinal);
        Assert.Contains("CN=Internal CA", message, StringComparison.Ordinal);
    }
}
