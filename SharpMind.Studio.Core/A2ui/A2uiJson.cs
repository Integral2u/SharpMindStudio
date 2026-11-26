using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.A2ui;

/// <summary>Type-safe reads for model-supplied JSON. A wrong JSON type must be
/// a validation error, not an exception that breaks the chat turn.</summary>
public static class A2uiJson
{
    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out bool flag) ? flag : null;

    public static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out int number) ? number : null;

    public static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out double number) ? number : null;
}
