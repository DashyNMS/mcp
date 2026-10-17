using System.Text;
using System.Text.Json;
using DashyNMS.Agent.Redaction;

namespace DashyNMS.Agent.Tests;

public sealed class ToolResultTests
{
    private static readonly Redactor Redactor = new();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private sealed record Sample(string Name, int SpeedMbps, DateTime LastPolled, string? Notes);

    [Fact]
    public void Ok_uses_snake_case_and_leaves_out_nulls()
    {
        var json = ToolResult.Render(
            ToolResult.Ok("One port.", new Sample("1/1/1", 1000, new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc), null)),
            Redactor,
            16_384);

        var root = Parse(json);
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("One port.", root.GetProperty("summary").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(1000, data.GetProperty("speed_mbps").GetInt32());
        Assert.Equal("2026-10-17T09:30:00Z", data.GetProperty("last_polled").GetString());
        Assert.False(data.TryGetProperty("notes", out _));
        Assert.False(root.TryGetProperty("truncated", out _));
    }

    [Fact]
    public void Error_has_status_and_message_only()
    {
        var root = Parse(ToolResult.Render(ToolResult.Error("No device matches."), Redactor, 16_384));

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal("No device matches.", root.GetProperty("message").GetString());
        Assert.Equal(2, root.EnumerateObject().Count());
    }

    [Fact]
    public void Every_string_is_redacted_including_nested_lists()
    {
        var data = new { config_diff = "snmp-server community S3cret RO", log_lines = new[] { "user admin password=hunter2" } };

        var json = ToolResult.Render(ToolResult.Ok("Changed.", data), Redactor, 16_384);

        Assert.DoesNotContain("S3cret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Contains(Redactor.Marker, json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tools_own_limit_is_reported()
    {
        var items = ToolResult.Take(Enumerable.Range(1, 60), 50, out var omitted);

        var root = Parse(ToolResult.Render(ToolResult.Ok("Alerts.", items, omitted), Redactor, 16_384));

        Assert.Equal(50, root.GetProperty("data").GetArrayLength());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(10, root.GetProperty("omitted").GetInt32());
    }

    [Fact]
    public void Oversize_lists_lose_entries_from_the_end_and_say_so()
    {
        var rows = Enumerable.Range(1, 500).Select(i => new { id = i, text = new string('x', 100) }).ToList();

        var json = ToolResult.Render(ToolResult.Ok("Many.", new { rows }), Redactor, 4_096);

        Assert.True(Encoding.UTF8.GetByteCount(json) <= 4_096);
        var root = Parse(json);
        Assert.True(root.GetProperty("truncated").GetBoolean());
        var kept = root.GetProperty("data").GetProperty("rows");
        Assert.Equal(1, kept[0].GetProperty("id").GetInt32());
        Assert.Equal(500 - kept.GetArrayLength(), root.GetProperty("omitted").GetInt32());
    }

    [Fact]
    public void Oversize_text_is_shortened_with_an_ellipsis()
    {
        var json = ToolResult.Render(ToolResult.Ok("Diff.", new { config_diff = new string('a', 20_000) }), Redactor, 2_048);

        Assert.True(Encoding.UTF8.GetByteCount(json) <= 2_048);
        var root = Parse(json);
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.EndsWith(ToolResult.Ellipsis, root.GetProperty("data").GetProperty("config_diff").GetString(), StringComparison.Ordinal);
        Assert.Equal("Diff.", root.GetProperty("summary").GetString());
    }

    [Fact]
    public void Output_that_cannot_shrink_becomes_an_error()
    {
        var json = ToolResult.Render(ToolResult.Ok(new string('s', 5_000), null), Redactor, 1_024);

        Assert.Equal("error", Parse(json).GetProperty("status").GetString());
    }

    [Fact]
    public void Markers_are_readable_not_escaped()
    {
        var json = ToolResult.Render(ToolResult.Ok("x", new { line = "password 7 abc" }), Redactor, 16_384);

        Assert.Contains("‹redacted›", json, StringComparison.Ordinal);
    }
}
