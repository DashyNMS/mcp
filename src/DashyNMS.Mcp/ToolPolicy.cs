using DashyNMS.Agent.Catalogue;

namespace DashyNMS.Mcp;

/// <summary>
/// Which tools the server offers, from the operator's allow and deny lists
/// (#37): with an allow list only those, and never one that's denied.
/// </summary>
/// <remarks>
/// A tool that's off is never registered, so it isn't listed and a call to it
/// is a call to a tool that doesn't exist: there's no second check to forget.
/// A name that isn't a tool stops startup. Skipping it would leave a tool the
/// operator meant to turn off, spelt wrongly, on.
/// </remarks>
public sealed class ToolPolicy
{
    private ToolPolicy(IReadOnlyList<string> on, IReadOnlyList<string> off)
    {
        On = on;
        Off = off;
    }

    /// <summary>The tools offered, in the catalogue's order.</summary>
    public IReadOnlyList<string> On { get; }

    /// <summary>The rest, in the catalogue's order.</summary>
    public IReadOnlyList<string> Off { get; }

    /// <summary>
    /// Whether any tool that's on reads LibreNMS. Help doesn't, so a server that
    /// offers nothing else needs no address or token (#164). With no tools on at
    /// all the lists are wrong, and what's missing is reported as always.
    /// </summary>
    public bool NeedsLibreNms => On.Count == 0 || On.Any(tool => tool != ToolText.DashynmsHelp.Name);

    public bool IsOn(string tool) => On.Contains(tool, StringComparer.Ordinal);

    /// <summary>Reads both lists (null when unset), adding a line to <paramref name="problems"/> for anything wrong.</summary>
    public static ToolPolicy Parse(string? allow, string? deny, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var allowed = Names(McpOptions.ToolsAllow, allow, problems);
        var denied = Names(McpOptions.ToolsDeny, deny, problems);
        var on = ToolCatalogue.Names.Where(n => (allowed is null || allowed.Contains(n)) && denied?.Contains(n) != true).ToArray();
        if (on.Length == 0 && problems.Count == 0)
        {
            problems.Add($"{McpOptions.ToolsAllow} and {McpOptions.ToolsDeny} leave no tools on.");
        }

        return new ToolPolicy(on, ToolCatalogue.Names.Except(on, StringComparer.Ordinal).ToArray());
    }

    private static HashSet<string>? Names(string key, string? list, List<string> problems)
    {
        if (list is null)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ToolCatalogue.Names.FirstOrDefault(n => n.Equals(entry, StringComparison.OrdinalIgnoreCase)) is { } name)
            {
                names.Add(name);
            }
            else
            {
                problems.Add($"{key}: '{entry}' isn't a tool. The tools are {string.Join(", ", ToolCatalogue.Names)}.");
            }
        }

        // A list of nothing but commas is a mistake, not "no tools" or "every tool".
        if (names.Count == 0 && problems.Count == 0)
        {
            problems.Add($"{key} has no tool names in it. Separate them with commas, or leave it unset.");
        }

        return names;
    }
}
