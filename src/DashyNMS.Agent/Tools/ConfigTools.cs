using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using ModelContextProtocol.Server;

namespace DashyNMS.Agent.Tools;

/// <summary>
/// Config backups from Unimus (#31), as desktop's Unimus integration views
/// and diffs them - redacted, capped, and only as much text as the operator
/// allows (#16).
/// </summary>
[McpServerToolType]
public sealed class ConfigTools(
    IUnimusApi unimus,
    UnimusDirectory unimusDevices,
    DeviceDirectory directory,
    DeviceResolver resolver,
    DeviceScope scopes,
    Redactor redactor,
    ToolRunner runner,
    AgentOptions options,
    TimeProvider time)
{
    public const int DefaultLines = 200;
    public const int MaxLines = 1000;
    public const int BackupsListed = 10;

    /// <summary>
    /// Backups are listed this many at a time: Unimus is slow to return big
    /// pages (a page of 50 timed out against a real server), and the newest
    /// few are what's asked for almost always.
    /// </summary>
    public const int BackupPageSize = 10;

    /// <summary>How many pages back a backup ID is looked for.</summary>
    public const int MaxBackupPages = 5;

    public const int BackupSearchDepth = BackupPageSize * MaxBackupPages;

    public const int DefaultChanges = 50;
    public const int MaxChanges = 100;

    /// <summary>Requests to Unimus at once when checking many devices.</summary>
    public const int MaxConcurrency = 8;

    private const string TextHelp =
        "Secrets (passwords, keys, SNMP communities, private keys) are redacted. If the server is set to MCP_CONFIG_TEXT=summary, " +
        "only sizes, times and line counts come back, never config text.";

    [McpServerTool(Name = "config_backup", Title = "Config backup", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "A device's configuration as Unimus backed it up: the newest backup by default, or an earlier one by ID, with the list of recent backups " +
        "(each is a config version: valid_since is when Unimus first saw it, valid_until when it last confirmed it unchanged). " +
        "For \"what's configured on X?\", \"is VLAN 120 on this switch?\" or reading an old version. " + TextHelp + " " +
        "Long configs are paged by line: 200 lines by default, up to 1000; use lines.next_offset for more. To compare versions, use config_diff.")]
    public Task<string> ConfigBackup(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("A backup ID from the backups list. Leave out for the newest.")] int? backup_id = null,
        [Description("How many lines to skip, for paging. Start at 0, then use lines.next_offset.")] int offset = 0,
        [Description("How many lines to return, 1 to 1000. Defaults to 200.")] int limit = DefaultLines,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("config_backup", ct => BackupAsync(device, backup_id, offset, limit, ct), cancellationToken);

    [McpServerTool(Name = "config_diff", Title = "Config diff", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The difference between two of a device's Unimus config backups, as changed lines only (- removed, + added). By default the newest backup " +
        "against the one before it; give from_backup and to_backup (IDs from config_backup's list) for any two. " + TextHelp + " " +
        "Long diffs are cut at 200 lines, with the total counted. For changes in a time window together with events and logs, use what_changed.")]
    public Task<string> ConfigDiffTool(
        [Description("The device: hostname, display name, IP address or LibreNMS device ID.")] string device,
        [Description("The older backup's ID. Leave out (with to_backup) for the one before the newest.")] int? from_backup = null,
        [Description("The newer backup's ID. Leave out for the newest.")] int? to_backup = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("config_diff", ct => DiffAsync(device, from_backup, to_backup, ct), cancellationToken);

    [McpServerTool(Name = "recent_config_changes", Title = "Recent config changes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Every device whose configuration changed in a window of time, according to Unimus, newest change first - for \"what changed in the network " +
        "since Friday?\" or \"has anyone touched the core switches today?\". " + EventTools.WindowHelp + " Filter by device group or location " +
        "(much quicker on a big network: each device is one request to Unimus). Devices Unimus backs up but LibreNMS doesn't know are included when " +
        "there's no filter. This says when each device changed; config_diff on a device shows what changed. Paged: 50 by default, up to 100.")]
    public Task<string> RecentConfigChanges(
        [Description("How many hours to look back from until, 1 to 168. Leave out when using since.")] int? hours = null,
        [Description("Start of the window: a duration before now (90m, 24h, 7d) or an ISO 8601 time.")] string? since = null,
        [Description("End of the window: a duration before now or an ISO 8601 time. Defaults to now.")] string? until = null,
        [Description("Only devices in this LibreNMS device group (by name).")] string? group = null,
        [Description("Only devices at this location (by name).")] string? location = null,
        [Description("How many changes to skip, for paging. Start at 0, then use page.next_offset.")] int offset = 0,
        [Description("How many changes to return, 1 to 100. Defaults to 50.")] int limit = DefaultChanges,
        CancellationToken cancellationToken = default)
        => runner.RunAsync("recent_config_changes", ct => ChangesAsync(hours, since, until, group, location, offset, limit, ct), cancellationToken);

    private async Task<JsonObject> BackupAsync(string query, int? backupId, int offset, int limit, CancellationToken cancellationToken)
    {
        unimusDevices.EnsureAvailable();
        Paging.Check(offset, limit, MaxLines);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var match = await unimusDevices.RequireAsync(device, cancellationToken).ConfigureAwait(false);

        var page = await unimus.GetBackupsAsync(match.Id, 0, BackupPageSize, cancellationToken).ConfigureAwait(false);
        var backups = page.Backups.OrderByDescending(b => b.ValidSince ?? 0).ToList();
        if (backups.Count == 0)
        {
            throw new ToolException("Unimus has no backups of this device yet.");
        }

        var (backup, pageIndex) = backupId is { } id
            ? await FindAsync(match.Id, id, backups, cancellationToken).ConfigureAwait(false)
            : (backups[0], 0);

        var content = backup.Content
            ?? (backup == backups[0] && backupId is null
                ? (await unimus.GetLatestBackupAsync(match.Id, cancellationToken).ConfigureAwait(false))?.Content
                : null)
            ?? await unimus.GetBackupContentAsync(match.Id, backup.Id, pageIndex, BackupPageSize, cancellationToken).ConfigureAwait(false);

        if (content is null)
        {
            throw new ToolException("That backup isn't text (Unimus also keeps binary backups), so there's nothing to show.");
        }

        // Redact the whole text before splitting it into pages, so a private
        // key that crosses a page boundary is still recognised as one block.
        var lines = redactor.Redact(content).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var full = options.ConfigText == ConfigText.Full;
        IReadOnlyList<string> shown;
        PageInfo info;
        if (full)
        {
            shown = Paging.Apply(lines, offset, limit, out info);
        }
        else
        {
            shown = [];
            info = new PageInfo(0, 0, 0, lines.Length, null);
        }

        var data = new
        {
            device = DeviceBrief.From(device),
            unimus_device = new { id = match.Id, address = match.Address, description = match.Description, vendor = match.Vendor, model = match.Model },
            backup = new { id = backup.Id, valid_since = backup.ValidSinceUtc, valid_until = backup.ValidUntilUtc, newest = backup == backups[0] },
            lines = new { total = lines.Length, info.Offset, info.Returned, info.NextOffset },
            config = full ? string.Join('\n', shown) : null,
            config_text = full ? null : "Left out on this server (MCP_CONFIG_TEXT=summary).",
            backups = backups.Take(BackupsListed).Select(b => new { id = b.Id, valid_since = b.ValidSinceUtc, valid_until = b.ValidUntilUtc }).ToList(),
            backups_total = page.TotalCount,
        };

        var which = backup == backups[0] ? "The newest backup" : "An earlier backup";
        var since = backup.ValidSinceUtc is { } at ? $", the config since {at:yyyy-MM-dd HH:mm} UTC" : string.Empty;
        var paging = full && (info.NextOffset is not null || info.Offset > 0) ? " " + Paging.Describe(info, "line", "lines") : string.Empty;
        return ToolResult.Ok($"{which}{since}: {DeviceTools.Count(lines.Length, "line")}.{paging}", data);
    }

    private async Task<JsonObject> DiffAsync(string query, int? fromId, int? toId, CancellationToken cancellationToken)
    {
        unimusDevices.EnsureAvailable();
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var match = await unimusDevices.RequireAsync(device, cancellationToken).ConfigureAwait(false);

        var page = await unimus.GetBackupsAsync(match.Id, 0, BackupPageSize, cancellationToken).ConfigureAwait(false);
        var backups = page.Backups.OrderByDescending(b => b.ValidSince ?? 0).ToList();

        var to = toId is { } t
            ? (await FindAsync(match.Id, t, backups, cancellationToken).ConfigureAwait(false)).Backup
            : backups.FirstOrDefault() ?? throw new ToolException("Unimus has no backups of this device yet.");
        var from = fromId is { } f
            ? (await FindAsync(match.Id, f, backups, cancellationToken).ConfigureAwait(false)).Backup
            : backups.SkipWhile(b => b.Id != to.Id).Skip(1).FirstOrDefault()
              ?? throw new ToolException("There's no earlier backup to compare with: Unimus has only this one version of the config, or the next is further back than one page.");

        if (from.Id == to.Id)
        {
            throw new ToolException("Those are the same backup.");
        }


        if ((from.ValidSince ?? 0) > (to.ValidSince ?? 0))
        {
            (from, to) = (to, from);
        }

        var diff = await unimus.GetDiffAsync(from.Id, to.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new ToolException("Unimus couldn't compare those two backups.");
        var rendered = ConfigDiff.Render(diff);
        var full = options.ConfigText == ConfigText.Full;

        var data = new
        {
            device = DeviceBrief.From(device),
            from = new { id = from.Id, valid_since = from.ValidSinceUtc },
            to = new { id = to.Id, valid_since = to.ValidSinceUtc },
            changed_lines = rendered.Lines,
            lines_removed = rendered.Removed,
            lines_added = rendered.Added,
            config_diff = full ? rendered.Text : null,
            config_diff_lines_omitted = full && rendered.Omitted > 0 ? rendered.Omitted : (int?)null,
            config_text = full ? null : "Left out on this server (MCP_CONFIG_TEXT=summary).",
        };

        var summary = rendered.Lines == 0
            ? "The two backups are the same."
            : $"{DeviceTools.Count(rendered.Removed, "line")} removed and {DeviceTools.Count(rendered.Added, "line")} added between {from.ValidSinceUtc:yyyy-MM-dd HH:mm} and {to.ValidSinceUtc:yyyy-MM-dd HH:mm} UTC." +
              (full && rendered.Omitted > 0 ? $" The diff is cut at {ConfigDiff.MaxLines} lines." : string.Empty);
        return ToolResult.Ok(summary, data);
    }

    /// <summary>
    /// A backup by ID, from the first page already read or the pages after
    /// it, with the page it's on (Unimus fetches a backup's content by page).
    /// </summary>
    private async Task<(UnimusBackup Backup, int Page)> FindAsync(int unimusId, int backupId, IReadOnlyList<UnimusBackup> firstPage, CancellationToken cancellationToken)
    {
        if (firstPage.FirstOrDefault(b => b.Id == backupId) is { } onFirst)
        {
            return (onFirst, 0);
        }

        for (var page = 1; page < MaxBackupPages && firstPage.Count == BackupPageSize; page++)
        {
            var next = await unimus.GetBackupsAsync(unimusId, page, BackupPageSize, cancellationToken).ConfigureAwait(false);
            if (next.Backups.FirstOrDefault(b => b.Id == backupId) is { } found)
            {
                return (found, page);
            }

            if (next.Backups.Count < BackupPageSize)
            {
                break;
            }
        }

        throw new ToolException($"No backup with that ID among this device's {BackupSearchDepth} most recent. config_backup without an ID lists the newest.");
    }

    private async Task<JsonObject> ChangesAsync(int? hours, string? since, string? until, string? group, string? location, int offset, int limit, CancellationToken cancellationToken)
    {
        unimusDevices.EnsureAvailable();
        var window = TimeWindow.Resolve(since, until, hours, EventTools.DefaultHours, time);
        Paging.Check(offset, limit, MaxChanges);
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);

        var librenmsTask = directory.GetAllAsync(cancellationToken);
        var unimusTask = unimusDevices.GetAllAsync(cancellationToken);
        await Task.WhenAll(librenmsTask, unimusTask).ConfigureAwait(false);

        // Each Unimus device to its LibreNMS device, the way desktop matches
        // them; with a filter, only the matched devices inside it are checked.
        var byUnimusId = new Dictionary<int, Device>();
        foreach (var d in librenmsTask.Result.Where(d => scope.Includes(d)))
        {
            if (UnimusDeviceMatcher.Match(unimusTask.Result, UnimusAddressCandidates.ForDevice(d, myDomain: null)) is { } u)
            {
                byUnimusId.TryAdd(u.Id, d);
            }
        }

        var toCheck = scope.IsEverything ? unimusTask.Result : unimusTask.Result.Where(u => byUnimusId.ContainsKey(u.Id)).ToList();

        using var gate = new SemaphoreSlim(MaxConcurrency);
        var checks = await Task.WhenAll(toCheck.Select(async u =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await Sources.TryAsync(() => unimus.GetBackupsAsync(u.Id, 0, 2, cancellationToken), cancellationToken).ConfigureAwait(false);
                return (Device: u, Backups: result.Value?.Backups.OrderByDescending(b => b.ValidSince ?? 0).ToList(), result.Error);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var changes = checks
            .Where(c => c.Backups is { Count: > 0 } b && b[0].ValidSinceUtc is { } began && window.Contains(began))
            .Select(c => new
            {
                device = byUnimusId.TryGetValue(c.Device.Id, out var d) ? DeviceBrief.From(d) : null,
                unimus_device = new { id = c.Device.Id, address = c.Device.Address, description = c.Device.Description },
                changed_at = c.Backups![0].ValidSinceUtc,
                previous_version_since = c.Backups.Count > 1 ? c.Backups[1].ValidSinceUtc : null,
                first_backup = c.Backups.Count == 1 ? true : (bool?)null,
                newest_backup_id = c.Backups[0].Id,
            })
            .OrderByDescending(c => c.changed_at)
            .ToList();

        var failed = checks.Count(c => c.Error is not null);
        var shown = Paging.Apply(changes, offset, limit, out var info);
        var lead = EventTools.Describe(window, time.GetUtcNow().UtcDateTime);
        var summary = $"{lead}: {DeviceTools.Count(changes.Count, "device")} changed config, of {toCheck.Count} checked." +
            (failed > 0 ? $" {DeviceTools.Count(failed, "device")} couldn't be read from Unimus." : string.Empty) +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "change", "changes") : string.Empty);

        return ToolResult.Ok(summary, new
        {
            window = new { since = window.Since, until = window.Until },
            changes = shown,
            page = info,
            unreadable = failed > 0 ? checks.Where(c => c.Error is not null).Take(5).Select(c => new { unimus_device = c.Device.Description ?? c.Device.Address, message = c.Error }).ToList() : null,
        });
    }
}
