namespace DashyNMS.Agent.Requests;

/// <summary>How a tool call went, for a host deciding whether its answer is worth remembering, or an earlier one worth giving (#39).</summary>
public enum ToolCallOutcome
{
    /// <summary>The call ended before the tool said how it went.</summary>
    Unknown,

    /// <summary>Answered, with nothing missing that this account may see.</summary>
    Complete,

    /// <summary>Answered, with something missing.</summary>
    Partial,

    /// <summary>LibreNMS couldn't be reached, took too long, or had trouble of its own.</summary>
    Unreachable,

    /// <summary>LibreNMS wouldn't give this account something, and the call gave no whole answer without it.</summary>
    Denied,

    /// <summary>LibreNMS refused the token itself.</summary>
    TokenRefused,

    /// <summary>The tool couldn't answer for some other reason, such as no such device.</summary>
    Failed,
}

/// <summary>
/// What <see cref="ToolRunner"/> says about the call it ran, for the host that
/// began a report around it. Ambient, as the SDK sits between the two.
/// </summary>
public sealed class ToolCallReport
{
    private static readonly AsyncLocal<ToolCallReport?> Ambient = new();

    /// <summary>The report begun for the call this code is running for, if any.</summary>
    internal static ToolCallReport? Current => Ambient.Value;

    /// <summary>Starts a report. Call it from the async method the call runs in, which is as far as it reaches.</summary>
    public static ToolCallReport Begin()
    {
        var report = new ToolCallReport();
        Ambient.Value = report;
        return report;
    }

    public ToolCallOutcome Outcome { get; private set; }

    /// <summary>The result's JSON, redacted and cut to its cap; null until the tool has answered.</summary>
    public string? Json { get; private set; }

    internal void Answer(ToolCallOutcome outcome, string json) => (Outcome, Json) = (outcome, json);
}
