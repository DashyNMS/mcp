using System.Globalization;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;

namespace DashyNMS.Mcp;

/// <summary>
/// The server's settings, read from environment variables (or anything else
/// the host's configuration carries) and checked before anything connects.
/// </summary>
/// <remarks>
/// Environment variables only, on purpose: the server runs under an MCP
/// client or in a container, with no person to ask and nowhere it should
/// write. Secrets are kept in the connections and never echoed back in an
/// error.
/// </remarks>
public sealed class McpOptions
{
    public const string LibreNmsUrl = "LIBRENMS_URL";
    public const string LibreNmsToken = "LIBRENMS_TOKEN";
    public const string LibreNmsBackupUrl = "LIBRENMS_BACKUP_URL";
    public const string LibreNmsTrustedCerts = "LIBRENMS_TRUSTED_CERTS";
    public const string LibreNmsTimeout = "LIBRENMS_TIMEOUT";
    public const string LibreNmsTimestampsUtc = "LIBRENMS_TIMESTAMPS_UTC";
    public const string UnimusUrl = "UNIMUS_URL";
    public const string UnimusToken = "UNIMUS_TOKEN";
    public const string UnimusTrustedCerts = "UNIMUS_TRUSTED_CERTS";
    public const string GraylogUrl = "GRAYLOG_URL";
    public const string GraylogUsername = "GRAYLOG_USERNAME";
    public const string GraylogPassword = "GRAYLOG_PASSWORD";
    public const string GraylogVersion = "GRAYLOG_VERSION";
    public const string GraylogQueryField = "GRAYLOG_QUERY_FIELD";
    public const string GraylogTrustedCerts = "GRAYLOG_TRUSTED_CERTS";
    public const string MaxResultBytesKey = "MCP_MAX_RESULT_BYTES";
    public const string ConfigTextKey = "MCP_CONFIG_TEXT";

    public const int DefaultTimeoutSeconds = 30;
    public const int DefaultMaxResultBytes = DashyNMS.Agent.AgentOptions.DefaultMaxResultBytes;

    /// <summary>Below this a single device summary wouldn't fit, so a smaller cap is a mistake rather than a choice.</summary>
    public const int MinimumMaxResultBytes = 1024;

    private McpOptions(
        LibreNmsConnection connection,
        bool timestampsAreUtc,
        UnimusConnection? unimus,
        GraylogConnection? graylog,
        string graylogQueryField,
        int maxResultBytes,
        DashyNMS.Agent.ConfigText configText)
    {
        Connection = connection;
        TimestampsAreUtc = timestampsAreUtc;
        Unimus = unimus;
        Graylog = graylog;
        GraylogField = graylogQueryField;
        MaxResultBytes = maxResultBytes;
        ConfigText = configText;
    }

    /// <summary>
    /// The LibreNMS connection. Certificate pinning is always on (Core's
    /// <c>allowUntrustedCertificate</c>): a certificate that passes the normal
    /// checks is accepted as usual, one that doesn't is accepted only if its
    /// fingerprint is in <see cref="LibreNmsTrustedCerts"/>, and any other is
    /// reported with its fingerprint so startup can say what to add (#189 in
    /// the desktop repo). There is deliberately no accept-everything switch.
    /// The same goes for Unimus and Graylog.
    /// </summary>
    public LibreNmsConnection Connection { get; }

    /// <summary>
    /// Whether LibreNMS stores its timestamps in UTC, as desktop's "Server
    /// stores timestamps in UTC" setting (on by default there too). Off, they
    /// are read as this server's local time, so in a container set TZ to the
    /// LibreNMS server's zone.
    /// </summary>
    public bool TimestampsAreUtc { get; }

    /// <summary>Unimus, when both its variables are set; null leaves it out of <c>what_changed</c>.</summary>
    public UnimusConnection? Unimus { get; }

    /// <summary>Graylog, when its address, username and password are set; null leaves it out of <c>what_changed</c>.</summary>
    public GraylogConnection? Graylog { get; }

    /// <summary>The Graylog field a device's addresses are matched against - LibreNMS's own default is "source".</summary>
    public string GraylogField { get; }

    /// <summary>The most JSON one tool call may return, in bytes.</summary>
    public int MaxResultBytes { get; }

    /// <summary>How much configuration text the Unimus tools return: full (the default), summary or off - see <see cref="DashyNMS.Agent.ConfigText"/>.</summary>
    public DashyNMS.Agent.ConfigText ConfigText { get; }

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

        var trusted = Fingerprints(lookup, LibreNmsTrustedCerts, problems);
        var timeout = ParseInt(lookup, LibreNmsTimeout, DefaultTimeoutSeconds, minimum: 1, problems);
        var timestampsAreUtc = ParseBool(lookup, LibreNmsTimestampsUtc, fallback: true, problems);
        var unimus = LoadUnimus(lookup, timeout, problems);
        var graylog = LoadGraylog(lookup, timeout, problems);
        var graylogField = Value(lookup, GraylogQueryField) ?? GraylogSettings.DefaultQueryField;
        var maxResultBytes = ParseInt(lookup, MaxResultBytesKey, DefaultMaxResultBytes, MinimumMaxResultBytes, problems);
        var configText = ParseConfigText(lookup, problems);

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

        return new McpOptions(connection, timestampsAreUtc, unimus, graylog, graylogField, maxResultBytes, configText);
    }

    private static UnimusConnection? LoadUnimus(Func<string, string?> lookup, int timeout, List<string> problems)
    {
        var url = Value(lookup, UnimusUrl);
        var token = Value(lookup, UnimusToken);
        var trusted = Fingerprints(lookup, UnimusTrustedCerts, problems);
        if (url is null && token is null)
        {
            return null;
        }

        if (url is null || token is null)
        {
            problems.Add($"Set both {UnimusUrl} and {UnimusToken} to use Unimus, or neither.");
            return null;
        }

        if (!UnimusConnection.TryParseWebRoot(url, out var webRoot, out var error) || webRoot is null)
        {
            problems.Add($"{UnimusUrl}: {error}");
            return null;
        }

        return new UnimusConnection(webRoot, token, allowUntrustedCertificate: true, timeoutSeconds: timeout, trustedCertificates: trusted);
    }

    private static GraylogConnection? LoadGraylog(Func<string, string?> lookup, int timeout, List<string> problems)
    {
        var url = Value(lookup, GraylogUrl);
        var username = Value(lookup, GraylogUsername);
        var password = Value(lookup, GraylogPassword);
        var trusted = Fingerprints(lookup, GraylogTrustedCerts, problems);
        if (url is null && username is null && password is null)
        {
            return null;
        }

        if (url is null || username is null || password is null)
        {
            problems.Add($"Set {GraylogUrl}, {GraylogUsername} and {GraylogPassword} to use Graylog, or none of them. With an access token, the token is the username and the password is \"token\".");
            return null;
        }

        // "other" needs a custom search path LibreNMS users set by hand; the
        // PoC sticks to the two standard API layouts.
        var version = Value(lookup, GraylogVersion) ?? GraylogSettings.Version21;
        if (version is not (GraylogSettings.Version20 or GraylogSettings.Version21))
        {
            problems.Add($"{GraylogVersion}: '{version}' isn't supported. Use {GraylogSettings.Version21} for Graylog 2.1 or newer, or {GraylogSettings.Version20}.");
            return null;
        }

        if (!GraylogConnection.TryParseRoot(url, port: null, out var root, out var error) || root is null)
        {
            problems.Add($"{GraylogUrl}: {error}");
            return null;
        }

        return new GraylogConnection(root, version, baseUri: null, username, password, allowUntrustedCertificate: true, timeoutSeconds: timeout, trustedCertificates: trusted);
    }

    /// <summary>Unset and blank are the same: a blank line in a compose file or .env shouldn't count as a value.</summary>
    private static string? Value(Func<string, string?> lookup, string key)
        => lookup(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static List<string> Fingerprints(Func<string, string?> lookup, string key, List<string> problems)
    {
        var trusted = new List<string>();
        foreach (var entry in (Value(lookup, key) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsSha256Fingerprint(entry))
            {
                trusted.Add(entry);
            }
            else
            {
                problems.Add($"{key}: '{entry}' is not a SHA-256 fingerprint (64 hex digits, colons optional).");
            }
        }

        return trusted;
    }

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

    private static DashyNMS.Agent.ConfigText ParseConfigText(Func<string, string?> lookup, List<string> problems)
    {
        switch (Value(lookup, ConfigTextKey)?.ToLowerInvariant())
        {
            case null or "full":
                return DashyNMS.Agent.ConfigText.Full;
            case "summary":
                return DashyNMS.Agent.ConfigText.Summary;
            case "off" or "none":
                return DashyNMS.Agent.ConfigText.Off;
            case var other:
                problems.Add($"{ConfigTextKey}: '{other}' should be full, summary or off.");
                return DashyNMS.Agent.ConfigText.Full;
        }
    }

    private static bool ParseBool(Func<string, string?> lookup, string key, bool fallback, List<string> problems)
    {
        switch (Value(lookup, key)?.ToLowerInvariant())
        {
            case null:
                return fallback;
            case "true" or "1" or "yes" or "on":
                return true;
            case "false" or "0" or "no" or "off":
                return false;
            case var other:
                problems.Add($"{key}: '{other}' should be true or false.");
                return fallback;
        }
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
