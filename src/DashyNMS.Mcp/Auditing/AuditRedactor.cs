using System.Text;
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

    /// <param name="secrets">The server's own credentials.</param>
    public AuditRedactor(Redactor redactor, IEnumerable<string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(secrets);
        _redactor = redactor;
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The start of <paramref name="text"/>, at most <paramref name="length"/>
    /// of it, with any credential the cut would go through left out whole: half
    /// of one isn't found. It only looks around the cut, so it costs the same
    /// however long the text is.
    /// </summary>
    internal string Start(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var cut = length;
        for (var i = 0; i < _secrets.Length; i++)
        {
            var secret = _secrets[i];
            var from = Math.Max(0, cut - secret.Length + 1);
            var around = text.AsSpan(from, Math.Min(text.Length, cut + secret.Length - 1) - from);

            // Anything found here starts before the cut and ends after it.
            if (cut > 0 && around.IndexOf(secret, StringComparison.Ordinal) is var found and >= 0)
            {
                // The cut has moved, perhaps into one already looked for.
                cut = from + found;
                i = -1;
            }
        }

        return text[..cut];
    }

    public string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxText)
        {
            return $"[{text.Length} characters]";
        }

        return _secrets.Contains(text, StringComparer.Ordinal) ? Marker : Scrub(text);
    }

    /// <summary>The same redaction for text of any length, with no limit on it and no whole-value match.</summary>
    internal string Scrub(string text) => _redactor.Redact(HideSecrets(text));

    /// <summary>
    /// Found all together in the text as it came, so that hiding one can't
    /// leave part of another that overlaps it.
    /// </summary>
    private string HideSecrets(string text)
    {
        var found = new List<(int Start, int End)>();
        foreach (var secret in _secrets.Where(s => s.Length >= MinimumEmbeddedSecret))
        {
            for (var at = text.IndexOf(secret, StringComparison.Ordinal); at >= 0; at = text.IndexOf(secret, at + 1, StringComparison.Ordinal))
            {
                found.Add((at, at + secret.Length));
            }
        }

        if (found.Count == 0)
        {
            return text;
        }

        found.Sort();
        var hidden = new StringBuilder();
        var done = 0;
        for (var i = 0; i < found.Count;)
        {
            var (start, end) = found[i];
            for (i++; i < found.Count && found[i].Start < end; i++)
            {
                end = Math.Max(end, found[i].End);
            }

            hidden.Append(text, done, start - done).Append(Marker);
            done = end;
        }

        return hidden.Append(text, done, text.Length - done).ToString();
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
        JsonValueKind.String => String(value),

        JsonValueKind.Number => Number(value.GetRawText()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => List(value),

        // No tool takes one, so there's nothing to say what its keys and values are.
        JsonValueKind.Object => "[object]",
        _ => null,
    };

    /// <summary>JSON can spell text that isn't valid Unicode, half a surrogate pair, which can't be read as a string.</summary>
    private string String(JsonElement value)
    {
        try
        {
            return Text(value.GetString()!);
        }
        catch (InvalidOperationException)
        {
            return "[invalid text]";
        }
    }

    /// <summary>Checked as the text it is: a credential can be all digits, and a number can be as long as a page.</summary>
    private JsonNode? Number(string number)
    {
        if (number.Length > 32)
        {
            return $"[{number.Length} characters]";
        }

        var text = Text(number);
        return text == number ? JsonNode.Parse(number) : text;
    }

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
