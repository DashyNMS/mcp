using System.Text;
using System.Text.Json;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// The tools an MCP client receives, each held byte for byte to <c>Fixtures/tools-list.json</c>.
/// </summary>
/// <remarks>
/// Hosts such as DashyNMS mobile take tool text from the shared catalogue, and MCP's attributes read
/// the same constants (DashyNMS/mcp#130). Moving text into the catalogue must not change a character
/// of what MCP lists, so this compares each tool's raw JSON as the server writes it, escaping and
/// property order included, rather than a parsed form that would forgive a changed escape. Only the
/// order of the tools themselves is normalised, by name: the SDK lists them in no fixed order, and a
/// client doesn't depend on it. When a change to the list is intended, the failure carries the new list
/// as base64: decode it into the fixture and review the diff.
/// </remarks>
internal static class ListedTools
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tools-list.json");

    public static void AssertUnchanged(JsonElement tools)
    {
        var listed = Canonical(tools);
        var expected = File.Exists(FixturePath) ? File.ReadAllText(FixturePath, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') : null;
        if (!string.Equals(expected, listed, StringComparison.Ordinal))
        {
            Assert.Fail(
                "The tools MCP lists differ from Fixtures/tools-list.json. If the change is intended, replace the fixture with this (base64, UTF-8):\n" +
                "TOOLS-LIST-BASE64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(listed + "\n")) + ":END");
        }
    }

    /// <summary>One tool's raw JSON per line, sorted by name, as a JSON array.</summary>
    private static string Canonical(JsonElement tools)
        => "[\n" + string.Join(",\n", tools.EnumerateArray()
            .OrderBy(t => t.GetProperty("name").GetString(), StringComparer.Ordinal)
            .Select(t => t.GetRawText())) + "\n]";
}
