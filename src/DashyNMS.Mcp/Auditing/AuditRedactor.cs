using System.Text.Json;
using System.Text.Json.Nodes;
using DashyNMS.Agent.Redaction;

namespace DashyNMS.Mcp.Auditing;

/// <summary>
/// Makes what a client sent fit to record: secrets out, and short enough that
/// one call can't fill the log.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of secret come out. The redactor's patterns catch what looks like
/// one, as they do in results. The server's own credentials are matched
/// exactly, because a model that has somehow been given one could repeat it as
/// an argument and it wouldn't look like anything.
/// </para>
/// <para>
/// Text too long to be a device name, a time or a search is recorded by its
/// length alone. Cutting it short first could cut a secret in half, and half a
/// secret matches no pattern.
/// </para>
/// </remarks>
public sealed class AuditRedactor
{
    internal const int MaxText = 256;
    internal const int MaxItems = 20;

    /// <summary>The most an entry's arguments may take as JSON. It keeps a GELF message inside one UDP packet.</summary>
    internal const int MaxArgumentsLength = 3000;

    private const string Marker = "[redacted]";

    /// <summary>
    /// A credential this short is only looked for as the whole of a value, as
    /// an ordinary word could contain it: with a Graylog access token the
    /// password is the word "token".
    /// </summary>
    private const int MinimumEmbeddedSecret = 6;

    private readonly Redactor _redactor;
    private readonly string[] _secrets;

    /// <param name="secrets">The server's own credentials. Longest first, so one that contains another goes whole.</param>
    public AuditRedactor(Redactor redactor, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(secrets);
        _redactor = redactor;
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
    }

    public string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxText)
        {
            return $"[{text.Length} characters]";
        }

        foreach (var secret in _secrets)
        {
            if (text.Equals(secret, StringComparison.Ordinal))
            {
                return Marker;
            }

            if (secret.Length >= MinimumEmbeddedSecret)
            {
                text = text.Replace(secret, Marker, StringComparison.Ordinal);
            }
        }

        return _redactor.Redact(text);
    }

    /// <summary>The arguments a tool declares, fit to record, and how many it doesn't.</summary>
    public (JsonObject Arguments, int Undeclared) Arguments(IEnumerable<KeyValuePair<string, JsonElement>>? arguments, JsonElement schema)
    {
        var declared = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        var recorded = new JsonObject();
        var undeclared = 0;
        foreach (var (name, value) in arguments ?? [])
        {
            if (declared.Contains(name))
            {
                recorded[name] = Value(value);
            }
            else
            {
                undeclared++;
            }
        }

        if (recorded.ToJsonString().Length > MaxArgumentsLength)
        {
            // Which arguments were given still says something; their values don't fit.
            foreach (var name in recorded.Select(p => p.Key).ToList())
            {
                recorded[name] = "[too long]";
            }
        }

        return (recorded, undeclared);
    }

    private JsonNode? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Text(value.GetString()!),

        // As text, so a number as long as a page is cut down like any other.
        JsonValueKind.Number => value.GetRawText() is { Length: <= 32 } number ? JsonNode.Parse(number) : $"[{value.GetRawText().Length} characters]",
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => List(value),

        // No tool takes one, so there's nothing to say what its keys and values are.
        JsonValueKind.Object => "[object]",
        _ => null,
    };

    private JsonArray List(JsonElement value)
    {
        var list = new JsonArray();
        foreach (var item in value.EnumerateArray())
        {
            if (list.Count == MaxItems)
            {
                list.Add($"[{value.GetArrayLength() - MaxItems} more]");
                break;
            }

            // Lists of lists aren't anything a tool takes either.
            list.Add(item.ValueKind == JsonValueKind.Array ? "[list]" : Value(item));
        }

        return list;
    }
}
