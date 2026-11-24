using System.Text.Json.Nodes;

namespace DashyNMS.Mcp.Auditing;

/// <summary>How a tool call ended, as the audit trail records it.</summary>
public static class AuditStatus
{
    /// <summary>The tool answered.</summary>
    public const string Ok = "ok";

    /// <summary>LibreNMS couldn't be reached, so the last good answer was given (#39).</summary>
    public const string Stale = "stale";

    /// <summary>The tool answered that it couldn't: no such device, or LibreNMS failed.</summary>
    public const string Error = "error";

    /// <summary>The call never ran: no such tool (or one that's off), or arguments it doesn't take.</summary>
    public const string Rejected = "rejected";

    /// <summary>The call never ran: the client was over its rate limit.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The client gave up on the call before it finished.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The server failed in a way it didn't expect.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One tool call, as it's recorded (#18): who asked for what, and how it went,
/// but never what it returned.
/// </summary>
/// <param name="Time">When the call arrived.</param>
/// <param name="Transport">stdio or http.</param>
/// <param name="TokenLabel">The label of the HTTP token used; null over stdio. Never the token.</param>
/// <param name="ClientName">What the MCP client calls itself. The client's own word for it, so not proof of anything.</param>
/// <param name="ClientVersion">The MCP client's version, likewise.</param>
/// <param name="Tool">The tool asked for.</param>
/// <param name="Arguments">Its declared arguments, redacted; null when there's no such tool to say which are declared.</param>
/// <param name="UndeclaredArguments">How many other arguments came with it. They're counted rather than recorded: nothing says what they hold.</param>
/// <param name="Status">One of <see cref="AuditStatus"/>.</param>
/// <param name="ResultBytes">The size of the result: text as UTF-8, plus any images. Zero when there wasn't one.</param>
/// <param name="Duration">From the call arriving to its answer.</param>
public sealed record AuditEntry(
    DateTimeOffset Time,
    string Transport,
    string? TokenLabel,
    string? ClientName,
    string? ClientVersion,
    string Tool,
    JsonObject? Arguments,
    int UndeclaredArguments,
    string Status,
    long ResultBytes,
    TimeSpan Duration)
{
    /// <summary>The line written to stderr: one JSON object, everything but ASCII escaped, so nothing in it can start a line of its own.</summary>
    public string ToJson()
    {
        var json = new JsonObject
        {
            ["event"] = "tool_call",
            ["time"] = Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["transport"] = Transport,
        };

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                json[name] = value;
            }
        }

        Add("token_label", TokenLabel);
        Add("client_name", ClientName);
        Add("client_version", ClientVersion);
        Add("tool", Tool);
        if (Arguments is not null)
        {
            json["arguments"] = Arguments.DeepClone();
        }

        if (UndeclaredArguments > 0)
        {
            json["undeclared_arguments"] = UndeclaredArguments;
        }

        json["status"] = Status;
        json["result_bytes"] = ResultBytes;
        json["duration_ms"] = (long)Duration.TotalMilliseconds;
        return json.ToJsonString();
    }
}
