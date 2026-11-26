using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.A2ui;

public sealed class A2uiParseResult
{
    public A2uiParseResult(IReadOnlyList<A2uiEnvelope> messages, IReadOnlyList<string> errors)
    {
        Messages = messages;
        Errors = errors;
    }

    public IReadOnlyList<A2uiEnvelope> Messages { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool Success => Errors.Count == 0;
}

/// <summary>One A2UI envelope. <see cref="Raw"/> keeps the exact message so a
/// persisted document can be replayed without re-serialising a typed model.</summary>
public sealed record A2uiEnvelope
{
    public required string Version { get; init; }
    public required JsonObject Raw { get; init; }
    public A2uiCreateSurface? CreateSurface { get; init; }
    public A2uiUpdateComponents? UpdateComponents { get; init; }
    public A2uiUpdateDataModel? UpdateDataModel { get; init; }
    public A2uiDeleteSurface? DeleteSurface { get; init; }

    public string? SurfaceId =>
        CreateSurface?.SurfaceId
        ?? UpdateComponents?.SurfaceId
        ?? UpdateDataModel?.SurfaceId
        ?? DeleteSurface?.SurfaceId;
}

public sealed record A2uiCreateSurface
{
    public required string SurfaceId { get; init; }
    public string? CatalogId { get; init; }
    public bool SendDataModel { get; init; }
    public IReadOnlyList<A2uiComponent> Components { get; init; } = [];
    public JsonObject? DataModel { get; init; }
}

public sealed record A2uiUpdateComponents
{
    public required string SurfaceId { get; init; }
    public required IReadOnlyList<A2uiComponent> Components { get; init; }
}

public sealed record A2uiUpdateDataModel
{
    public required string SurfaceId { get; init; }
    public string? Path { get; init; }
    public JsonNode? Value { get; init; }

    /// <summary>False when the envelope omitted <c>value</c>; true when it
    /// explicitly supplied null, which deletes the targeted key.</summary>
    public bool HasValue { get; init; }
}

public sealed record A2uiDeleteSurface
{
    public required string SurfaceId { get; init; }
}

public sealed record A2uiComponent
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required JsonObject Raw { get; init; }

    public JsonNode? GetNode(string name) => Raw[name];

    public string? GetString(string name) =>
        Raw[name] is JsonValue value && value.TryGetValue<string>(out string? text)
            ? text
            : null;

    public bool GetBool(string name, bool fallback = false) =>
        Raw[name] is JsonValue value && value.TryGetValue<bool>(out bool result)
            ? result
            : fallback;

    public double? GetNumber(string name) =>
        Raw[name] is JsonValue value && value.TryGetValue<double>(out double result)
            ? result
            : null;
}

/// <summary>A persisted Studio wrapper around a stream of A2UI messages.</summary>
public sealed class A2uiDocument
{
    public string Schema { get; init; } = A2uiProtocol.DocumentSchema;
    public string Origin { get; init; } = "agent";
    public string? ServerId { get; init; }
    public string? ResourceUri { get; init; }
    public string? ActionTool { get; init; }
    public IReadOnlyList<A2uiEnvelope> Messages { get; init; } = [];

    public string? SurfaceId =>
        Messages.Select(message => message.SurfaceId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

    public bool IsMcp => string.Equals(Origin, "mcp", StringComparison.OrdinalIgnoreCase);

    public string ToJson(bool indented = false)
    {
        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["origin"] = Origin,
        };
        if (!string.IsNullOrWhiteSpace(ServerId)) root["serverId"] = ServerId;
        if (!string.IsNullOrWhiteSpace(ResourceUri)) root["resourceUri"] = ResourceUri;
        if (!string.IsNullOrWhiteSpace(ActionTool)) root["actionTool"] = ActionTool;

        var messages = new JsonArray();
        foreach (A2uiEnvelope message in Messages)
            messages.Add(message.Raw.DeepClone());
        root["messages"] = messages;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }

    public string Fingerprint()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(ToJson());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public static bool TryParse(
        string? json,
        out A2uiDocument document,
        out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        document = new A2uiDocument();
        if (string.IsNullOrWhiteSpace(json))
        {
            errors = ["The A2UI document is empty."];
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(
                json,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException ex)
        {
            errors = [$"The A2UI document is not valid JSON: {ex.Message}"];
            return false;
        }

        if (node is JsonArray)
        {
            A2uiParseResult parsed = A2uiParser.Parse(json);
            document = new A2uiDocument
            {
                Origin = "agent",
                Messages = parsed.Messages,
            };
            errors = parsed.Errors;
            return parsed.Success && parsed.Messages.Count > 0;
        }

        if (node is not JsonObject root)
        {
            errors = ["The A2UI document must be a JSON object or array."];
            return false;
        }

        string schema = A2uiJson.String(root["schema"]) ?? "";
        string origin = A2uiJson.String(root["origin"]) ?? "agent";
        string? serverId = A2uiJson.String(root["serverId"]);
        string? resourceUri = A2uiJson.String(root["resourceUri"]);
        string? actionTool = A2uiJson.String(root["actionTool"]);

        JsonArray? messages = root["messages"] as JsonArray;
        if (messages is null)
        {
            // Also accept a bare stream of A2UI messages.
            A2uiParseResult parsed = A2uiParser.Parse(json);
            problems.AddRange(parsed.Errors);
            document = new A2uiDocument
            {
                Origin = origin,
                ServerId = serverId,
                ResourceUri = resourceUri,
                ActionTool = actionTool,
                Messages = parsed.Messages,
            };
            errors = problems;
            return problems.Count == 0;
        }

        var envelopes = new List<A2uiEnvelope>();
        foreach (JsonNode? item in messages)
        {
            A2uiEnvelope? envelope = A2uiParser.ParseEnvelope(item, problems);
            if (envelope is not null) envelopes.Add(envelope);
        }

        document = new A2uiDocument
        {
            Schema = string.IsNullOrWhiteSpace(schema) ? A2uiProtocol.DocumentSchema : schema,
            Origin = origin,
            ServerId = serverId,
            ResourceUri = resourceUri,
            ActionTool = actionTool,
            Messages = envelopes,
        };
        errors = problems;
        return problems.Count == 0 && envelopes.Count > 0;
    }
}
