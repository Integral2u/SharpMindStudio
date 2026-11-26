using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.A2ui;

/// <summary>
/// Parses A2UI 1.0 arrays, single envelopes and newline-delimited JSON. The
/// parser is deliberately permissive about unknown properties and unsupported
/// component names; the validator and renderer decide what can be shown.
/// </summary>
public static class A2uiParser
{
    private static readonly string[] MessageKeys =
    [
        "createSurface",
        "updateComponents",
        "updateDataModel",
        "deleteSurface",
    ];

    public static A2uiParseResult Parse(string? text)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add("The A2UI payload is empty.");
            return new A2uiParseResult([], errors);
        }

        if (Encoding.UTF8.GetByteCount(text) > A2uiProtocol.MaxPayloadBytes)
        {
            errors.Add(
                $"The A2UI payload is larger than {A2uiProtocol.MaxPayloadBytes / 1024} KB.");
            return new A2uiParseResult([], errors);
        }

        List<JsonNode> nodes = ReadNodes(text.Trim(), errors);
        if (nodes.Count > A2uiProtocol.MaxMessages)
        {
            errors.Add(
                $"The A2UI payload contains more than {A2uiProtocol.MaxMessages} messages.");
            return new A2uiParseResult([], errors);
        }

        var messages = new List<A2uiEnvelope>();
        foreach (JsonNode node in nodes)
        {
            A2uiEnvelope? envelope = ParseEnvelope(node, errors);
            if (envelope is not null) messages.Add(envelope);
        }

        return new A2uiParseResult(messages, errors);
    }

    private static List<JsonNode> ReadNodes(string text, List<string> errors)
    {
        JsonNode? root = TryParseNode(text);
        if (root is not null)
        {
            if (root is JsonArray array)
                return [.. array.Where(item => item is not null).Select(item => item!)];

            if (root is JsonObject obj)
            {
                if (obj["messages"] is JsonArray wrapped)
                    return [.. wrapped.Where(item => item is not null).Select(item => item!)];

                if (MessageKeys.Any(obj.ContainsKey))
                    return [obj];

                errors.Add("The A2UI object does not contain a protocol message.");
                return [];
            }

            errors.Add("The A2UI payload must be a JSON object or array of objects.");
            return [];
        }

        // JSONL: one envelope per line.
        var nodes = new List<JsonNode>();
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim().TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            JsonNode? node = TryParseNode(trimmed);
            if (node is null)
            {
                errors.Add("The A2UI payload is not valid JSON or newline-delimited JSON.");
                return [];
            }
            nodes.Add(node);
        }

        if (nodes.Count == 0)
            errors.Add("The A2UI payload is empty.");
        return nodes;
    }

    private static JsonNode? TryParseNode(string text)
    {
        try
        {
            return JsonNode.Parse(
                text,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static A2uiEnvelope? ParseEnvelope(JsonNode? node, List<string> errors)
    {
        if (node is not JsonObject message)
        {
            errors.Add("Every A2UI message must be a JSON object.");
            return null;
        }

        string version = A2uiJson.String(message["version"]) ?? A2uiProtocol.Version;
        if (!A2uiProtocol.IsSupportedVersion(version))
        {
            errors.Add($"A2UI version '{version}' is not supported; expected 1.0.");
            return null;
        }

        string[] present = [.. MessageKeys.Where(message.ContainsKey)];
        if (present.Length != 1)
        {
            errors.Add("Every A2UI message must contain exactly one message type.");
            return null;
        }

        string key = present[0];
        if (message[key] is not JsonObject body)
        {
            errors.Add($"A2UI '{key}' must be a JSON object.");
            return null;
        }

        string surfaceId = A2uiJson.String(body["surfaceId"]) ?? "";
        if (!A2uiProtocol.IsValidSurfaceId(surfaceId))
        {
            errors.Add($"A2UI surface id '{surfaceId}' is not valid.");
            return null;
        }

        return key switch
        {
            "createSurface" => ParseCreate(message, body, version, surfaceId, errors),
            "updateComponents" => ParseUpdate(message, body, version, surfaceId, errors),
            "updateDataModel" => ParseData(message, body, version, surfaceId, errors),
            "deleteSurface" => new A2uiEnvelope
            {
                Version = version,
                Raw = (JsonObject)message.DeepClone(),
                DeleteSurface = new A2uiDeleteSurface { SurfaceId = surfaceId },
            },
            _ => null,
        };
    }

    private static A2uiEnvelope? ParseCreate(
        JsonObject message,
        JsonObject body,
        string version,
        string surfaceId,
        List<string> errors)
    {
        string? catalogId = A2uiJson.String(body["catalogId"]);
        bool sendDataModel = body["sendDataModel"] is JsonValue send && send.TryGetValue<bool>(out bool flag)
            && flag;

        IReadOnlyList<A2uiComponent> components = ParseComponents(body["components"], errors, surfaceId);

        JsonObject? dataModel = null;
        if (body["dataModel"] is { } model)
        {
            if (model is JsonObject obj)
                dataModel = (JsonObject)obj.DeepClone();
            else
                errors.Add("A2UI createSurface.dataModel must be an object.");
        }

        return new A2uiEnvelope
        {
            Version = version,
            Raw = (JsonObject)message.DeepClone(),
            CreateSurface = new A2uiCreateSurface
            {
                SurfaceId = surfaceId,
                CatalogId = string.IsNullOrWhiteSpace(catalogId) ? null : catalogId,
                SendDataModel = sendDataModel,
                Components = components,
                DataModel = dataModel,
            },
        };
    }

    private static A2uiEnvelope? ParseUpdate(
        JsonObject message,
        JsonObject body,
        string version,
        string surfaceId,
        List<string> errors)
    {
        if (body["components"] is not JsonArray)
        {
            errors.Add("A2UI updateComponents.components must be an array.");
            return null;
        }

        IReadOnlyList<A2uiComponent> components = ParseComponents(body["components"], errors, surfaceId);
        return new A2uiEnvelope
        {
            Version = version,
            Raw = (JsonObject)message.DeepClone(),
            UpdateComponents = new A2uiUpdateComponents
            {
                SurfaceId = surfaceId,
                Components = components,
            },
        };
    }

    private static A2uiEnvelope ParseData(
        JsonObject message,
        JsonObject body,
        string version,
        string surfaceId,
        List<string> errors)
    {
        string? path = A2uiJson.String(body["path"]);
        bool hasValue = body.ContainsKey("value");
        JsonNode? value = hasValue ? body["value"]?.DeepClone() : null;
        if (!hasValue)
            errors.Add("A2UI updateDataModel.value is required.");

        return new A2uiEnvelope
        {
            Version = version,
            Raw = (JsonObject)message.DeepClone(),
            UpdateDataModel = new A2uiUpdateDataModel
            {
                SurfaceId = surfaceId,
                Path = string.IsNullOrWhiteSpace(path) ? null : path,
                Value = value,
                HasValue = hasValue,
            },
        };
    }

    private static IReadOnlyList<A2uiComponent> ParseComponents(
        JsonNode? node,
        List<string> errors,
        string surfaceId)
    {
        if (node is null) return [];
        if (node is not JsonArray array)
        {
            errors.Add("A2UI components must be an array.");
            return [];
        }

        var components = new List<A2uiComponent>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject component)
            {
                errors.Add("Every A2UI component must be a JSON object.");
                continue;
            }

            string id = A2uiJson.String(component["id"]) ?? "";
            string type = A2uiJson.String(component["component"]) ?? "";
            if (!A2uiProtocol.IsValidId(id))
            {
                errors.Add($"A2UI component id '{id}' is not valid.");
                continue;
            }
            if (type.Length == 0)
            {
                errors.Add($"A2UI component '{id}' has no component type.");
                continue;
            }
            if (!ids.Add(id))
            {
                errors.Add($"A2UI component '{id}' appears more than once in one message.");
                continue;
            }

            components.Add(new A2uiComponent
            {
                Id = id,
                Type = type,
                Raw = (JsonObject)component.DeepClone(),
            });
        }

        return components;
    }
}
