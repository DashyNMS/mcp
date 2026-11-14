using DashyNMS.Agent.Devices;
using DashyNMS.Agent.Events;
using DashyNMS.Agent.Redaction;
using DashyNMS.Agent.Summaries;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Unimus;

public sealed record ConfigBackupRequest(int DeviceId, int? BackupId = null, int Offset = 0, int Limit = 200);
public sealed record ConfigDiffRequest(int DeviceId, int? FromBackup = null, int? ToBackup = null);
public sealed record RecentConfigChangesRequest(int? Hours = null, string? Since = null, string? Until = null,
    string? Group = null, string? Location = null, int Offset = 0, int Limit = 50);
public sealed record UnimusDeviceInfo(int Id, string? Address, string? Description, string? Vendor = null, string? Model = null);
public sealed record BackupVersion(int Id, DateTime? ValidSince, DateTime? ValidUntil = null, bool? Newest = null);
public sealed record BackupLinePage(int Total, int Offset, int Returned, int? NextOffset);
public sealed record ConfigBackupData(DeviceBrief Device, UnimusDeviceInfo UnimusDevice, BackupVersion Backup,
    BackupLinePage Lines, string? Config, string? ConfigText, IReadOnlyList<BackupVersion> Backups, int BackupsTotal);
public sealed record ConfigBackupResult(string Summary, ConfigBackupData Data);
public sealed record ConfigDiffData(DeviceBrief Device, BackupVersion From, BackupVersion To, int ChangedLines,
    int LinesRemoved, int LinesAdded, string? ConfigDiff, int? ConfigDiffLinesOmitted, string? ConfigText);
public sealed record ConfigDiffResult(string Summary, ConfigDiffData Data);
public sealed record ConfigChangeWindow(DateTime Since, DateTime Until);
public sealed record RecentConfigChange(DeviceBrief? Device, UnimusDeviceInfo UnimusDevice, DateTime? ChangedAt,
    DateTime? PreviousVersionSince, bool? FirstBackup, int NewestBackupId);
public sealed record UnreadableConfig(string? UnimusDevice, string? Message);
public sealed record RecentConfigChangesData(ConfigChangeWindow Window, IReadOnlyList<RecentConfigChange> Changes,
    PageInfo Page, IReadOnlyList<UnreadableConfig>? Unreadable);
public sealed record RecentConfigChangesResult(string Summary, RecentConfigChangesData Data, int DevicesChecked,
    int UnreadableDevices, int UnreadableOmitted);

/// <summary>Read-only config backups, diffs and fleet changes shared with MCP.</summary>
public sealed class ConfigLookups(IUnimusApi unimus, UnimusDirectory unimusDevices, DeviceDirectory directory,
    DeviceScope scopes, Redactor redactor, AgentOptions options, TimeProvider time)
{
    public const int MaxLines = 1000;
    public const int BackupsListed = 10;
    public const int BackupPageSize = 10;
    public const int MaxBackupPages = 5;
    public const int BackupSearchDepth = BackupPageSize * MaxBackupPages;
    public const int MaxChanges = 100;
    public const int MaxConcurrency = 8;
    public static ConfigLookups Create(ILibreNmsClient client, IUnimusApi unimus, TimeProvider time, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(unimus);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        var directory = new DeviceDirectory(client, time);
        return new(unimus, new UnimusDirectory(unimus, options, time), directory, new DeviceScope(client, directory, time), new Redactor(), options, time);
    }
    public async Task<ConfigBackupResult> ReadBackupAsync(ConfigBackupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        unimusDevices.EnsureAvailable();
        Paging.Check(request.Offset, request.Limit, MaxLines);
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be positive.");
        var device = await DeviceAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        return await ReadBackupAsync(device, request.BackupId, request.Offset, request.Limit, cancellationToken).ConfigureAwait(false);
    }
    public async Task<ConfigDiffResult> ReadDiffAsync(ConfigDiffRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        unimusDevices.EnsureAvailable();
        if (request.DeviceId <= 0) throw new ToolException("A device ID must be positive.");
        var device = await DeviceAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        return await ReadDiffAsync(device, request.FromBackup, request.ToBackup, cancellationToken).ConfigureAwait(false);
    }
    private async Task<Device> DeviceAsync(int id, CancellationToken token)
        => (await directory.GetAllAsync(token).ConfigureAwait(false)).FirstOrDefault(d => d.DeviceId == id) ?? throw new DeviceNotFoundException();

    internal async Task<ConfigBackupResult> ReadBackupAsync(Device device, int? backupId, int offset, int limit, CancellationToken cancellationToken)
    {
        unimusDevices.EnsureAvailable();
        Paging.Check(offset, limit, MaxLines);
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

        cancellationToken.ThrowIfCancellationRequested();
        var data = new ConfigBackupData(Brief(device), Info(match, fullDetails: true),
            new BackupVersion(backup.Id, backup.ValidSinceUtc, backup.ValidUntilUtc, backup == backups[0]),
            new BackupLinePage(lines.Length, info.Offset, info.Returned, info.NextOffset), full ? string.Join('\n', shown) : null,
            full ? null : "Left out on this server (MCP_CONFIG_TEXT=summary).",
            backups.Take(BackupsListed).Select(b => new BackupVersion(b.Id, b.ValidSinceUtc, b.ValidUntilUtc)).ToList(), page.TotalCount);

        var which = backup == backups[0] ? "The newest backup" : "An earlier backup";
        var since = backup.ValidSinceUtc is { } at ? $", the config since {at:yyyy-MM-dd HH:mm} UTC" : string.Empty;
        var paging = full && (info.NextOffset is not null || info.Offset > 0) ? " " + Paging.Describe(info, "line", "lines") : string.Empty;
        var stale = UnimusDirectory.StaleNote(backups[0], time.GetUtcNow().UtcDateTime) is { } note ? " " + note : string.Empty;
        return new($"{which}{since}: {DeviceHealth.Count(lines.Length, "line")}.{paging}{stale}", data);
    }

    internal async Task<ConfigDiffResult> ReadDiffAsync(Device device, int? fromId, int? toId, CancellationToken cancellationToken)
    {
        unimusDevices.EnsureAvailable();
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

        cancellationToken.ThrowIfCancellationRequested();
        var data = new ConfigDiffData(Brief(device), new BackupVersion(from.Id, from.ValidSinceUtc), new BackupVersion(to.Id, to.ValidSinceUtc),
            rendered.Lines, rendered.Removed, rendered.Added, full ? redactor.Redact(rendered.Text) : null,
            full && rendered.Omitted > 0 ? rendered.Omitted : null, full ? null : "Left out on this server (MCP_CONFIG_TEXT=summary).");

        var summary = rendered.Lines == 0
            ? "The two backups are the same."
            : $"{DeviceHealth.Count(rendered.Removed, "line")} removed and {DeviceHealth.Count(rendered.Added, "line")} added between {from.ValidSinceUtc:yyyy-MM-dd HH:mm} and {to.ValidSinceUtc:yyyy-MM-dd HH:mm} UTC." +
              (full && rendered.Omitted > 0 ? $" The diff is cut at {ConfigDiff.MaxLines} lines." : string.Empty);
        return new(summary, data);
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

    public async Task<RecentConfigChangesResult> ReadChangesAsync(RecentConfigChangesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (hours, since, until, group, location, offset, limit) = request;
        unimusDevices.EnsureAvailable();
        var window = TimeWindow.Resolve(since, until, hours, DeviceEventHistory.DefaultHours, time);
        Paging.Check(offset, limit, MaxChanges);
        var scope = await scopes.ResolveAsync(group, location, cancellationToken).ConfigureAwait(false);

        var librenmsTask = directory.GetAllAsync(cancellationToken);
        var unimusTask = unimusDevices.GetAllAsync(cancellationToken);
        await Task.WhenAll(librenmsTask, unimusTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

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

        cancellationToken.ThrowIfCancellationRequested();
        var changes = checks
            .Where(c => c.Backups is { Count: > 0 } b && b[0].ValidSinceUtc is { } began && window.Contains(began))
            .Select(c => new RecentConfigChange(byUnimusId.TryGetValue(c.Device.Id, out var d) ? Brief(d) : null,
                Info(c.Device, fullDetails: false), c.Backups![0].ValidSinceUtc, c.Backups.Count > 1 ? c.Backups[1].ValidSinceUtc : null,
                c.Backups.Count == 1 ? true : null, c.Backups[0].Id))
            .OrderByDescending(c => c.ChangedAt)
            .ToList();

        var failed = checks.Count(c => c.Error is not null);
        var shown = Paging.Apply(changes, offset, limit, out var info);
        var lead = EventWindowText.Describe(window, time.GetUtcNow().UtcDateTime);
        var summary = $"{lead}: {DeviceHealth.Count(changes.Count, "device")} changed config, of {toCheck.Count} checked." +
            (failed > 0 ? $" {DeviceHealth.Count(failed, "device")} couldn't be read from Unimus." : string.Empty) +
            (info.NextOffset is not null || info.Offset > 0 ? " " + Paging.Describe(info, "change", "changes") : string.Empty);

        return new(summary, new RecentConfigChangesData(new(window.Since, window.Until), shown, info,
            failed > 0 ? checks.Where(c => c.Error is not null).Take(5).Select(c => new UnreadableConfig(Clean(c.Device.Description ?? c.Device.Address), c.Error)).ToList() : null),
            toCheck.Count, failed, Math.Max(0, failed - 5));
    }
    private string? Clean(string? value) => value is null ? null : redactor.Redact(value);
    private DeviceBrief Brief(Device device) => DeviceBrief.FromSummary(DeviceSummary.Redacted(device, options, redactor));
    private UnimusDeviceInfo Info(UnimusDevice device, bool fullDetails) => new(device.Id, Clean(device.Address), Clean(device.Description),
        fullDetails ? Clean(device.Vendor) : null, fullDetails ? Clean(device.Model) : null);
}
