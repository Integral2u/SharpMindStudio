using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Recipes;

/// <summary>
/// Content address for a Recipe. Property order and insignificant whitespace do
/// not matter: the node tree is canonicalised (object keys sorted) before hashing.
/// </summary>
public static class RecipeHasher
{
    public static string Hash(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        JsonNode node = JsonSerializer.SerializeToNode(recipe, StudioJson.File)
            ?? throw new InvalidOperationException("Recipe did not serialise to a JSON value.");
        JsonNode canonical = Canonicalize(node)
            ?? throw new InvalidOperationException("Recipe canonicalisation produced null.");
        NormalizeGraph(canonical);
        byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// A graph's identity must not depend on the order nodes were listed or the
    /// order a step listed its inputs. Parameter arrays keep their order, since
    /// those are data (for example an explicit source list).
    /// </summary>
    private static void NormalizeGraph(JsonNode node)
    {
        if (node is not JsonObject root || root["steps"] is not JsonArray steps) return;

        var ordered = new JsonArray();
        foreach (JsonNode? step in steps.OrderBy(StepId, StringComparer.Ordinal))
        {
            if (step is JsonObject stepObject && stepObject["needs"] is JsonArray needs)
            {
                var sortedNeeds = new JsonArray();
                foreach (string need in needs.Select(NeedId).OrderBy(id => id, StringComparer.Ordinal))
                    sortedNeeds.Add(need);
                stepObject["needs"] = sortedNeeds;
            }
            ordered.Add(step?.DeepClone());
        }
        root["steps"] = ordered;
    }

    private static string StepId(JsonNode? step) =>
        step is JsonObject obj && obj["id"] is JsonValue id && id.TryGetValue(out string? value)
            ? value ?? ""
            : "";

    private static string NeedId(JsonNode? need) =>
        need is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    sorted[pair.Key] = Canonicalize(pair.Value);
                return sorted;
            }
            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array) copy.Add(Canonicalize(item));
                return copy;
            }
            default:
                return node.DeepClone();
        }
    }
}
