using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Vault;

/// <summary>One resolved reference, kept so callers can redact its value later.</summary>
public sealed record SecretUse(string Name, string Value);

/// <summary>
/// The result of expanding a template. <see cref="Missing"/> is non-empty when
/// a reference names a secret that does not exist; the caller must refuse to
/// run rather than pass the literal reference to a process.
/// </summary>
public sealed record SecretExpansion(
    string Text,
    IReadOnlyList<SecretUse> Used,
    IReadOnlyList<string> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>
/// The one placeholder rule: <c>%vault:NAME%</c>. The prefix keeps the rule
/// unambiguous beside Windows environment syntax and batch variables. Expansion
/// is a single pass, so a secret value can never pull in another secret.
/// </summary>
public static partial class SecretTemplate
{
    public const string Prefix = "%vault:";
    public const string Redacted = "***";

    private const string NamePattern = "[A-Za-z_][A-Za-z0-9_]*";

    [GeneratedRegex(
        "%%vault:(" + NamePattern + ")%%|%vault:(" + NamePattern + ")%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    /// <summary>
    /// Expands one string. <c>%%vault:NAME%%</c> is an escaped literal, which
    /// lets a command mention the syntax without using a secret.
    /// </summary>
    public static SecretExpansion Expand(string? text, Func<string, string?>? resolve)
    {
        if (string.IsNullOrEmpty(text)) return new SecretExpansion(text ?? "", [], []);
        resolve ??= static _ => null;

        var used = new List<SecretUse>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var missingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string expanded = Reference().Replace(text, match =>
        {
            if (match.Groups[1].Success)
                return $"%vault:{match.Groups[1].Value}%";

            string name = match.Groups[2].Value;
            string? value = resolve(name);
            if (value is null)
            {
                if (missingNames.Add(name)) missing.Add(name);
                return match.Value;
            }

            if (usedNames.Add(name)) used.Add(new SecretUse(name, value));
            return value;
        });

        return new SecretExpansion(expanded, used, missing);
    }

    /// <summary>The names referenced by a plain string, ignoring escaped forms.</summary>
    public static IReadOnlyList<string> Names(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Reference().Matches(text))
        {
            if (match.Groups[1].Success) continue;
            string name = match.Groups[2].Value;
            if (seen.Add(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Expands JSON string values, recursively. Object keys and non-string values
    /// are left alone. The input node is not changed.
    /// </summary>
    public static JsonNode? ExpandJson(
        JsonNode? node,
        Func<string, string?>? resolve,
        out IReadOnlyList<SecretUse> used,
        out IReadOnlyList<string> missing)
    {
        var uses = new Dictionary<string, SecretUse>(StringComparer.OrdinalIgnoreCase);
        var misses = new List<string>();
        var seenMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        JsonNode? expanded = ExpandNode(node, resolve, uses, misses, seenMisses);
        used = [.. uses.Values];
        missing = misses;
        return expanded;
    }

    /// <summary>The names referenced anywhere in a JSON tree.</summary>
    public static IReadOnlyList<string> Names(JsonNode? node)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectNames(node, names, seen);
        return names;
    }

    /// <summary>
    /// Replaces exact occurrences of the supplied values. Longest first, so one
    /// secret cannot partially redact another. This is best effort: an encoded or
    /// transformed value is not recognised.
    /// </summary>
    public static string Redact(string? text, IEnumerable<string>? values)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (values is null) return text;

        string[] secrets = [.. values
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)];

        if (secrets.Length == 0) return text;

        var builder = new StringBuilder(text);
        foreach (string secret in secrets)
            builder.Replace(secret, Redacted);
        return builder.ToString();
    }

    public static string Redact(string? text, IReadOnlyList<SecretUse> uses) =>
        Redact(text, uses.Select(use => use.Value));

    private static JsonNode? ExpandNode(
        JsonNode? node,
        Func<string, string?>? resolve,
        Dictionary<string, SecretUse> uses,
        List<string> missing,
        HashSet<string> seenMissing)
    {
        switch (node)
        {
            case JsonObject source:
            {
                var copy = new JsonObject();
                foreach (var pair in source)
                {
                    copy[pair.Key] = ExpandNode(
                        pair.Value, resolve, uses, missing, seenMissing);
                }
                return copy;
            }

            case JsonArray source:
            {
                var copy = new JsonArray();
                foreach (JsonNode? item in source)
                    copy.Add(ExpandNode(item, resolve, uses, missing, seenMissing));
                return copy;
            }

            case JsonValue value when value.TryGetValue<string>(out string? text):
            {
                SecretExpansion expansion = Expand(text, resolve);
                foreach (SecretUse use in expansion.Used)
                    uses.TryAdd(use.Name, use);
                foreach (string name in expansion.Missing)
                {
                    if (seenMissing.Add(name)) missing.Add(name);
                }
                return JsonValue.Create(expansion.Text);
            }

            default:
                return node?.DeepClone();
        }
    }

    private static void CollectNames(
        JsonNode? node,
        List<string> names,
        HashSet<string> seen)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                    CollectNames(pair.Value, names, seen);
                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                    CollectNames(item, names, seen);
                break;

            case JsonValue value when value.TryGetValue<string>(out string? text):
                foreach (string name in Names(text))
                {
                    if (seen.Add(name)) names.Add(name);
                }
                break;
        }
    }
}
