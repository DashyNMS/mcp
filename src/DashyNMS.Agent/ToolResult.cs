using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DashyNMS.Agent.Redaction;

namespace DashyNMS.Agent;

/// <summary>
/// The JSON every tool returns, and the rules that keep it safe and small.
/// </summary>
/// <remarks>
/// <para>
/// Success: <c>{"status":"ok","summary":"...","data":{...}}</c>. Failure:
/// <c>{"status":"error","message":"..."}</c>. The summary and message are
/// one sentence we write ourselves; anything that came from a device (names,
/// aliases, log lines, config text) only ever sits under <c>data</c> or
/// <c>details</c>, so a prompt hidden in a syslog line never reads as ours.
/// </para>
/// <para>
/// Every string value is redacted, then the whole thing is cut to the byte
/// cap: entries come off the end of the biggest list first, then the longest
/// text is shortened. A cut result says so with <c>"truncated": true</c> and
/// how many list entries were left out in <c>"omitted"</c>.
/// </para>
/// </remarks>
public static class ToolResult
{
    internal const string Ellipsis = "…";

    /// <summary>
    /// snake_case names with units in them (<c>speed_mbps</c>), no nulls, and
    /// characters such as ‹ › left readable rather than \u-escaped.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <param name="summary">One sentence of our own; never device text.</param>
    /// <param name="data">The result itself.</param>
    /// <param name="omitted">List entries a tool already left out to stay inside its own limit.</param>
    public static JsonObject Ok(string summary, object? data, int omitted = 0)
    {
        var result = new JsonObject
        {
            ["status"] = "ok",
            ["summary"] = summary,
            ["data"] = JsonSerializer.SerializeToNode(data, Json),
        };

        if (omitted > 0)
        {
            result["truncated"] = true;
            result["omitted"] = omitted;
        }

        return result;
    }

    public static JsonObject Error(string message, object? details = null)
    {
        var result = new JsonObject
        {
            ["status"] = "error",
            ["message"] = message,
        };

        if (details is not null)
        {
            result["details"] = JsonSerializer.SerializeToNode(details, Json);
        }

        return result;
    }

    /// <summary>Takes up to <paramref name="limit"/> items and counts the rest, for a tool's own list limits.</summary>
    public static IReadOnlyList<T> Take<T>(IEnumerable<T> items, int limit, out int omitted)
    {
        var all = items as IReadOnlyCollection<T> ?? items.ToList();
        omitted = Math.Max(0, all.Count - limit);
        return all.Take(limit).ToList();
    }

    /// <summary>Redacts every string in <paramref name="result"/>, applies the cap and returns the JSON text.</summary>
    public static string Render(JsonObject result, Redactor redactor, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(redactor);

        RedactStrings(result, redactor);

        var json = result.ToJsonString(Json);
        if (Encoding.UTF8.GetByteCount(json) <= maxBytes)
        {
            return json;
        }

        result["truncated"] = true;
        var omitted = result["omitted"]?.GetValue<int>() ?? 0;

        // Each pass removes one list entry or halves one string, so this ends;
        // the bound only guards against a shape nothing here can shrink.
        for (var pass = 0; pass < 10_000; pass++)
        {
            if (LargestNonEmptyArray(result) is { } array)
            {
                array.RemoveAt(array.Count - 1);
                omitted++;
            }
            else if (!ShortenLongestString(result))
            {
                break;
            }

            result["omitted"] = omitted;
            json = result.ToJsonString(Json);
            if (Encoding.UTF8.GetByteCount(json) <= maxBytes)
            {
                return json;
            }
        }

        return Error("The result was too large to return, even shortened. Ask for a narrower time window or a single device.")
            .ToJsonString(Json);
    }

    private static void RedactStrings(JsonNode? node, Redactor redactor)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = redactor.Redact(text);
                    }
                    else
                    {
                        RedactStrings(child, redactor);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = redactor.Redact(text);
                    }
                    else
                    {
                        RedactStrings(array[i], redactor);
                    }
                }

                break;
        }
    }

    /// <summary>The non-empty list taking the most room - where cutting entries buys the most.</summary>
    private static JsonArray? LargestNonEmptyArray(JsonNode root)
    {
        JsonArray? best = null;
        var bestSize = -1;

        foreach (var node in Descendants(root))
        {
            if (node is JsonArray { Count: > 0 } array)
            {
                var size = array.ToJsonString(Json).Length;
                if (size > bestSize)
                {
                    best = array;
                    bestSize = size;
                }
            }
        }

        return best;
    }

    /// <summary>Halves the longest string over 64 characters; false when there is none.</summary>
    private static bool ShortenLongestString(JsonNode root)
    {
        JsonNode? parent = null;
        object? slot = null;
        var longest = 64;

        foreach (var node in Descendants(root))
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        if (key is not ("status" or "summary" or "message") && child is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > longest)
                        {
                            (parent, slot, longest) = (obj, key, s.Length);
                        }
                    }

                    break;

                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > longest)
                        {
                            (parent, slot, longest) = (array, i, s.Length);
                        }
                    }

                    break;
            }
        }

        switch (parent, slot)
        {
            case (JsonObject obj, string key):
                obj[key] = Halve(obj[key]!.GetValue<string>());
                return true;
            case (JsonArray array, int index):
                array[index] = Halve(array[index]!.GetValue<string>());
                return true;
            default:
                return false;
        }
    }

    private static string Halve(string text)
    {
        var keep = text.Length / 2;

        // Don't split a surrogate pair, or a redaction marker in two.
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        var marker = text.LastIndexOf('‹', keep - 1);
        if (marker >= 0 && text.IndexOf('›', marker) is var end && end >= keep)
        {
            keep = marker;
        }

        return text[..keep] + Ellipsis;
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode root)
    {
        var stack = new Stack<JsonNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            IEnumerable<JsonNode?> children = node switch
            {
                JsonObject obj => obj.Select(p => p.Value),
                JsonArray array => array,
                _ => [],
            };

            foreach (var child in children)
            {
                if (child is not null)
                {
                    stack.Push(child);
                }
            }
        }
    }
}
