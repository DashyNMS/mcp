using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Summaries;

/// <summary>One open alert, without the device (callers group or name that themselves).</summary>
public sealed record AlertSummary(int AlertId, string Rule, string Severity, string State, DateTime? Since, string? Note)
{
    public static AlertSummary From(Alert alert, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new AlertSummary(
            alert.Id,
            alert.DisplayRuleName,
            alert.Severity.ToString().ToLowerInvariant(),
            alert.State.ToString().ToLowerInvariant(),
            Sources.Utc(alert.Timestamp, options),
            string.IsNullOrWhiteSpace(alert.Note) ? null : alert.Note);
    }
}

/// <summary>A port worth a look: down while enabled, or counting errors.</summary>
public sealed record PortProblem(int PortId, string? IfName, string? Alias, string? OperStatus, double? InErrorsPerSec, double? OutErrorsPerSec)
{
    public static PortProblem From(Port port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return new PortProblem(
            port.PortId,
            port.IfName ?? port.IfDescr,
            string.IsNullOrWhiteSpace(port.IfAlias) || port.IfAlias == port.IfName ? null : port.IfAlias,
            port.IfOperStatus,
            Round(port.IfInErrorsRate),
            Round(port.IfOutErrorsRate));
    }

    internal static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;
}
