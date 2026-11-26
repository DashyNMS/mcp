using System.ComponentModel;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Help;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>How to use DashyNMS itself, from its user guides (#164). Needs no LibreNMS.</summary>
[McpServerToolType]
public sealed class HelpTools(HelpLibrary library, ToolRunner runner)
{
    public const int DefaultResults = HelpLookup.DefaultResults;
    public const int MaxResults = HelpLookup.MaxResults;

    [McpServerTool(Name = ToolText.DashynmsHelp.Name, Title = ToolText.DashynmsHelp.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.DashynmsHelp.Description)]
    public Task<string> DashynmsHelp(
        [Description(ToolText.DashynmsHelp.Args.Query)] string? query = null,
        [Description(ToolText.DashynmsHelp.Args.Product)] string? product = null,
        [Description(ToolText.DashynmsHelp.Args.Page)] string? page = null,
        [Description(ToolText.DashynmsHelp.Args.Section)] string? section = null,
        [Description(ToolText.DashynmsHelp.Args.Limit)] int limit = DefaultResults,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(
            ToolText.DashynmsHelp.Name,
            token =>
            {
                token.ThrowIfCancellationRequested();

                // For the next question: this one is answered from what's here.
                library.RefreshInBackground();
                var result = new HelpLookup(library, runner.Redactor).Read(new(query, product, page, section, limit));
                return Task.FromResult(ToolResult.Ok(result.Summary, result.Data));
            },
            cancellationToken);
}
