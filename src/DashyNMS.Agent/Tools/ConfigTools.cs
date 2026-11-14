using System.ComponentModel;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Catalogue;
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

    [McpServerTool(Name = ToolText.ConfigBackup.Name, Title = ToolText.ConfigBackup.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.ConfigBackup.Description)]
    public Task<string> ConfigBackup(
        [Description(ToolText.ConfigBackup.Args.Device)] string device,
        [Description(ToolText.ConfigBackup.Args.BackupId)] int? backup_id = null,
        [Description(ToolText.ConfigBackup.Args.Offset)] int offset = 0,
        [Description(ToolText.ConfigBackup.Args.Limit)] int limit = DefaultLines,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.ConfigBackup.Name, ct => BackupAsync(device, backup_id, offset, limit, ct), cancellationToken);

    [McpServerTool(Name = ToolText.ConfigDiff.Name, Title = ToolText.ConfigDiff.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.ConfigDiff.Description)]
    public Task<string> ConfigDiffTool(
        [Description(ToolText.ConfigDiff.Args.Device)] string device,
        [Description(ToolText.ConfigDiff.Args.FromBackup)] int? from_backup = null,
        [Description(ToolText.ConfigDiff.Args.ToBackup)] int? to_backup = null,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.ConfigDiff.Name, ct => DiffAsync(device, from_backup, to_backup, ct), cancellationToken);

    [McpServerTool(Name = ToolText.RecentConfigChanges.Name, Title = ToolText.RecentConfigChanges.Title, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ToolText.RecentConfigChanges.Description)]
    public Task<string> RecentConfigChanges(
        [Description(ToolText.RecentConfigChanges.Args.Hours)] int? hours = null,
        [Description(ToolText.RecentConfigChanges.Args.Since)] string? since = null,
        [Description(ToolText.RecentConfigChanges.Args.Until)] string? until = null,
        [Description(ToolText.RecentConfigChanges.Args.Group)] string? group = null,
        [Description(ToolText.RecentConfigChanges.Args.Location)] string? location = null,
        [Description(ToolText.RecentConfigChanges.Args.Offset)] int offset = 0,
        [Description(ToolText.RecentConfigChanges.Args.Limit)] int limit = DefaultChanges,
        CancellationToken cancellationToken = default)
        => runner.RunAsync(ToolText.RecentConfigChanges.Name, ct => ChangesAsync(hours, since, until, group, location, offset, limit, ct), cancellationToken);

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
        var stale = UnimusDirectory.StaleNote(backups[0], time.GetUtcNow().UtcDateTime) is { } note ? " " + note : string.Empty;
        return ToolResult.Ok($"{which}{since}: {DeviceTools.Count(lines.Length, "line")}.{paging}{stale}", data);
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
