using System.Text.RegularExpressions;

namespace DashyNMS.Agent.Redaction;

/// <summary>
/// Blanks secrets out of text before a model sees it: SNMP communities,
/// passwords and keys in device configs, private keys, and the secret
/// elements of a pfSense config.
/// </summary>
/// <remarks>
/// <para>
/// Every string a tool returns goes through this, including Unimus backup
/// diffs and Graylog messages. SNMP v2c communities matter most: they are
/// live credentials, and they turn up in configs and device details alike.
/// </para>
/// <para>
/// The keyword stays and only the value goes, so the model can still say
/// "the community changed" or "a RADIUS key was added". Leaning towards
/// redacting too much: a missed secret leaks, an extra redaction only hides
/// a word.
/// </para>
/// </remarks>
public sealed partial class Redactor
{
    public const string Marker = "‹redacted›";

    /// <summary>
    /// A value: a quoted string, or everything up to the next whitespace -
    /// but never one already redacted, or the "=" of an assignment. With the
    /// atomic groups in the rules (which stop a type number such as the 7 in
    /// "key 7 x" being taken for the value), a second pass changes nothing.
    /// </summary>
    private const string Value = "(?!" + Marker + "|[=:])(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s\"']+)";

    // Applied in this order; earlier, more specific rules run first so the
    // generic keyword rule doesn't swallow the wrong word (AOS-S's
    // "password manager user-name ..." would otherwise lose "manager").
    private static readonly Regex[] KeepPrefixRules =
    [
        // Cisco and AOS-S: snmp-server community public RO / snmp-server community "public" operator
        SnmpCommunity(),
        // SNMPv3 users: ... auth sha <secret> priv aes 128 <secret>
        SnmpV3Auth(),
        SnmpV3Priv(),
        // AOS-S: password manager user-name "admin" sha1 "<hash>"
        ArubaLocalPassword(),
        // radius-server host 10.0.0.1 key 7 <x> / tacacs-server key <x> / crypto isakmp key 0 <x> address ...
        RadiusTacacsKey(),
        // password 7 <x> / enable secret 5 <x> / pre-shared-key ascii-text <x> / key-string <x>
        KeywordValue(),
        // password=<x> / psk: <x> / api_key = <x> (Linux, ini, YAML, JSON-ish)
        KeywordAssignment(),
    ];

    /// <summary>Redacts every secret it recognises in <paramref name="text"/>.</summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        text = PrivateKeyBlock().Replace(text, "-----BEGIN $1-----" + Marker + "-----END $1-----");
        text = PfSenseSecretElement().Replace(text, "<$1>" + Marker + "</$1>");

        foreach (var rule in KeepPrefixRules)
        {
            text = rule.Replace(text, "${prefix}" + Marker);
        }

        return text;
    }

    [GeneratedRegex(@"(?<prefix>\bsnmp-server\s+community\s+)" + Value, RegexOptions.IgnoreCase)]
    private static partial Regex SnmpCommunity();

    [GeneratedRegex(@"(?<prefix>\bauth\s+(?:md5|sha(?:-?\d+)?)\s+)" + Value, RegexOptions.IgnoreCase)]
    private static partial Regex SnmpV3Auth();

    [GeneratedRegex(@"(?<prefix>\bpriv\s+(?:3?des\d*|aes(?:-?\d+)?)\s+(?>(?:\d{3}\s+)?))" + Value, RegexOptions.IgnoreCase)]
    private static partial Regex SnmpV3Priv();

    [GeneratedRegex(@"(?<prefix>\bpassword\s+(?:manager|operator)\b[^\r\n]*?\b(?:sha1|sha256|plaintext)\s+)" + Value, RegexOptions.IgnoreCase)]
    private static partial Regex ArubaLocalPassword();

    [GeneratedRegex(@"(?<prefix>\b(?:radius|tacacs|isakmp)(?:-server)?\b[^\r\n]*?\bkey\s+(?>(?:\d\s+)?))" + Value, RegexOptions.IgnoreCase)]
    private static partial Regex RadiusTacacsKey();

    [GeneratedRegex(@"(?<prefix>\b(?:password|passwd|secret|pre-shared-key|psk|encrypted-key|key-string|authentication-key)\s+(?>(?:\d{1,2}\s+)?)(?>(?:ascii-text|hex-key|ascii|hex|plain-text|cipher)\s+)?)(?!manager\b|operator\b)" + Value, RegexOptions.IgnoreCase)]
    private static partial Regex KeywordValue();

    [GeneratedRegex(@"(?<prefix>\b(?:password|passwd|passphrase|wpa_passphrase|secret|psk|pre-shared-key|api[_-]?key|apikey|token|key)""?\s*[=:]\s*)" + Value, RegexOptions.IgnoreCase)]
    private static partial Regex KeywordAssignment();

    [GeneratedRegex(@"-----BEGIN ((?:[A-Z0-9]+ )*PRIVATE KEY)-----.*?-----END \1-----", RegexOptions.Singleline)]
    private static partial Regex PrivateKeyBlock();

    /// <summary>pfSense's config.xml: user passwords and hashes, IPsec PSKs, API keys and certificate private keys (base64, not PEM).</summary>
    [GeneratedRegex(@"<(password|bcrypt-hash|sha512-hash|md5-hash|pre-shared-key|apikey|prv|authkey|secret)>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PfSenseSecretElement();
}
