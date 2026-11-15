using System.Text.Json;

namespace DashyNMS.Mcp.Tests;

/// <summary>
/// The catalogue against the recorded tool list. <see cref="StdioSmokeTests"/> holds that record to what
/// the running server lists, byte for byte, and checks the live list against the catalogue too.
/// </summary>
public sealed class ToolCatalogueTests
{
    private static JsonElement Recorded()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tools-list.json")));
        return json.RootElement.Clone();
    }

    [Fact]
    public void The_listed_tools_are_the_catalogue() => CatalogueMatch.AssertEqual(Recorded());

    [Fact]
    public void Documented_values_ranges_and_defaults_agree_with_the_descriptions() => CatalogueMatch.AssertDescriptionsAgree();
}
