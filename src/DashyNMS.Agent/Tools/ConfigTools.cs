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

    private DashyNMS.Agent.Unimus.ConfigLookups Operation() => new(unimus, unimusDevices, directory, scopes, redactor, options, time);
    private async Task<JsonObject> BackupAsync(string query, int? backupId, int offset, int limit, CancellationToken cancellationToken)
    {
        unimusDevices.EnsureAvailable();
        Paging.Check(offset, limit, MaxLines);
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await Operation().ReadBackupAsync(device, backupId, offset, limit, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }
    private async Task<JsonObject> DiffAsync(string query, int? fromId, int? toId, CancellationToken cancellationToken)
    {
        unimusDevices.EnsureAvailable();
        var device = await resolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        var result = await Operation().ReadDiffAsync(device, fromId, toId, cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }
    private async Task<JsonObject> ChangesAsync(int? hours, string? since, string? until, string? group, string? location, int offset, int limit, CancellationToken cancellationToken)
    {
        var result = await Operation().ReadChangesAsync(new(hours, since, until, group, location, offset, limit), cancellationToken).ConfigureAwait(false);
        return ToolResult.Ok(result.Summary, result.Data);
    }
}
