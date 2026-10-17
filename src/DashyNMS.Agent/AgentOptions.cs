namespace DashyNMS.Agent;

/// <summary>Limits the tool layer applies to everything it returns.</summary>
public sealed class AgentOptions
{
    public const int DefaultMaxResultBytes = 16 * 1024;

    /// <summary>
    /// The most JSON (UTF-8 bytes) one tool call returns. Bigger results lose
    /// list entries, then text, and say so - see <see cref="ToolResult"/>.
    /// </summary>
    public int MaxResultBytes { get; init; } = DefaultMaxResultBytes;
}
