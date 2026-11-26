using System.Text.Json;
using DashyNMS.Agent.Catalogue;
using DashyNMS.Agent.Outputs;
using DashyNMS.Agent.Redaction;
using Microsoft.Extensions.Logging.Abstractions;

namespace DashyNMS.Agent.Tests;

public sealed class ToolOutputSchemaTests
{
    public static TheoryData<string> Names => new(ToolCatalogue.Names);

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_tool_has_the_same_envelope_with_its_own_data(string tool)
    {
        var schema = ToolOutputSchema.For(tool);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(
            ["status", "summary", "data", "truncated", "omitted", "message", "details", "stale", "as_of", "stale_reason", "throttling"],
            schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["status"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!));
        var data = schema.GetProperty("properties").GetProperty("data");
        foreach (var shape in data.TryGetProperty("anyOf", out var shapes) ? shapes.EnumerateArray().ToList() : [data])
        {
            Assert.Equal("object", shape.GetProperty("type").GetString());
            Assert.NotEmpty(shape.GetProperty("properties").EnumerateObject());
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_schema_holds_nothing_a_client_could_refuse_a_true_result_over(string tool)
    {
        var text = ToolOutputSchema.For(tool).GetRawText();

        // References would point outside data, and a strict reader rejects a time without a zone as a date-time.
        Assert.DoesNotContain("$ref", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"format\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"additionalProperties\":false", text, StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_always_written_is_required_and_text_is_not()
    {
        var page = Data("find_device").GetProperty("anyOf")[0].GetProperty("properties").GetProperty("page");
        Assert.Equal(["offset", "limit", "returned", "total"], Required(page));

        var device = Data("find_device").GetProperty("anyOf")[0].GetProperty("properties").GetProperty("devices").GetProperty("items");
        Assert.Equal(["device_id"], Required(device));
    }

    [Fact]
    public void A_property_that_holds_more_than_one_type_says_which()
    {
        var applies = Data("alert_rules").GetProperty("properties").GetProperty("rules").GetProperty("items")
            .GetProperty("properties").GetProperty("applies_to").GetProperty("anyOf");

        Assert.Equal(["string", "object"], applies.EnumerateArray().Select(t => t.GetProperty("type").GetString()!));
    }

    [Fact]
    public async Task A_result_cut_to_its_cap_still_fits()
    {
        var nms = new FakeLibreNms();
        var runner = new ToolRunner(new Redactor(), new AgentOptions { MaxResultBytes = 400 }, NullLogger<ToolRunner>.Instance) { Returned = OutputContract.Check };

        var json = JsonDocument.Parse(await new Tools.DeviceTools(nms.Client, nms.Directory, nms.Resolver, nms.Scope, runner, nms.Options)
            .FindDevice("sw", verbose: true)).RootElement;

        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void The_check_itself_fails_what_it_should()
    {
        static IReadOnlyList<string> Problems(string schema, string value)
            => JsonSchemaCheck.Problems(JsonDocument.Parse(schema).RootElement, JsonDocument.Parse(value).RootElement);

        Assert.Empty(Problems("""{"type":"object","properties":{"a":{"type":["integer","null"]}},"required":["a"]}""", """{"a":1}"""));
        Assert.NotEmpty(Problems("""{"type":"object","properties":{"a":{"type":"integer"}},"required":["a"]}""", "{}"));
        Assert.NotEmpty(Problems("""{"type":"object","properties":{"a":{"type":"integer"}}}""", """{"a":"1"}"""));
        Assert.NotEmpty(Problems("""{"type":"object","properties":{"a":{"type":"integer"}}}""", """{"b":1}"""));
        Assert.NotEmpty(Problems("""{"type":"array","items":{"type":"string"}}""", "[1]"));
        Assert.NotEmpty(Problems("""{"anyOf":[{"type":"string"},{"type":"object","properties":{}}]}""", "1"));
        Assert.NotEmpty(Problems("""{"enum":["ok"]}""", "\"no\""));
        Assert.NotEmpty(Problems("""{"pattern":"x"}""", "\"x\""));
        Assert.NotEmpty(Problems("true", "1"));
    }

    private static JsonElement Data(string tool) => ToolOutputSchema.For(tool).GetProperty("properties").GetProperty("data");

    private static IEnumerable<string> Required(JsonElement schema) => schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!);
}
