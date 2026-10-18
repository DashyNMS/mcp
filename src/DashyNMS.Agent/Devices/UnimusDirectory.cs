using DashyNMS.Agent.Redaction;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>
/// Unimus's devices, cached for a minute like LibreNMS's, and matched to a
/// LibreNMS device the way desktop matches them (Core's
/// <see cref="UnimusDeviceMatcher"/> over <see cref="UnimusAddressCandidates"/>).
/// </summary>
public sealed class UnimusDirectory(IUnimusApi unimus, AgentOptions options, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<UnimusDevice>? _devices;
    private DateTimeOffset _fetchedAt;

    /// <summary>
    /// Stops a config tool before it starts when Unimus isn't set up or the
    /// operator has turned config data off (#16), with the reason in words.
    /// </summary>
    public void EnsureAvailable()
    {
        if (options.ConfigText == ConfigText.Off)
        {
            throw new ToolException("Config tools are turned off on this server (MCP_CONFIG_TEXT=off).");
        }

        if (!unimus.IsConfigured)
        {
            throw new ToolException("Unimus isn't configured on this server, so there are no config backups to read. Set UNIMUS_URL and UNIMUS_TOKEN to turn it on.");
        }
    }

    public async Task<IReadOnlyList<UnimusDevice>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } devices)
        {
            return devices;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } fetched)
            {
                return fetched;
            }

            var list = await unimus.ListAllDevicesAsync(cancellationToken).ConfigureAwait(false);
            _devices = list;
            _fetchedAt = time.GetUtcNow();
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The Unimus device for a LibreNMS device, or null when Unimus doesn't back it up.</summary>
    public async Task<UnimusDevice?> MatchAsync(Device device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return UnimusDeviceMatcher.Match(all, UnimusAddressCandidates.ForDevice(device, myDomain: null));
    }

    /// <summary>Like <see cref="MatchAsync"/>, but an error naming the problem when there's no match.</summary>
    public async Task<UnimusDevice> RequireAsync(Device device, CancellationToken cancellationToken)
        => await MatchAsync(device, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException("Unimus doesn't back up this device: no Unimus device has its hostname or address.");

    /// <summary>How long Unimus can go without confirming a config before the backup is called stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// A sentence when the newest backup hasn't been confirmed for a week:
    /// Unimus re-checks a config every few hours, so silence that long
    /// usually means its job for this device is failing (#13's trial found a
    /// core switch six months out of date, with no word of it).
    /// </summary>
    public static string? StaleNote(UnimusBackup? newest, DateTime now)
    {
        if ((newest?.ValidUntilUtc ?? newest?.ValidSinceUtc) is not { } confirmed || now - confirmed < StaleAfter)
        {
            return null;
        }

        var days = (int)(now - confirmed).TotalDays;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Unimus last backed this device up on {confirmed:yyyy-MM-dd}, {days} days ago, so its backup job for it is probably failing and the config may be out of date.");
    }

    private IReadOnlyList<UnimusDevice>? Fresh()
        => _devices is { } devices && time.GetUtcNow() - _fetchedAt < DeviceDirectory.Lifetime ? devices : null;
}

/// <summary>A Unimus backup diff as text, shared by what_changed and config_diff.</summary>
public static class ConfigDiff
{
    public const int MaxLines = 200;

    /// <summary>The changed lines as a unified-style diff: "- old", "+ new", common lines left out.</summary>
    /// <remarks>
    /// Private keys are blanked line by line here, before anything is cut,
    /// following each side of the diff through the unchanged lines too: a
    /// rotated key's diff holds only its body lines (BEGIN and END are
    /// unchanged), and a long one can be cut between BEGIN and END - either
    /// way the redactor would never see a whole block.
    /// <para>
    /// Unimus sends some lines - blank ones, on a real pfSense diff - as
    /// "text": null, which overwrites the model's empty default, so a missing
    /// line is read as an empty one.
    /// </para>
    /// </remarks>
    public static (string Text, int Lines, int Removed, int Added, int Omitted) Render(UnimusBackupDiff diff, int maxLines = MaxLines)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var lines = new List<string>();
        int removed = 0, added = 0;
        bool originalInKey = false, revisedInKey = false;
        foreach (var group in diff.LineGroups)
        {
            if (group.IsCommon)
            {
                foreach (var line in group.OriginalLines)
                {
                    Redactor.MaskPrivateKeyLine(line.Text ?? string.Empty, ref originalInKey);
                }

                foreach (var line in group.RevisedLines.Count > 0 ? group.RevisedLines : group.OriginalLines)
                {
                    Redactor.MaskPrivateKeyLine(line.Text ?? string.Empty, ref revisedInKey);
                }

                continue;
            }

            foreach (var line in group.OriginalLines)
            {
                lines.Add("- " + Redactor.MaskPrivateKeyLine(line.Text ?? string.Empty, ref originalInKey));
                removed++;
            }

            foreach (var line in group.RevisedLines)
            {
                lines.Add("+ " + Redactor.MaskPrivateKeyLine(line.Text ?? string.Empty, ref revisedInKey));
                added++;
            }
        }

        var shown = ToolResult.Take(lines, maxLines, out var omitted);
        return (string.Join('\n', shown), lines.Count, removed, added, omitted);
    }
}
