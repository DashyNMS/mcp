using System.Security.Cryptography;
using System.Text;

namespace DashyNMS.Mcp;

/// <summary>
/// The bearer tokens that may use the server over HTTP (#17), each with a
/// label that says whose it is in the audit trail.
/// </summary>
/// <remarks>
/// Tokens are compared as hashes, every one of them every time, so how long a
/// comparison takes says nothing about how much of a guess was right or which
/// token it was near.
/// </remarks>
public sealed class HttpTokens
{
    /// <summary>Shorter could be guessed. 32 hex digits is 128 bits.</summary>
    public const int MinimumLength = 32;

    private const int MaximumLabelLength = 50;

    private readonly IReadOnlyList<(string Label, string Token, byte[] Hash)> _tokens;

    private HttpTokens(IReadOnlyList<(string Label, string Token, byte[] Hash)> tokens) => _tokens = tokens;

    public IEnumerable<string> Labels => _tokens.Select(t => t.Label);

    /// <summary>The tokens themselves, only for keeping them out of the audit trail.</summary>
    internal IEnumerable<string> Values => _tokens.Select(t => t.Token);

    /// <summary>
    /// Reads "label:token" entries separated by commas or spaces. A token
    /// without a label is called token-1, token-2 and so on by its place in
    /// the list. Problems name an entry by that place, never by what's in it:
    /// either half could be the token.
    /// </summary>
    public static HttpTokens? Parse(string text, string key, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(problems);
        var found = problems.Count;
        var tokens = new List<(string Label, string Token, byte[] Hash)>();
        var entries = text.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var place = $"{key}: entry {i + 1}";
            var colon = entries[i].IndexOf(':', StringComparison.Ordinal);
            var label = colon < 0 ? $"token-{i + 1}" : entries[i][..colon];
            var token = entries[i][(colon + 1)..];
            if (!IsLabel(label))
            {
                problems.Add($"{place} has a label that isn't 1 to {MaximumLabelLength} letters, digits, dots, hyphens or underscores.");
            }
            else if (!IsToken(token))
            {
                problems.Add($"{place} isn't a bearer token of at least {MinimumLength} letters, digits or -._~+/ characters. Make one with: openssl rand -hex 32");
            }
            else if (tokens.Any(t => t.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{place} has the same label as another.");
            }
            else if (tokens.Any(t => t.Token == token))
            {
                problems.Add($"{place} has the same token as another, so its label couldn't say who used it.");
            }
            else
            {
                tokens.Add((label, token, Hash(token)));
            }
        }

        if (tokens.Count == 0 && problems.Count == found)
        {
            problems.Add($"{key} has no tokens in it.");
        }

        return problems.Count == found ? new HttpTokens(tokens) : null;
    }

    /// <summary>Whose token this is, or null when it isn't anyone's.</summary>
    public string? LabelFor(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Hash(token);
        string? label = null;
        foreach (var known in _tokens)
        {
            // Every one, without stopping at a match.
            if (CryptographicOperations.FixedTimeEquals(hash, known.Hash))
            {
                label = known.Label;
            }
        }

        return label;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static bool IsLabel(string text)
        => text.Length is > 0 and <= MaximumLabelLength && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>What an Authorization header can carry (RFC 6750's b64token), so a token that could never arrive is caught here.</summary>
    private static bool IsToken(string text)
        => text.Length >= MinimumLength
            && text.TrimEnd('=') is { Length: > 0 } body
            && body.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/');
}
