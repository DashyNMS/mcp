using System.Globalization;
using DesktopNMS.Core.Api;

namespace DashyNMS.Mcp;

/// <summary>
/// The server's settings, read from environment variables (or anything else
/// the host's configuration carries) and checked before anything connects.
/// </summary>
/// <remarks>
/// Environment variables only, on purpose: the server runs under an MCP
/// client or in a container, with no person to ask and nowhere it should
/// write. Secrets are kept in <see cref="Connection"/> and never echoed back
/// in an error.
/// </remarks>
public sealed class McpOptions
{
    public const string LibreNmsUrl = "LIBRENMS_URL";
    public const string LibreNmsToken = "LIBRENMS_TOKEN";
    public const string LibreNmsBackupUrl = "LIBRENMS_BACKUP_URL";
    public const string LibreNmsTrustedCerts = "LIBRENMS_TRUSTED_CERTS";
    public const string LibreNmsTimeout = "LIBRENMS_TIMEOUT";
    public const string MaxResultBytesKey = "MCP_MAX_RESULT_BYTES";

    public const int DefaultTimeoutSeconds = 30;
    public const int DefaultMaxResultBytes = DashyNMS.Agent.AgentOptions.DefaultMaxResultBytes;

    /// <summary>Below this a single device summary wouldn't fit, so a smaller cap is a mistake rather than a choice.</summary>
    public const int MinimumMaxResultBytes = 1024;

    private McpOptions(LibreNmsConnection connection, int maxResultBytes)
    {
        Connection = connection;
        MaxResultBytes = maxResultBytes;
    }

    /// <summary>
    /// The LibreNMS connection. Certificate pinning is always on (Core's
    /// <c>allowUntrustedCertificate</c>): a certificate that passes the normal
    /// checks is accepted as usual, one that doesn't is accepted only if its
    /// fingerprint is in <see cref="LibreNmsTrustedCerts"/>, and any other is
    /// reported with its fingerprint so startup can say what to add (#189 in
    /// the desktop repo). There is deliberately no accept-everything switch.
    /// </summary>
    public LibreNmsConnection Connection { get; }

    /// <summary>The most JSON one tool call may return, in bytes.</summary>
    public int MaxResultBytes { get; }

    /// <summary>
    /// Reads and checks every setting. Returns null and lists every problem
    /// in <paramref name="errors"/>, one line each, rather than stopping at
    /// the first, so a misconfigured server is fixed in one go.
    /// </summary>
    public static McpOptions? TryLoad(Func<string, string?> lookup, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var problems = new List<string>();

        Uri? webRoot = null;
        var url = Value(lookup, LibreNmsUrl);
        if (url is null)
        {
            problems.Add($"{LibreNmsUrl} is not set. Set it to the address of your LibreNMS server, e.g. https://librenms.example.com.");
        }
        else if (!LibreNmsConnection.TryParseWebRoot(url, out webRoot, out var urlError))
        {
            problems.Add($"{LibreNmsUrl}: {urlError}");
        }

        var token = Value(lookup, LibreNmsToken);
        if (token is null)
        {
            problems.Add($"{LibreNmsToken} is not set. Create an API token for a read-only LibreNMS user and set it here.");
        }

        Uri? backupWebRoot = null;
        var backupUrl = Value(lookup, LibreNmsBackupUrl);
        if (backupUrl is not null && !LibreNmsConnection.TryParseWebRoot(backupUrl, out backupWebRoot, out var backupError))
        {
            problems.Add($"{LibreNmsBackupUrl}: {backupError}");
        }

        var trusted = new List<string>();
        foreach (var entry in (Value(lookup, LibreNmsTrustedCerts) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsSha256Fingerprint(entry))
            {
                trusted.Add(entry);
            }
            else
            {
                problems.Add($"{LibreNmsTrustedCerts}: '{entry}' is not a SHA-256 fingerprint (64 hex digits, colons optional).");
            }
        }

        var timeout = ParseInt(lookup, LibreNmsTimeout, DefaultTimeoutSeconds, minimum: 1, problems);
        var maxResultBytes = ParseInt(lookup, MaxResultBytesKey, DefaultMaxResultBytes, MinimumMaxResultBytes, problems);

        errors = problems;
        if (problems.Count > 0)
        {
            return null;
        }

        var connection = new LibreNmsConnection(
            webRoot!,
            token!,
            allowUntrustedCertificate: true,
            timeoutSeconds: timeout,
            backupWebRoot: backupWebRoot,
            trustedCertificates: trusted);

        return new McpOptions(connection, maxResultBytes);
    }

    /// <summary>Unset and blank are the same: a blank line in a compose file or .env shouldn't count as a value.</summary>
    private static string? Value(Func<string, string?> lookup, string key)
        => lookup(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int ParseInt(Func<string, string?> lookup, string key, int fallback, int minimum, List<string> problems)
    {
        var text = Value(lookup, key);
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum)
        {
            problems.Add($"{key}: '{text}' is not a whole number of at least {minimum}.");
            return fallback;
        }

        return value;
    }

    /// <summary>Accepts the forms browsers and openssl print: "AB:CD:...", "ab cd ...", or 64 bare hex digits.</summary>
    internal static bool IsSha256Fingerprint(string text)
    {
        var hex = text.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }
}
