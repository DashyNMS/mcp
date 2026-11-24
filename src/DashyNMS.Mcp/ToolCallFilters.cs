using System.Security.Claims;
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

    /// <summary>Who is calling: the label of the token they used over HTTP, or stdio's only client.</summary>
    internal static string ClientOf(ClaimsPrincipal? user)
        => user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? StdioClient;
}
