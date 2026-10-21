using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Summaries;

/// <summary>
/// One fault in an alert group: what it's about and its own state (#75). On
/// LibreNMS 26.10 a rule firing on eight cores keeps eight of these.
/// </summary>
/// <param name="FaultId">The fault's own ID - what <c>alert_details</c> takes.</param>
/// <param name="Entity">"CPU 0", a port, a location - null for one about the whole device.</param>
/// <param name="EntityType">LibreNMS's entity type ("processor", "port"), when it says.</param>
public sealed record FaultBrief(int FaultId, string? Entity, string? EntityType, long? EntityId, string State, DateTime? Since)
{
    public static FaultBrief From(Alert fault, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return new FaultBrief(
            fault.Id,
            AlertGrouping.EntityName(fault),
            fault.EntityType,
            fault.EntityId,
            fault.State.ToString().ToLowerInvariant(),
            Sources.Utc(fault.FirstSeen ?? fault.Timestamp, options));
    }
}

/// <summary>
/// One rule firing on one device (#75) - Core's <see cref="AlertGroup"/>, as
/// every alert tool reports it, so they count and word groups the same way.
/// Before LibreNMS 26.10 every group is one alert.
/// </summary>
/// <param name="GroupId">"device:7/rule:3" - stable across calls.</param>
/// <param name="AlertId">The first fault's ID (the alert's own ID before 26.10) - what <c>alert_details</c> takes. Not a group ID.</param>
/// <param name="State">active, acknowledged or partly_acknowledged.</param>
/// <param name="Entity">For a group of one, what that fault is about.</param>
/// <param name="Faults">Each fault, for a group of more than one.</param>
public sealed record AlertGroupSummary(
    string GroupId,
    int AlertId,
    int RuleId,
    string Rule,
    string Severity,
    string State,
    int FaultCount,
    int AcknowledgedCount,
    string? Entity,
    DateTime? Since,
    string? Note,
    IReadOnlyList<FaultBrief>? Faults,
    int? FaultsOmitted)
{
    /// <summary>Faults listed per group; the rest are counted in <see cref="FaultsOmitted"/>.</summary>
    public const int MaxFaults = 25;

    public static AlertGroupSummary From(AlertGroup group, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(group);

        var faults = group.IsSingle ? null : group.Faults.Take(MaxFaults).Select(f => FaultBrief.From(f, options)).ToList();
        return new AlertGroupSummary(
            group.Key.ToString(),
            group.Primary.Id,
            group.RuleId,
            group.DisplayRuleName,
            group.Severity.ToString().ToLowerInvariant(),
            StateOf(group),
            group.FaultCount,
            group.AcknowledgedCount,
            group.IsSingle ? AlertGrouping.EntityName(group.Primary) : null,
            Sources.Utc(group.FirstSeen, options),
            string.IsNullOrWhiteSpace(group.Primary.Note) ? null : group.Primary.Note,
            faults,
            group.FaultCount > MaxFaults ? group.FaultCount - MaxFaults : null);
    }

    /// <summary>"partly_acknowledged" when some faults are acknowledged and some aren't - acknowledging one may not have covered the rest.</summary>
    public static string StateOf(AlertGroup group)
        => group.IsPartlyAcknowledged ? "partly_acknowledged" : group.State.ToString().ToLowerInvariant();
}
