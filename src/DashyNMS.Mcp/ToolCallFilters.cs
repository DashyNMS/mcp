using System.Security.Claims;
using System.Text.Json;
using DashyNMS.Agent;
using DashyNMS.Agent.Redaction;
using DashyNMS.Mcp.Auditing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DashyNMS.Mcp;

/// <summary>What every tool call goes through on its way to a tool, whichever transport it came over.</summary>
public static class ToolCallFilters
{
    /// <summary>The name stdio's only client goes by, where HTTP has the token's label.</summary>
    internal const string StdioClient = "stdio";

    /// <summary>Refuses calls over the limit with a result the model can read, so it waits rather than retrying at once.</summary>
    public static IMcpServerBuilder WithRateLimit(this IMcpServerBuilder builder, ToolCallRateLimiter limiter, Redactor redactor, int maxResultBytes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limiter);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            if (limiter.TryCall(ClientOf(context.User), out var retryAfter))
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            context.Items[ToolCallAudit.RateLimitedItem] = true;
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            var error = ToolResult.Error(
                $"Too many tool calls: the limit is {limiter.CallsPerMinute} a minute. Wait {seconds} seconds before calling again.",
                new { retry_after_seconds = seconds });
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = ToolResult.Render(error, redactor, maxResultBytes) }],
            };
        }));
    }

    /// <summary>
    /// Returns each result's JSON as structured content too (#40), which a tool
    /// with an output schema owes its clients. The text stays as it was, for the
    /// ones that only read that.
    /// </summary>
    /// <remarks>Inside the audit and outside everything that answers for a tool, so the refusals have it as well.</remarks>
    public static IMcpServerBuilder WithStructuredContent(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            if (result.Content.OfType<TextContentBlock>().FirstOrDefault() is { } text)
            {
                // Over whatever is there: seeing an output schema, the SDK makes
                // the text a tool returned into structured content as one string.
                result.StructuredContent = Parse(text.Text);
            }

            return result;
        }));
    }

    /// <summary>The object in <paramref name="text"/>, or null for the SDK's own refusals, which are sentences.</summary>
    private static JsonElement? Parse(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object ? json.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Who is calling: the label of the token they used over HTTP, or stdio's only client.</summary>
    internal static string ClientOf(ClaimsPrincipal? user)
        => user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? StdioClient;
}
