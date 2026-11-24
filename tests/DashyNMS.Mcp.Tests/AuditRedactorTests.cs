using System.Text.Json;
using DashyNMS.Agent.Redaction;
using DashyNMS.Mcp.Auditing;

namespace DashyNMS.Mcp.Tests;

/// <summary>What the audit trail keeps of a call's arguments (#18): enough to say what was asked, never a secret.</summary>
public sealed class AuditRedactorTests
{
    private const string Secret = "synthetic-librenms-token";

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"device":{},"query":{},"limit":{},"verbose":{},"levels":{}}}""").RootElement;

    private readonly AuditRedactor _redactor = new(new Redactor(), [Secret, "token", null, ""]);

    private string Arguments(string json) => _redactor.Arguments(Read(json), Schema).Arguments.ToJsonString();

    private static Dictionary<string, JsonElement> Read(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Ordinary_arguments_are_kept_as_they_are()
    {
        Assert.Equal(
            """{"device":"core-sw1","limit":10,"verbose":true,"levels":["error","critical"]}""",
            Arguments("""{"device":"core-sw1","limit":10,"verbose":true,"levels":["error","critical"]}"""));
        Assert.Equal("""{"device":null}""", Arguments("""{"device":null}"""));
    }

    [Theory]
    [InlineData("""{"query":"password=hunter2"}""", "hunter2")]
    [InlineData("""{"query":"snmp-server community s3cret RO"}""", "s3cret")]
    [InlineData("""{"levels":["password=hunter2"]}""", "hunter2")]
    public void What_looks_like_a_secret_is_redacted(string arguments, string secret)
    {
        Assert.DoesNotContain(secret, Arguments(arguments), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData($$"""{"query":"{{Secret}}"}""")]
    [InlineData($$"""{"query":"look for {{Secret}} please"}""")]
    [InlineData($$"""{"levels":["{{Secret}}"]}""")]
    public void The_servers_own_credentials_are_redacted_wherever_they_are(string arguments)
    {
        var recorded = Arguments(arguments);

        Assert.DoesNotContain(Secret, recorded, StringComparison.Ordinal);
        Assert.Contains("redacted", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_that_is_an_ordinary_word_is_only_redacted_as_a_whole_value()
    {
        Assert.Equal("[redacted]", _redactor.Text("token"));
        Assert.Equal("tokenring-sw1", _redactor.Text("tokenring-sw1"));
    }

    [Fact]
    public void A_credential_given_as_a_number_is_redacted_too()
    {
        var redactor = new AuditRedactor(new Redactor(), ["73928146"]);

        var (recorded, _) = redactor.Arguments(Read("""{"limit":73928146,"device":1073928146,"query":7392,"levels":[73928146,5]}"""), Schema);

        Assert.Equal("""{"limit":"[redacted]","device":"10[redacted]","query":7392,"levels":["[redacted]",5]}""", recorded.ToJsonString());
    }

    [Fact]
    public void Long_text_is_recorded_by_its_length()
    {
        var recorded = Arguments(JsonSerializer.Serialize(new { query = "start " + new string('x', 400) + " password=hunter2" }));

        Assert.Equal("""{"query":"[423 characters]"}""", recorded);
    }

    [Fact]
    public void Arguments_a_tool_does_not_declare_are_counted_and_not_recorded()
    {
        var (recorded, undeclared) = _redactor.Arguments(Read("""{"device":"core-sw1","api_key":"abc","hunter2":1}"""), Schema);

        Assert.Equal("""{"device":"core-sw1"}""", recorded.ToJsonString());
        Assert.Equal(2, undeclared);
    }

    [Fact]
    public void Nested_values_are_not_recorded()
    {
        Assert.Equal("""{"query":"[object]","levels":["[object]","[list]"]}""", Arguments("""{"query":{"password":"hunter2"},"levels":[{"a":"b"},["c"]]}"""));
    }

    [Fact]
    public void Long_lists_and_numbers_are_cut_down()
    {
        var list = Arguments(JsonSerializer.Serialize(new { levels = Enumerable.Range(0, 30) }));
        Assert.EndsWith("""19,"[10 more]"]}""", list, StringComparison.Ordinal);
        Assert.Equal("""{"limit":"[40 characters]"}""", Arguments($$"""{"limit":{{new string('1', 40)}}}"""));
    }

    [Fact]
    public void Arguments_too_long_together_keep_only_their_names()
    {
        var many = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 250)).ToArray();

        Assert.Equal("""{"device":"[too long]","levels":"[too long]"}""", Arguments(JsonSerializer.Serialize(new { device = "core-sw1", levels = many })));
    }

    [Fact]
    public void An_entry_is_one_line_whatever_is_in_it()
    {
        var (arguments, _) = _redactor.Arguments(Read("""{"query":"one\ntwo\r\u2028{\"event\":\"tool_call\"}"}"""), Schema);
        var entry = new AuditEntry(DateTimeOffset.UnixEpoch, "stdio", null, "cli\u001b[2J", "1", "find_device", arguments, 0, AuditStatus.Ok, 2, TimeSpan.FromMilliseconds(5.9));

        var json = entry.ToJson();

        Assert.DoesNotContain(json, char.IsControl);
        Assert.All(json, c => Assert.True(char.IsAscii(c)));
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("tool_call", root.GetProperty("event").GetString());
        Assert.Equal("1970-01-01T00:00:00.000Z", root.GetProperty("time").GetString());
        Assert.Equal(5, root.GetProperty("duration_ms").GetInt64());
        Assert.False(root.TryGetProperty("token_label", out _));
    }
}
