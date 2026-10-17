using System.Reflection;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// Names the assembly the host scans for <c>[McpServerToolType]</c> classes,
/// so the host doesn't have to name a particular tool class to find them all.
/// </summary>
public static class ToolsAssembly
{
    public static Assembly Assembly => typeof(ToolsAssembly).Assembly;
}
