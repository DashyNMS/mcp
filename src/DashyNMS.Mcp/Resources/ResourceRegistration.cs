using DashyNMS.Mcp.Auditing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DashyNMS.Mcp.Resources;

public static class ResourceRegistration
{
    /// <summary>From this revision a resource that isn't there is a bad parameter, not an error of its own.</summary>
    private const string InvalidParamsFrom = "2026-07-28";

    /// <summary>Serves the files in <paramref name="directory"/> as resources (#20).</summary>
    public static IMcpServerBuilder WithResourceFolder(this IMcpServerBuilder builder, string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(directory);
        builder.Services.AddSingleton(provider => new ResourceFolder(
            directory,
            provider.GetRequiredService<AuditRedactor>(),
            provider.GetRequiredService<ILogger<ResourceFolder>>()));
        return builder
            .WithListResourcesHandler((context, _) => ValueTask.FromResult(context.Services!.GetRequiredService<ResourceFolder>().List()))
            .WithReadResourceHandler((context, _) =>
            {
                var folder = context.Services!.GetRequiredService<ResourceFolder>();
                return folder.Read(context.Params?.Uri ?? string.Empty, ToolCallFilters.ClientOf(context.User)) is { } result
                    ? ValueTask.FromResult(result)
                    : throw new McpProtocolException(
                        "There's no such resource. List the resources for the ones there are.",
                        string.CompareOrdinal(context.Server.NegotiatedProtocolVersion, InvalidParamsFrom) >= 0 ? McpErrorCode.InvalidParams : McpErrorCode.ResourceNotFound);
            });
    }
}
