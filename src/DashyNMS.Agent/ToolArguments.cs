using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace DashyNMS.Agent;

/// <summary>
/// How tool arguments are read: forgiving about JSON types the way a model
/// is loose with them, so an argument meant as text never fails before the
/// tool runs.
/// </summary>
/// <remarks>
/// The schemas say an optional text parameter is <c>["string","null"]</c>,
/// but some clients flatten that union away, and a model then sends an ID as
/// a number - <c>"sensor": 16197</c> - which the SDK rejected with a bare
/// "An error occurred invoking 'graph'" (found in the #13 trial). Here a
/// number or true/false is read as its text, and a number parameter accepts
/// "24" too. The schemas themselves don't change.
/// </remarks>
public static class ToolArguments
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Insert(0, new LenientStringConverter());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>A string from a string, or from a number or boolean as written.</summary>
    internal sealed class LenientStringConverter : JsonConverter<string>
    {
        public override bool HandleNull => false;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray()),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Expected text, not {reader.TokenType}."),
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
