using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DashyNMS.Agent.Devices;

/// <summary>
/// Reads one of LibreNMS's logs back over a window, however busy it is, or
/// says how far it got and why it stopped (#70).
/// </summary>
/// <remarks>
/// <para>
/// LibreNMS returns the newest entries, optionally at or before a time
/// (DashyNMS/core#101), so this goes back a page at a time from the window's
/// end. The time is an entry's own, so the server's time zone doesn't come into
/// it; entries at that second come back again and are matched by ID. A log
/// that can't be asked that way - a host's own bounded wrapper, or a server
/// that ignores the time - is read as it was before, by
/// <see cref="DeviceTimeline.ReadBackAsync"/>.
/// </para>
/// <para>
/// Entries that don't match the question's filters aren't kept, so a filtered
/// question ("reboots this week") can look through far more of the log than an
/// unfiltered one returns.
/// </para>
/// </remarks>
internal sealed class LogReads(AgentOptions options, TimeProvider time)
{
    public const int FirstPage = 200;
    public const int EventPage = 2000;

    /// <summary>Alert log entries carry what each alert matched, so they're much bigger.</summary>
    public const int AlertPage = 1000;

    /// <summary>The most asked for at once, when more entries than a page share one second.</summary>
    public const int MaxPage = 16_000;

    /// <summary>Entries kept for an answer. An unfiltered question on a busy network stops here.</summary>
    public const int MaxKept = 10_000;

    /// <summary>Entries looked at, kept or not.</summary>
    public const int MaxRead = 100_000;

    /// <summary>When to stop starting another page. Inside the minute most clients give a tool call.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>Why a log wasn't read back to the window's start.</summary>
    public const string ReadLimit = "read_limit";
    public const string TimeLimit = "time_limit";
    public const string SourceFailed = "source_failed";

    /// <summary>A window ending this recently is read from the newest entry: there's little to skip.</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromHours(1);

    /// <param name="keep">The entries worth keeping; null for every one.</param>
    public Task<Fetched<DeviceTimeline.ReadBack<EventLogEntry>>> EventsAsync(
        ILogsApi logs, int? deviceId, TimeWindow window, Func<EventLogEntry, bool>? keep, CancellationToken cancellationToken)
        => ReadAsync(
            depth => logs.ListEventLogAsync(deviceId, depth, cancellationToken),
            logs.CanReadUpTo ? (upTo, limit) => logs.ListEventLogUpToAsync(deviceId, upTo, limit, cancellationToken) : null,
            e => e.Id,
            e => e.Timestamp,
            EventPage,
            window,
            // Each event stands alone, so none after the window is needed.
            Start(window),
            keep,
            cancellationToken);

    /// <summary>From the newest entry, as a recovery after the window says how an alert in it ended.</summary>
    public Task<Fetched<DeviceTimeline.ReadBack<AlertLogEntry>>> AlertsAsync(
        ILogsApi logs, int? deviceId, TimeWindow window, Func<AlertLogEntry, bool>? keep, CancellationToken cancellationToken)
        => ReadAsync(
            depth => logs.ListAlertLogAsync(deviceId, depth, cancellationToken),
            logs.CanReadUpTo ? (upTo, limit) => logs.ListAlertLogUpToAsync(deviceId, upTo, limit, cancellationToken) : null,
            a => a.Id,
            a => a.TimeLogged,
            AlertPage,
            window,
            start: null,
            keep,
            cancellationToken);

    /// <summary>Where to start for a window that ended a while ago, in the server's terms and never before the window's end.</summary>
    private DateTime? Start(TimeWindow window)
    {
        if (window.Until >= time.GetUtcNow().UtcDateTime - Recent)
        {
            return null;
        }

        // A local time may be one of two when the clocks go back, so allow for both.
        return options.ServerTimestampsAreUtc
            ? DateTime.SpecifyKind(window.Until.AddMinutes(1), DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(window.Until.ToLocalTime().AddHours(2), DateTimeKind.Unspecified);
    }

    private Task<Fetched<DeviceTimeline.ReadBack<T>>> ReadAsync<T>(
        Func<int, Task<IReadOnlyList<T>>> newest,
        Func<DateTime, int, Task<IReadOnlyList<T>>>? upTo,
        Func<T, int> idOf,
        Func<T, DateTime?> timeOf,
        int pageSize,
        TimeWindow window,
        DateTime? start,
        Func<T, bool>? keep,
        CancellationToken cancellationToken)
        => upTo is null
            ? DeviceTimeline.ReadBackAsync(newest, e => Sources.Utc(timeOf(e), options), window, cancellationToken)
            : Sources.TryAsync(
                async () => await PagesAsync(newest, upTo, idOf, timeOf, pageSize, window, start, keep, cancellationToken).ConfigureAwait(false)
                    ?? await DeviceTimeline.ReadBackCoreAsync(newest, e => Sources.Utc(timeOf(e), options), window).ConfigureAwait(false),
                cancellationToken);

    /// <summary>Null when the server ignores the time it's given.</summary>
    private async Task<DeviceTimeline.ReadBack<T>?> PagesAsync<T>(
        Func<int, Task<IReadOnlyList<T>>> newest,
        Func<DateTime, int, Task<IReadOnlyList<T>>> upTo,
        Func<T, int> idOf,
        Func<T, DateTime?> timeOf,
        int pageSize,
        TimeWindow window,
        DateTime? start,
        Func<T, bool>? keep,
        CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var kept = new List<T>();
        var seen = new HashSet<int>();
        var read = 0;
        var cursor = start;
        var limit = FirstPage;
        DateTime? reached = null;

        // Entries at the second reached may be missing, so it's covered from the next.
        DeviceTimeline.ReadBack<T> Stopped(string why) => new(kept, reached?.AddSeconds(1) ?? window.Until) { Stopped = why };

        while (true)
        {
            IReadOnlyList<T> page;
            try
            {
                page = cursor is { } back ? await upTo(back, limit).ConfigureAwait(false) : await newest(limit).ConfigureAwait(false);
            }
            catch (Exception ex) when (read > 0 && !cancellationToken.IsCancellationRequested && ToolErrors.MessageFor(ex) is not null)
            {
                // What was read still stands.
                return Stopped(SourceFailed);
            }

            if (cursor is { } asked && page.Any(e => timeOf(e) is { } at && at >= asked.AddSeconds(1)))
            {
                return null;
            }

            var fresh = page.Where(e => idOf(e) == 0 || seen.Add(idOf(e))).ToList();
            read += fresh.Count;
            kept.AddRange(keep is null ? fresh : fresh.Where(keep));
            if (page.Count < limit)
            {
                return new(kept, DateTime.MinValue);
            }

            var oldest = page.Select(timeOf).Where(t => t is not null).Min();
            reached = Sources.Utc(oldest, options);
            if (reached < window.Since)
            {
                return new(kept, reached.Value);
            }

            if (oldest is null || read >= MaxRead || kept.Count >= MaxKept)
            {
                return Stopped(ReadLimit);
            }

            if (time.GetElapsedTime(started) >= Budget)
            {
                return Stopped(TimeLimit);
            }

            // A page all of one second says nothing about what's before it.
            if (fresh.Count == 0 || cursor == oldest)
            {
                if (limit >= MaxPage)
                {
                    return Stopped(ReadLimit);
                }

                limit = Math.Min(limit * 4, MaxPage);
            }
            else
            {
                limit = pageSize;
            }

            cursor = oldest;
        }
    }
}
