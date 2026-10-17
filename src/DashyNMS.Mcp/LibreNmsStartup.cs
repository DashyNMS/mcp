using DesktopNMS.Core.Api;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Logging;

namespace DashyNMS.Mcp;

/// <summary>
/// Checks the LibreNMS address and token before the server takes any MCP
/// traffic, and connects the shared client when they work.
/// </summary>
/// <remarks>
/// Failing here, with one plain line on stderr, beats starting anyway: an MCP
/// client only shows "server disconnected", and every tool call would fail
/// with the same error the model would then try to explain.
/// </remarks>
public sealed class LibreNmsStartup(ILibreNmsClient client, ILogger<LibreNmsStartup> logger)
{
    /// <summary>Returns null once connected, or the reason it couldn't connect.</summary>
    public async Task<string?> ConnectAsync(LibreNmsConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var result = await client.TestAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Describe(connection, result);
        }

        client.Connect(connection, startOnBackup: result.UsedBackupAddress);
        logger.LogInformation(
            "Connected to LibreNMS {Version} at {Host}{Backup}",
            result.SystemInfo?.LocalVersion ?? "(unknown version)",
            connection.WebRoot.Host,
            result.UsedBackupAddress ? " through the backup address" : string.Empty);
        return null;
    }

    internal static string Describe(LibreNmsConnection connection, ConnectionTestResult result)
    {
        if (result.UntrustedCertificate is { } certificate)
        {
            return DescribeCertificate(certificate);
        }

        if (result.IsAuthenticationFailure)
        {
            return $"LibreNMS at {connection.WebRoot} refused the API token. Check {McpOptions.LibreNmsToken}.";
        }

        return $"Couldn't connect to LibreNMS at {connection.WebRoot}: {result.ErrorMessage}";
    }

    /// <summary>
    /// The server can't ask anyone to trust a certificate, so it prints what's
    /// needed to check it and the exact value to pin.
    /// </summary>
    private static string DescribeCertificate(CertificateDetails certificate)
    {
        var why = certificate.ReplacesTrustedCertificate
            ? $"The certificate {certificate.Host} presents isn't one listed in {McpOptions.LibreNmsTrustedCerts}. It may have been renewed, or the connection may be intercepted."
            : $"The certificate {certificate.Host} presents isn't trusted by this machine.";

        return $"{why} Subject: {certificate.Subject}; issuer: {certificate.Issuer}; valid {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}. " +
            $"If it is your LibreNMS server's own certificate, add this fingerprint to {McpOptions.LibreNmsTrustedCerts}: {certificate.Fingerprint}";
    }
}
