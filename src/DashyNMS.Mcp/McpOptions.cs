using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using DashyNMS.Mcp.Auditing;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;

namespace DashyNMS.Mcp;

/// <summary>How clients reach the server.</summary>
public enum McpTransport
{
    /// <summary>The client starts the server and talks over its stdin and stdout.</summary>
    Stdio,

    /// <summary>The server listens for MCP's Streamable HTTP, with bearer tokens.</summary>
    Http,
}

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
    /// <summary>LibreNMS's discovery domain, for matching short hostnames to Unimus devices - see <see cref="UnimusMyDomain"/>.</summary>
    public const string UnimusMyDomainKey = "UNIMUS_MY_DOMAIN";
    public const string GraylogUrl = "GRAYLOG_URL";
    public const string GraylogUsername = "GRAYLOG_USERNAME";
    public const string GraylogPassword = "GRAYLOG_PASSWORD";
    public const string GraylogVersion = "GRAYLOG_VERSION";
    public const string GraylogQueryField = "GRAYLOG_QUERY_FIELD";
    public const string GraylogMatchAnyAddressKey = "GRAYLOG_MATCH_ANY_ADDRESS";
    public const string GraylogTrustedCerts = "GRAYLOG_TRUSTED_CERTS";
    public const string MaxResultBytesKey = "MCP_MAX_RESULT_BYTES";
    public const string ConfigTextKey = "MCP_CONFIG_TEXT";
    public const string MaxConcurrentRequestsKey = "MCP_MAX_CONCURRENT_REQUESTS";
    public const string ToolsAllow = "MCP_TOOLS_ALLOW";
    public const string ToolsDeny = "MCP_TOOLS_DENY";
    public const string AuditGelfUrl = "AUDIT_GELF_URL";
    public const string AuditGelfTrustedCerts = "AUDIT_GELF_TRUSTED_CERTS";
    public const string TransportKey = "MCP_TRANSPORT";
    public const string HttpPortKey = "MCP_HTTP_PORT";
    public const string HttpAddressKey = "MCP_HTTP_ADDRESS";
    public const string HttpTokensKey = "MCP_HTTP_TOKENS";
    public const string HttpAllowedOriginsKey = "MCP_HTTP_ALLOWED_ORIGINS";
    public const string RateLimitPerMinuteKey = "MCP_RATE_LIMIT_PER_MINUTE";
    public const string ResourcesDirectoryKey = "MCP_RESOURCES_DIR";
    public const string LastKnownGoodKey = "MCP_LAST_KNOWN_GOOD";
    public const string LastKnownGoodDirectoryKey = "MCP_LAST_KNOWN_GOOD_DIR";
    public const string LastKnownGoodHoursKey = "MCP_LAST_KNOWN_GOOD_HOURS";

    public const int DefaultTimeoutSeconds = 30;
    public const int DefaultMaxResultBytes = DashyNMS.Agent.AgentOptions.DefaultMaxResultBytes;

    /// <summary>Below this a single device summary wouldn't fit, so a smaller cap is a mistake rather than a choice.</summary>
    public const int MinimumMaxResultBytes = 1024;

    /// <summary>More than this in flight isn't a limit on anything, so a larger number is a mistake.</summary>
    public const int MaximumConcurrentRequests = 256;

    /// <summary>Past this nothing is being limited, and every allowed call is remembered for a minute.</summary>
    public const int MaximumRateLimitPerMinute = 10_000;

    public const int DefaultLastKnownGoodHours = 24;

    /// <summary>A week: an answer older than that says little about the network now.</summary>
    public const int MaximumLastKnownGoodHours = 168;

    public const int DefaultHttpPort = 8080;

    /// <summary>
    /// Whether this build has the HTTP transport. A host that bundles the
    /// server to run it over stdio builds without it (McpHttpTransport=false),
    /// and ships neither ASP.NET Core nor a way to open a port.
    /// </summary>
#if MCP_HTTP
    internal static bool HasHttp => true;
#else
    internal static bool HasHttp => false;
#endif

    private McpOptions(
        LibreNmsConnection connection,
        bool timestampsAreUtc,
        UnimusConnection? unimus,
        string? unimusMyDomain,
        GraylogConnection? graylog,
        string graylogQueryField,
        bool graylogMatchAnyAddress,
        int maxResultBytes,
        DashyNMS.Agent.ConfigText configText, AppSettings sensorSettings)
    {
        Connection = connection;
        TimestampsAreUtc = timestampsAreUtc;
        Unimus = unimus;
        UnimusMyDomain = unimusMyDomain;
        Graylog = graylog;
        GraylogField = graylogQueryField;
        GraylogMatchAnyAddress = graylogMatchAnyAddress;
        MaxResultBytes = maxResultBytes;
        ConfigText = configText;
        SensorSettings = sensorSettings;
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

    /// <summary>Thresholds owned by this standalone host, never read from app settings files.</summary>
    public AppSettings SensorSettings { get; }

    /// <summary>
    /// Whether LibreNMS stores its timestamps in UTC, as desktop's "Server
    /// stores timestamps in UTC" setting (on by default there too). Off, they
    /// are read as this server's local time, so in a container set TZ to the
    /// LibreNMS server's zone.
    /// </summary>
    public bool TimestampsAreUtc { get; }

    /// <summary>Unimus, when both its variables are set; null leaves it out of <c>what_changed</c>.</summary>
    public UnimusConnection? Unimus { get; }

    /// <summary>
    /// LibreNMS's discovery domain (its <c>mydomain</c> setting), as desktop's
    /// Unimus "discovery domain" setting. LibreNMS's API doesn't expose it, so
    /// a device LibreNMS knows as <c>core-sw1</c> only matches a Unimus device
    /// at <c>core-sw1.example.net</c> when the host says what the suffix is.
    /// Null, the default, leaves matching exactly as it was before the setting
    /// existed. Accepted without Unimus configured, as <see cref="GraylogField"/>
    /// is without Graylog, so a host can pass its saved value unconditionally.
    /// </summary>
    public string? UnimusMyDomain { get; }

    /// <summary>Graylog, when its address, username and password are set; null leaves it out of <c>what_changed</c>.</summary>
    public GraylogConnection? Graylog { get; }

    /// <summary>The Graylog field a device's addresses are matched against - LibreNMS's own default is "source".</summary>
    public string GraylogField { get; }

    /// <summary>Whether a device's Graylog messages are matched on its interface addresses too, as desktop's "match any address".</summary>
    public bool GraylogMatchAnyAddress { get; }

    /// <summary>The most JSON one tool call may return, in bytes.</summary>
    public int MaxResultBytes { get; }

    /// <summary>The most requests in flight to each of LibreNMS, Unimus and Graylog; more queue (#38).</summary>
    public int MaxConcurrentRequests { get; private init; }

    /// <summary>The most tool calls one client may make in a minute, or null - the default - for no limit (#38).</summary>
    public int? RateLimitPerMinute { get; private init; }

    /// <summary>The folder of reference files offered as resources, or null for none (#20).</summary>
    public string? ResourcesDirectory { get; private init; }

    /// <summary>How old an answer may be given when LibreNMS can't be reached, or null to give none (#39).</summary>
    public TimeSpan? LastKnownGood { get; private init; }

    /// <summary>Where those answers are kept on disk as well as in memory; null for memory alone.</summary>
    public string? LastKnownGoodDirectory { get; private init; }

    /// <summary>
    /// Everything one of those answers depends on: the LibreNMS account, the
    /// settings that shape a result, and the build that shaped it. Holds the
    /// token, so it's only ever made into keys.
    /// </summary>
    internal string AnswerIdentity => JsonSerializer.Serialize(new
    {
        Connection.WebRoot,
        Connection.BackupWebRoot,
        Connection.ApiToken,
        TimestampsAreUtc,
        MaxResultBytes,
        SensorSettings.DbmThresholds,
        SensorSettings.SignalThresholds,
        SensorSettings.TemperatureThresholds,
        SensorSettings.FanSpeedThresholds,
        SensorSettings.OverrideSensorLimitsWithAppThresholds,
        Build = typeof(McpOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    });

    /// <summary>The tools the server offers: every one unless the operator's lists say otherwise (#37).</summary>
    public required ToolPolicy Tools { get; init; }

    /// <summary>A Graylog GELF input that gets the audit trail as well as stderr, or null for stderr alone (#18).</summary>
    public GelfTarget? AuditGelf { get; private init; }

    /// <summary>How clients reach the server: stdio unless the operator asks for HTTP (#17).</summary>
    public McpTransport Transport { get; private init; }

    public int HttpPort { get; private init; } = DefaultHttpPort;

    /// <summary>The address to listen on, this machine only by default; null is every address.</summary>
    public IPAddress? HttpAddress { get; private init; } = IPAddress.Loopback;

    /// <summary>Who may use the server over HTTP. Never null when that's the transport.</summary>
    public HttpTokens? HttpTokens { get; private init; }

    /// <summary>The web origins that may call the server from a browser; none by default.</summary>
    public IReadOnlyCollection<string> HttpAllowedOrigins { get; private init; } = [];

    /// <summary>HTTP settings given without HTTP turned on, which is probably not what was meant.</summary>
    private bool HasUnusedHttpSettings { get; init; }

    /// <summary>
    /// Every credential the server holds, for keeping them out of the audit
    /// trail (#18). The Graylog username is one, as an access token is given
    /// as the username.
    /// </summary>
    internal IReadOnlyList<string> Secrets
        => new[] { Connection.ApiToken, Unimus?.ApiToken, Graylog?.Username, Graylog?.Password }.OfType<string>().Concat(HttpTokens?.Values ?? []).ToArray();

    /// <summary>
    /// Settings that work but put credentials at risk: a service reached over
    /// plain http sends its token or password across the network unencrypted.
    /// Printed at startup rather than refused, since some Unimus and Graylog
    /// servers only serve http (the pre-release review found one).
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            void Check(Uri? root, string key, string what)
            {
                if (root is not null && root.Scheme == Uri.UriSchemeHttp)
                {
                    warnings.Add($"{key} uses http://, so the {what} crosses the network unencrypted. Use https:// if the server supports it.");
                }
            }

            Check(Connection.WebRoot, LibreNmsUrl, "LibreNMS API token");
            Check(Connection.BackupWebRoot, LibreNmsBackupUrl, "LibreNMS API token");
            Check(Unimus?.WebRoot, UnimusUrl, "Unimus API token");
            Check(Graylog?.Root, GraylogUrl, "Graylog password");
            if (AuditGelf?.IsUnencrypted == true)
            {
                warnings.Add($"{AuditGelfUrl} isn't https://, so the audit trail, which names devices and searches, crosses the network unencrypted. Use a GELF HTTP input behind https:// if you can.");
            }

            if (Transport == McpTransport.Http && (HttpAddress is null || !IPAddress.IsLoopback(HttpAddress)))
            {
                warnings.Add($"{TransportKey}=http serves plain HTTP, so tokens and answers are only private behind a reverse proxy that does TLS. Don't expose the port itself.");
            }

            if (HasUnusedHttpSettings)
            {
                warnings.Add($"MCP_HTTP_ settings are set but {TransportKey} isn't http, so they do nothing.");
            }

            return warnings;
        }
    }

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
        var unimusMyDomain = ParseDiscoveryDomain(lookup, problems);
        var graylog = LoadGraylog(lookup, timeout, problems);
        var graylogField = Value(lookup, GraylogQueryField) ?? GraylogSettings.DefaultQueryField;
        var graylogMatchAny = ParseBool(lookup, GraylogMatchAnyAddressKey, fallback: false, problems);
        var maxResultBytes = ParseInt(lookup, MaxResultBytesKey, DefaultMaxResultBytes, MinimumMaxResultBytes, problems);
        var maxConcurrentRequests = ParseInt(lookup, MaxConcurrentRequestsKey, DashyNMS.Agent.AgentOptions.DefaultMaxConcurrentRequests, minimum: 1, problems, MaximumConcurrentRequests);
        var rateLimit = ParseInt(lookup, RateLimitPerMinuteKey, fallback: 0, minimum: 0, problems, MaximumRateLimitPerMinute);
        var lastKnownGood = ParseBool(lookup, LastKnownGoodKey, fallback: true, problems);
        var lastKnownGoodHours = ParseInt(lookup, LastKnownGoodHoursKey, DefaultLastKnownGoodHours, minimum: 1, problems, MaximumLastKnownGoodHours);
        var lastKnownGoodDirectory = ParseDirectory(lookup, LastKnownGoodDirectoryKey, problems);
        var resourcesDirectory = ParseDirectory(lookup, ResourcesDirectoryKey, problems);
        if (resourcesDirectory is not null && !Directory.Exists(resourcesDirectory))
        {
            // The operator meant the model to have it, so going without isn't a detail.
            problems.Add($"{ResourcesDirectoryKey}: there's no such folder, or the server can't see it.");
        }

        var tools = ParseTools(lookup, problems);
        var transport = ParseTransport(lookup, problems);
        var http = transport == McpTransport.Http;
        var httpPort = http ? ParseInt(lookup, HttpPortKey, DefaultHttpPort, minimum: 1, problems, maximum: 65535) : DefaultHttpPort;
        var httpAddress = http ? ParseHttpAddress(lookup, problems) : IPAddress.Loopback;
        var httpTokens = http ? ParseHttpTokens(lookup, problems) : null;
        var httpOrigins = http ? ParseOrigins(lookup, problems) : [];
        var auditGelf = ParseGelf(lookup, problems);
        var configText = ParseConfigText(lookup, problems);
        var sensorSettings = LoadSensorSettings(lookup, problems);

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

        return new McpOptions(connection, timestampsAreUtc, unimus, unimusMyDomain, graylog, graylogField, graylogMatchAny, maxResultBytes, configText, sensorSettings)
        {
            Tools = tools,
            AuditGelf = auditGelf,
            Transport = transport,
            HttpPort = httpPort,
            HttpAddress = httpAddress,
            HttpTokens = httpTokens,
            HttpAllowedOrigins = httpOrigins,
            HasUnusedHttpSettings = !http && new[] { HttpPortKey, HttpTokensKey, HttpAllowedOriginsKey }.Any(key => Value(lookup, key) is not null),
            MaxConcurrentRequests = maxConcurrentRequests,
            RateLimitPerMinute = rateLimit > 0 ? rateLimit : null,
            ResourcesDirectory = resourcesDirectory,
            LastKnownGood = lastKnownGood ? TimeSpan.FromHours(lastKnownGoodHours) : null,
            LastKnownGoodDirectory = lastKnownGood ? lastKnownGoodDirectory : null,
        };
    }

    /// <summary>A folder's full path: a relative one would depend on where the client happened to start the server.</summary>
    private static string? ParseDirectory(Func<string, string?> lookup, string key, List<string> problems)
    {
        var path = Value(lookup, key);
        if (path is null || Path.IsPathFullyQualified(path))
        {
            return path;
        }

        problems.Add($"{key} isn't a full path.");
        return null;
    }

    private static ToolPolicy ParseTools(Func<string, string?> lookup, List<string> problems)
    {
        // A list of its own: the policy only reports a list that leaves nothing on when the names in it were right.
        var toolProblems = new List<string>();
        var policy = ToolPolicy.Parse(Value(lookup, ToolsAllow), Value(lookup, ToolsDeny), toolProblems);
        problems.AddRange(toolProblems);
        return policy;
    }

    private static McpTransport ParseTransport(Func<string, string?> lookup, List<string> problems)
    {
        switch (Value(lookup, TransportKey)?.ToLowerInvariant())
        {
            case null or "stdio":
                return McpTransport.Stdio;
            case "http" when HasHttp:
                return McpTransport.Http;
            case "http":
                problems.Add($"{TransportKey}: this build of the server only has stdio. Use the container image, or build without McpHttpTransport=false.");
                return McpTransport.Stdio;
            case var other:
                problems.Add($"{TransportKey}: '{other}' should be stdio or http.");
                return McpTransport.Stdio;
        }
    }

    private static IPAddress? ParseHttpAddress(Func<string, string?> lookup, List<string> problems)
    {
        switch (Value(lookup, HttpAddressKey))
        {
            case null:
                return IPAddress.Loopback;
            case "*":
                return null;
            case var text when IPAddress.TryParse(text, out var address):
                return address;
            case var other:
                problems.Add($"{HttpAddressKey}: '{other}' isn't an IP address. Use one of this machine's, 127.0.0.1 for this machine only, or * for every address.");
                return IPAddress.Loopback;
        }
    }

    private static HttpTokens? ParseHttpTokens(Func<string, string?> lookup, List<string> problems)
    {
        if (Value(lookup, HttpTokensKey) is { } text)
        {
            return HttpTokens.Parse(text, HttpTokensKey, problems);
        }

        problems.Add($"{HttpTokensKey} is not set. The server doesn't run over HTTP without a token: set one or more as label:token, such as alice:$(openssl rand -hex 32).");
        return null;
    }

    /// <summary>Origins as a browser writes them: scheme, host and any port, with nothing after.</summary>
    private static List<string> ParseOrigins(Func<string, string?> lookup, List<string> problems)
    {
        var origins = new List<string>();
        foreach (var entry in (Value(lookup, HttpAllowedOriginsKey) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.GetLeftPart(UriPartial.Authority) == entry.TrimEnd('/') && uri.UserInfo.Length == 0)
            {
                origins.Add(uri.GetLeftPart(UriPartial.Authority));
            }
            else
            {
                problems.Add($"{HttpAllowedOriginsKey}: '{entry}' isn't an origin, such as https://tools.example.net.");
            }
        }

        return origins;
    }

    private static GelfTarget? ParseGelf(Func<string, string?> lookup, List<string> problems)
    {
        var trusted = Fingerprints(lookup, AuditGelfTrustedCerts, problems);
        if (Value(lookup, AuditGelfUrl) is not { } text)
        {
            return null;
        }

        var target = GelfTarget.TryParse(text, trusted);
        if (target is null)
        {
            // Fixed rather than quoting the value, which may hold credentials.
            problems.Add($"{AuditGelfUrl} isn't a GELF input's address. Use https://, http:// or udp:// with a host, such as https://graylog.example.net:12201/gelf or udp://graylog.example.net:12201, and no user name or password.");
        }

        return target;
    }

    private static AppSettings LoadSensorSettings(Func<string, string?> lookup, List<string> problems)
    {
        var settings = new AppSettings();
        double Number(string key, double fallback)
        {
            var text = Value(lookup, key);
            if (text is null) return fallback;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
            problems.Add($"{key} must be a finite number.");
            return fallback;
        }
        settings.DbmThresholds.WarningThreshold = Number("MCP_DBM_WARNING_THRESHOLD", settings.DbmThresholds.WarningThreshold);
        settings.DbmThresholds.CriticalThreshold = Number("MCP_DBM_CRITICAL_THRESHOLD", settings.DbmThresholds.CriticalThreshold);
        settings.DbmThresholds.IgnoreAtOrAbove = Number("MCP_DBM_IGNORE_AT_OR_ABOVE", settings.DbmThresholds.IgnoreAtOrAbove);
        settings.DbmThresholds.IgnoreAtOrBelow = Number("MCP_DBM_IGNORE_AT_OR_BELOW", settings.DbmThresholds.IgnoreAtOrBelow);
        if (!(settings.DbmThresholds.IgnoreAtOrBelow < settings.DbmThresholds.CriticalThreshold && settings.DbmThresholds.CriticalThreshold <= settings.DbmThresholds.WarningThreshold && settings.DbmThresholds.WarningThreshold < settings.DbmThresholds.IgnoreAtOrAbove))
            problems.Add("MCP_DBM thresholds must be ordered: ignore below < critical <= warning < ignore above.");
        settings.SignalThresholds.WarningThreshold = Number("MCP_SIGNAL_WARNING_THRESHOLD", settings.SignalThresholds.WarningThreshold);
        settings.SignalThresholds.CriticalThreshold = Number("MCP_SIGNAL_CRITICAL_THRESHOLD", settings.SignalThresholds.CriticalThreshold);
        settings.SignalThresholds.IgnoreAtOrAbove = Number("MCP_SIGNAL_IGNORE_AT_OR_ABOVE", settings.SignalThresholds.IgnoreAtOrAbove);
        settings.SignalThresholds.IgnoreAtOrBelow = Number("MCP_SIGNAL_IGNORE_AT_OR_BELOW", settings.SignalThresholds.IgnoreAtOrBelow);
        if (!(settings.SignalThresholds.IgnoreAtOrBelow < settings.SignalThresholds.CriticalThreshold && settings.SignalThresholds.CriticalThreshold <= settings.SignalThresholds.WarningThreshold && settings.SignalThresholds.WarningThreshold < settings.SignalThresholds.IgnoreAtOrAbove))
            problems.Add("MCP_SIGNAL thresholds must be ordered: ignore below < critical <= warning < ignore above.");
        settings.TemperatureThresholds.LowCritical = Number("MCP_TEMPERATURE_LOW_CRITICAL", settings.TemperatureThresholds.LowCritical);
        settings.TemperatureThresholds.LowWarning = Number("MCP_TEMPERATURE_LOW_WARNING", settings.TemperatureThresholds.LowWarning);
        settings.TemperatureThresholds.HighWarning = Number("MCP_TEMPERATURE_HIGH_WARNING", settings.TemperatureThresholds.HighWarning);
        settings.TemperatureThresholds.HighCritical = Number("MCP_TEMPERATURE_HIGH_CRITICAL", settings.TemperatureThresholds.HighCritical);
        if (!(settings.TemperatureThresholds.LowCritical <= settings.TemperatureThresholds.LowWarning && settings.TemperatureThresholds.LowWarning <= settings.TemperatureThresholds.HighWarning && settings.TemperatureThresholds.HighWarning <= settings.TemperatureThresholds.HighCritical))
            problems.Add("MCP_TEMPERATURE thresholds must be ordered: low critical <= low warning <= high warning <= high critical.");
        settings.FanSpeedThresholds.LowCritical = Number("MCP_FAN_SPEED_LOW_CRITICAL", settings.FanSpeedThresholds.LowCritical);
        settings.FanSpeedThresholds.LowWarning = Number("MCP_FAN_SPEED_LOW_WARNING", settings.FanSpeedThresholds.LowWarning);
        settings.FanSpeedThresholds.HighWarning = Number("MCP_FAN_SPEED_HIGH_WARNING", settings.FanSpeedThresholds.HighWarning);
        settings.FanSpeedThresholds.HighCritical = Number("MCP_FAN_SPEED_HIGH_CRITICAL", settings.FanSpeedThresholds.HighCritical);
        if (!(settings.FanSpeedThresholds.LowCritical <= settings.FanSpeedThresholds.LowWarning && settings.FanSpeedThresholds.LowWarning <= settings.FanSpeedThresholds.HighWarning && settings.FanSpeedThresholds.HighWarning <= settings.FanSpeedThresholds.HighCritical))
            problems.Add("MCP_FAN_SPEED thresholds must be ordered: low critical <= low warning <= high warning <= high critical.");
        settings.OverrideSensorLimitsWithAppThresholds = ParseBool(lookup, "MCP_OVERRIDE_SENSOR_LIMITS", false, problems);
        return settings;
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

    /// <summary>
    /// Leading dots (<c>.example.net</c>, a common way to write a suffix) are
    /// dropped as DashyNMS mobile does, and so is one trailing dot (the
    /// fully qualified <c>example.net.</c>): both mean the same domain, and
    /// desktop saves the value as typed, so refusing them would stop the whole
    /// server over punctuation. Anything else that isn't a domain name is a
    /// problem. The problem line is fixed rather than quoting the value: a
    /// token pasted into the wrong variable would otherwise end up on stderr,
    /// and from there in the MCP client's log.
    /// </summary>
    private static string? ParseDiscoveryDomain(Func<string, string?> lookup, List<string> problems)
    {
        var value = Value(lookup, UnimusMyDomainKey);
        if (value is null)
        {
            return null;
        }

        var domain = value.TrimStart('.');
        if (domain.EndsWith('.'))
        {
            domain = domain[..^1];
        }

        if (IsDiscoveryDomain(domain))
        {
            return domain;
        }

        problems.Add($"{UnimusMyDomainKey} isn't a domain name. Set it to LibreNMS's discovery domain (its mydomain setting), such as example.net, or leave it unset.");
        return null;
    }

    /// <summary>
    /// A DNS name: dot-separated labels of 1 to 63 letters, digits, hyphens or
    /// underscores, no label starting or ending with a hyphen, 253 characters
    /// at most. Core appends it verbatim after a dot, so an empty label, a
    /// space or a scheme would build an address Unimus can never match;
    /// refusing it at startup says so instead of quietly matching nothing.
    /// Underscores are allowed because some internal zones use them and
    /// refusing a working value would stop the server for no gain.
    /// </summary>
    internal static bool IsDiscoveryDomain(string text)
    {
        if (text.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in text.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
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

    private static int ParseInt(Func<string, string?> lookup, string key, int fallback, int minimum, List<string> problems, int maximum = int.MaxValue)
    {
        var text = Value(lookup, key);
        if (text is null)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
        {
            problems.Add(maximum == int.MaxValue
                ? $"{key}: '{text}' is not a whole number of at least {minimum}."
                : $"{key}: '{text}' is not a whole number from {minimum} to {maximum}.");
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
