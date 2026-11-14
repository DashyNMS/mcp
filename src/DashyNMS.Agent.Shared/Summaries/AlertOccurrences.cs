using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Summaries;

/// <summary>
/// One stretch of a rule firing on a device, in UTC (#76): from its first
/// fault going into alert until its last recovering. Eight cores going into
/// alert together are one occurrence of eight fault events, and one core
/// recovering doesn't end it.
/// </summary>
/// <param name="Started">Null when that's older than the alert log read.</param>
/// <param name="Ended">When the last fault recovered; null while it's open, or when the recovery wasn't logged.</param>
/// <param name="RecoveryUnseen">A legacy alert fired again with no recovery logged in between: it ended, but when isn't known.</param>
public sealed record RuleOccurrence(int DeviceId, int RuleId, DateTime? Started, DateTime? Ended, int FaultCount, bool RecoveryUnseen)
{
    public bool IsOpen => Ended is null && !RecoveryUnseen;
}

/// <summary>
/// Rebuilds occurrences from LibreNMS's alert log with Core's
/// <see cref="AlertHistory"/> - the same rules the apps use - so per-fault
/// rows (LibreNMS 26.10, with <c>fault_id</c>) and legacy rows both read
/// right, and a recovery is matched to its own fault, never another core's.
/// </summary>
public static class AlertOccurrences
{
    /// <summary>Every device and rule's occurrences in <paramref name="entries"/>, oldest first within each.</summary>
    public static IReadOnlyList<RuleOccurrence> From(IEnumerable<AlertLogEntry> entries, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        // Core works in one zone throughout; hand it UTC values marked as
        // zone-less "server local", which it leaves as they are.
        var utc = entries
            .Select(e => (Entry: e, At: Sources.Utc(e.TimeLogged, options)))
            .Where(e => e.At is not null)
            .Select(e => new AlertLogEntry
            {
                Id = e.Entry.Id,
                RuleId = e.Entry.RuleId,
                DeviceId = e.Entry.DeviceId,
                FaultId = e.Entry.FaultId,
                StateValue = e.Entry.StateValue,
                TimeLogged = DateTime.SpecifyKind(e.At!.Value, DateTimeKind.Unspecified),
            })
            .ToList();

        return utc
            .GroupBy(e => (e.DeviceId, e.RuleId))
            .SelectMany(g => AlertHistory.Occurrences(g, g.Key.RuleId, g.Key.DeviceId, serverTimestampsAreUtc: false)
                .Select(o => new RuleOccurrence(
                    g.Key.DeviceId,
                    g.Key.RuleId,
                    AsUtc(o.Started),
                    AsUtc(o.Ended),
                    o.FaultCount,
                    o.RecoveryUnseen)))
            .ToList();
    }

    private static DateTime? AsUtc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
