using System.Text.Json.Nodes;
using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging;

namespace DashyNMS.Agent;

/// <summary>
/// Runs a tool's body and turns whatever happens into a <see cref="ToolResult"/>:
/// redacted, capped, and with failures as one plain sentence.
/// </summary>
/// <remarks>
/// Never a stack trace, a raw HTTP body or a URL with a token: each client's
/// own exception is mapped through its <c>ToUserMessage()</c>, which Core
/// already writes for people, and anything unexpected becomes a generic
/// line with the detail in the server log.
/// </remarks>
public sealed class ToolRunner(Redactor redactor, AgentOptions options, ILogger<ToolRunner> logger)
{
    public async Task<string> RunAsync(string tool, Func<CancellationToken, Task<JsonObject>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonObject result;
        try
        {
            result = await body(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ToolException expected)
        {
            result = ToolResult.Error(expected.Message, expected.Details);
        }
        catch (Exception ex)
        {
            if (ToolErrors.MessageFor(ex) is { } message)
            {
                logger.LogWarning(ex, "{Tool}: request failed", tool);
                result = ToolResult.Error(message);
            }
            else
            {
                logger.LogError(ex, "{Tool} failed unexpectedly", tool);
                result = ToolResult.Error($"Something went wrong running {tool}. The server log has the details.");
            }
        }

        return ToolResult.Render(result, redactor, options.MaxResultBytes);
    }
}

/// <summary>Plain-English messages for the failures a tool expects from its sources.</summary>
public static class ToolErrors
{
    /// <summary>The message for an expected failure, or null for one that's a bug.</summary>
    public static string? MessageFor(Exception ex) => ex switch
    {
        ToolException expected => expected.Message,
        LibreNmsApiException libreNms => $"LibreNMS: {libreNms.ToUserMessage()}",
        UnimusApiException unimus => $"Unimus: {unimus.ToUserMessage()}",
        GraylogApiException graylog => $"Graylog: {graylog.ToUserMessage()}",

        // Not the caller's cancellation (that propagates), so a request timed out.
        OperationCanceledException => "A request timed out. The server may be busy; try again, or ask about less at once.",
        _ => null,
    };
}
