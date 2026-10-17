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
}
