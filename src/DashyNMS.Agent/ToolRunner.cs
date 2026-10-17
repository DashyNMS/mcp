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
        catch (Exception ex)
        {
            result = Describe(tool, ex);
        }

        return ToolResult.Render(result, redactor, options.MaxResultBytes);
    }

    private JsonObject Describe(string tool, Exception ex)
    {
        switch (ex)
        {
            case ToolException expected:
                return ToolResult.Error(expected.Message, expected.Details);

            case LibreNmsApiException libreNms:
                logger.LogWarning(ex, "{Tool}: LibreNMS request failed", tool);
                return ToolResult.Error($"LibreNMS: {libreNms.ToUserMessage()}");

            case UnimusApiException unimus:
                logger.LogWarning(ex, "{Tool}: Unimus request failed", tool);
                return ToolResult.Error($"Unimus: {unimus.ToUserMessage()}");

            case GraylogApiException graylog:
                logger.LogWarning(ex, "{Tool}: Graylog request failed", tool);
                return ToolResult.Error($"Graylog: {graylog.ToUserMessage()}");

            case OperationCanceledException:
                // Not our caller's cancellation, so a request timed out.
                logger.LogWarning(ex, "{Tool}: request timed out", tool);
                return ToolResult.Error("A request timed out. The server may be busy; try again, or ask about less at once.");

            default:
                logger.LogError(ex, "{Tool} failed unexpectedly", tool);
                return ToolResult.Error($"Something went wrong running {tool}. The server log has the details.");
        }
    }
}
