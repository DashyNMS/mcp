using DashyNMS.Agent.Devices;
using Microsoft.Extensions.Time.Testing;

namespace DashyNMS.Agent.Tests;

public sealed class PagingAndWindowTests
{
    private static readonly int[] Items = Enumerable.Range(1, 38).ToArray();
    private readonly FakeTimeProvider _time = new(FakeLibreNms.Now);

    [Fact]
    public void First_page_points_at_the_next()
    {
        var page = Paging.Apply(Items, 0, 10, out var info);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], page);
        Assert.Equal(new PageInfo(0, 10, 10, 38, 10), info);
        Assert.Equal("Showing 1–10 of 38 devices; pass offset 10 for the next page.", Paging.Describe(info, "device", "devices"));
    }

    [Fact]
    public void Last_page_has_no_next()
    {
        var page = Paging.Apply(Items, 30, 10, out var info);

        Assert.Equal(8, page.Count);
        Assert.Null(info.NextOffset);
        Assert.Equal("Showing 31–38 of 38 devices (the last page).", Paging.Describe(info, "device", "devices"));
    }

    [Fact]
    public void Everything_on_one_page_says_so()
    {
        Paging.Apply(Items, 0, 50, out var info);

        Assert.Equal("All 38 devices.", Paging.Describe(info, "device", "devices"));
    }

    [Fact]
    public void Offset_past_the_end_says_where_to_start()
    {
        Paging.Apply(Items, 100, 10, out var info);

        Assert.StartsWith("There are 38 devices, none at offset 100.", Paging.Describe(info, "device", "devices"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 51)]
    public void Bad_offsets_and_limits_are_errors(int offset, int limit)
        => Assert.Throws<ToolException>(() => Paging.Check(offset, limit, 50));

    [Fact]
    public void Default_window_is_the_last_24_hours()
    {
        var window = TimeWindow.Resolve(null, null, null, 24, _time);

        Assert.Equal(FakeLibreNms.Now.UtcDateTime.AddHours(-24), window.Since);
        Assert.Equal(FakeLibreNms.Now.UtcDateTime, window.Until);
    }

    [Theory]
    [InlineData("90m", -1.5)]
    [InlineData("6h", -6)]
    [InlineData("2d", -48)]
    [InlineData("1w", -168)]
    [InlineData("3 hours", -3)]
    public void Relative_since(string since, double hoursBefore)
        => Assert.Equal(FakeLibreNms.Now.UtcDateTime.AddHours(hoursBefore), TimeWindow.Resolve(since, null, null, 24, _time).Since);

    [Fact]
    public void Absolute_window_in_utc()
    {
        var window = TimeWindow.Resolve("2026-10-17T02:00:00Z", "2026-10-17T04:00:00+01:00", null, 24, _time);

        Assert.Equal(new DateTime(2026, 10, 17, 2, 0, 0, DateTimeKind.Utc), window.Since);
        Assert.Equal(new DateTime(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc), window.Until);
    }

    [Fact]
    public void A_time_without_a_zone_is_utc()
        => Assert.Equal(new DateTime(2026, 10, 17, 2, 0, 0, DateTimeKind.Utc), TimeWindow.Resolve("2026-10-17 02:00", null, null, 24, _time).Since);

    [Fact]
    public void Hours_count_back_from_until()
    {
        var window = TimeWindow.Resolve(null, "2026-10-17T04:00:00Z", 2, 24, _time);

        Assert.Equal(new DateTime(2026, 10, 17, 2, 0, 0, DateTimeKind.Utc), window.Since);
    }

    [Fact]
    public void Until_in_the_future_stops_at_now()
        => Assert.Equal(FakeLibreNms.Now.UtcDateTime, TimeWindow.Resolve("1h", "2027-01-01T00:00:00Z", null, 24, _time).Until);

    [Theory]
    [InlineData("1h", "2h", null)]
    [InlineData("yesterday", null, null)]
    [InlineData("40d", null, null)]
    [InlineData("6h", null, 6)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 169)]
    public void Bad_windows_are_errors(string? since, string? until, int? hours)
        => Assert.Throws<ToolException>(() => TimeWindow.Resolve(since, until, hours, 24, _time));

    [Fact]
    public async Task Read_back_goes_deeper_until_it_reaches_the_window()
    {
        var window = new TimeWindow(FakeLibreNms.Now.UtcDateTime.AddDays(-3), FakeLibreNms.Now.UtcDateTime);
        var depths = new List<int>();

        // One entry per hour, newest first: the first read of 200 covers just
        // over 8 days, so a 3-day window is complete without reading deeper.
        Task<IReadOnlyList<DateTime>> Read(int depth)
        {
            depths.Add(depth);
            return Task.FromResult<IReadOnlyList<DateTime>>(Enumerable.Range(0, depth).Select(h => FakeLibreNms.Now.UtcDateTime.AddHours(-h)).ToList());
        }

        var result = await DeviceTimeline.ReadBackAsync(Read, t => t, window, CancellationToken.None);

        Assert.Equal([DeviceTimeline.FirstDepth], depths);
        Assert.True(result.Value!.CoveredSince <= window.Since);
    }

    [Fact]
    public async Task Read_back_stops_at_the_cap_and_says_how_far_it_got()
    {
        var window = new TimeWindow(FakeLibreNms.Now.UtcDateTime.AddDays(-30), FakeLibreNms.Now.UtcDateTime);
        var depths = new List<int>();

        // A busy log: one entry a minute, so even the deepest read covers under 2 days.
        Task<IReadOnlyList<DateTime>> Read(int depth)
        {
            depths.Add(depth);
            return Task.FromResult<IReadOnlyList<DateTime>>(Enumerable.Range(0, depth).Select(m => FakeLibreNms.Now.UtcDateTime.AddMinutes(-m)).ToList());
        }

        var result = await DeviceTimeline.ReadBackAsync(Read, t => t, window, CancellationToken.None);

        Assert.Equal([200, 1000, 2000], depths);
        Assert.True(result.Value!.CoveredSince > window.Since);
    }

    [Fact]
    public async Task A_short_log_covers_everything()
    {
        var window = new TimeWindow(FakeLibreNms.Now.UtcDateTime.AddDays(-30), FakeLibreNms.Now.UtcDateTime);

        var result = await DeviceTimeline.ReadBackAsync(
            _ => Task.FromResult<IReadOnlyList<DateTime>>([FakeLibreNms.Now.UtcDateTime.AddHours(-1)]),
            t => t,
            window,
            CancellationToken.None);

        Assert.Equal(DateTime.MinValue, result.Value!.CoveredSince);
    }
}
