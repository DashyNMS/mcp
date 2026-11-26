using System.Text.Json;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Outputs;

namespace DashyNMS.Agent.Tests;

/// <summary>Holds every result a tool returns in a test to that tool's output schema (#40).</summary>
internal static class OutputContract
{
    public static void Check(string tool, string json)
    {
        // Not a tool: a test of the runner itself.
        if (!ToolCatalogue.Names.Contains(tool))
        {
            return;
        }

        using var result = JsonDocument.Parse(json);
        var problems = JsonSchemaCheck.Problems(ToolOutputSchema.For(tool), result.RootElement);
        if (problems.Count > 0)
        {
            Assert.Fail($"{tool} doesn't fit its output schema:\n{string.Join('\n', problems)}\n{json}");
        }

        // What the one flat envelope can't say: which half of it goes with which status.
        var ok = result.RootElement.GetProperty("status").GetString() == "ok";
        Assert.Equal(ok, result.RootElement.TryGetProperty("summary", out _));
        Assert.Equal(ok, result.RootElement.TryGetProperty("data", out _));
        Assert.Equal(!ok, result.RootElement.TryGetProperty("message", out _));
    }
}
