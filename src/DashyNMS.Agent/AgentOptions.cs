namespace DashyNMS.Agent;

/// <summary>Settings the tool layer applies to everything it reads and returns.</summary>
public sealed class AgentOptions
{
    public const int DefaultMaxResultBytes = 16 * 1024;

    /// <summary>
    /// The most JSON (UTF-8 bytes) one tool call returns. Bigger results lose
    /// list entries, then text, and say so - see <see cref="ToolResult"/>.
    /// </summary>
    public int MaxResultBytes { get; init; } = DefaultMaxResultBytes;

    /// <summary>
    /// Whether LibreNMS's zone-less timestamps are UTC (desktop's "Server
    /// stores timestamps in UTC", on by default) or the server's local time.
    /// </summary>
    public bool ServerTimestampsAreUtc { get; init; } = true;

    /// <summary>The Graylog field matched against a device's addresses, as LibreNMS's graylog.query_field.</summary>
    public string GraylogQueryField { get; init; } = "source";

    /// <summary>
    /// Whether a device's Graylog messages are matched on every address on its
    /// interfaces too, not only its hostname, display name, IP and sysName -
    /// desktop's and LibreNMS's "match any address" (graylog.match_any_address).
    /// </summary>
    public bool GraylogMatchAnyAddress { get; init; }

    /// <summary>How much of a device's configuration the tools may return (#16).</summary>
    public ConfigText ConfigText { get; init; } = ConfigText.Full;
}

/// <summary>
/// How much configuration text goes to the model. Redaction takes the
/// secrets out either way, but some organisations won't let configuration
/// leave their network at all; <see cref="Summary"/> still answers "did it
/// change, when, and by how much?" without the text.
/// </summary>
public enum ConfigText
{
    /// <summary>Backups and diffs as redacted text.</summary>
    Full,

    /// <summary>Whether and when the config changed, and how many lines, but no config text.</summary>
    Summary,

    /// <summary>No Unimus data at all.</summary>
    Off,
}
