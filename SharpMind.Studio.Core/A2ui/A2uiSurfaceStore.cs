using System.Globalization;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.A2ui;

/// <summary>One live surface: component definitions plus the data model that
/// component properties bind to.</summary>
public sealed class A2uiSurface
{
    public A2uiSurface(string id) => Id = id;

    public string Id { get; }
    public string? CatalogId { get; private set; }
    public bool SendDataModel { get; private set; }
    public JsonObject DataModel { get; private set; } = new();
    public Dictionary<string, A2uiComponent> Components { get; } =
        new(StringComparer.Ordinal);
    public bool Removed { get; private set; }
    public string Origin { get; private set; } = "agent";
    public string? ServerId { get; private set; }
    public string? ActionTool { get; private set; }

    public A2uiComponent? Root =>
        Components.TryGetValue("root", out A2uiComponent? root) ? root : null;

    internal void Create(A2uiCreateSurface create)
    {
        CatalogId = create.CatalogId;
        SendDataModel = create.SendDataModel;
        if (create.DataModel is not null)
            DataModel = (JsonObject)create.DataModel.DeepClone();
        Apply(create.Components);
    }

    internal void Apply(IEnumerable<A2uiComponent> components)
    {
        foreach (A2uiComponent component in components)
            Components[component.Id] = component;
    }

    internal void SetDataModel(string? path, JsonNode? value, bool hasValue)
    {
        if (!hasValue) return;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            DataModel = value is JsonObject obj
                ? (JsonObject)obj.DeepClone()
                : new JsonObject();
            return;
        }

        string[] tokens = A2uiJsonPointer.Tokens(path);
        if (tokens.Length == 0)
        {
            DataModel = value is JsonObject root
                ? (JsonObject)root.DeepClone()
                : new JsonObject();
            return;
        }

        A2uiJsonPointer.Set(DataModel, tokens, value);
    }

    internal void Remove() => Removed = true;

    internal void SetOrigin(string origin, string? serverId, string? actionTool)
    {
        Origin = string.IsNullOrWhiteSpace(origin) ? "agent" : origin;
        ServerId = serverId;
        ActionTool = actionTool;
    }
}

public sealed record A2uiChild(string ComponentId, A2uiScope Scope, string Key);

public sealed record A2uiActionRequest(
    string Name,
    string SurfaceId,
    string SourceComponentId,
    JsonObject Context,
    JsonObject? DataModel,
    string Origin,
    string? ServerId,
    string? ActionTool);

/// <summary>An evaluation scope for data bindings. A list template supplies the
/// current item and index; everything else resolves against the surface root.</summary>
public sealed record A2uiScope(
    JsonObject Root,
    JsonNode? Current = null,
    int? Index = null,
    string? CurrentPath = null)
{
    public static A2uiScope ForSurface(JsonObject root) => new(root);

    public JsonNode? Resolve(string? path)
    {
        JsonNode? start = string.IsNullOrWhiteSpace(path) || path.StartsWith('/')
            ? Root
            : Current ?? Root;

        string value = path?.Trim() ?? "";
        if (value.Length == 0 || value == "/") return start;

        foreach (string token in A2uiJsonPointer.Tokens(value))
        {
            if (start is JsonObject obj)
            {
                start = obj[token];
            }
            else if (start is JsonArray array &&
                     int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
                     index >= 0 && index < array.Count)
            {
                start = array[index];
            }
            else
            {
                return null;
            }
        }

        return start;
    }
}

/// <summary>Owns the live surfaces for one chat or Labs session. Applying a
/// document is idempotent by content fingerprint so Blazor re-renders can ask
/// each surface to load itself without replaying user edits.</summary>
public sealed class A2uiSurfaceStore
{
    private readonly Dictionary<string, A2uiSurface> _surfaces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyDictionary<string, A2uiSurface> Surfaces => _surfaces;

    public bool TryGet(string surfaceId, out A2uiSurface surface) =>
        _surfaces.TryGetValue(surfaceId, out surface!);

    public bool ApplyOnce(A2uiDocument document)
    {
        if (!_applied.Add(document.Fingerprint())) return false;
        return Apply(document.Messages, document);
    }

    public void Reset()
    {
        _surfaces.Clear();
        _applied.Clear();
        Changed?.Invoke();
    }

    public bool Apply(IReadOnlyList<A2uiEnvelope> messages, A2uiDocument? document = null)
    {
        bool changed = false;
        foreach (A2uiEnvelope message in messages)
        {
            string surfaceId = message.SurfaceId ?? "";
            if (surfaceId.Length == 0) continue;

            switch (message)
            {
                case { CreateSurface: { } create }:
                    if (_surfaces.TryGetValue(surfaceId, out A2uiSurface? existing))
                    {
                        // A cached template may be re-sent; keep the live data model
                        // and the surface's original origin.
                        if (create.Components.Count > 0)
                        {
                            existing.Apply(create.Components);
                            changed = true;
                        }
                        break;
                    }
                    var created = new A2uiSurface(surfaceId);
                    created.SetOrigin(document?.Origin ?? "agent", document?.ServerId, document?.ActionTool);
                    created.Create(create);
                    _surfaces[surfaceId] = created;
                    changed = true;
                    break;

                case { UpdateComponents: { } update }:
                    if (!_surfaces.TryGetValue(surfaceId, out A2uiSurface? target))
                    {
                        target = new A2uiSurface(surfaceId);
                        target.SetOrigin(document?.Origin ?? "agent", document?.ServerId, document?.ActionTool);
                        _surfaces[surfaceId] = target;
                    }
                    target.Apply(update.Components);
                    changed = true;
                    break;

                case { UpdateDataModel: { } data }:
                    if (!_surfaces.TryGetValue(surfaceId, out A2uiSurface? model))
                    {
                        model = new A2uiSurface(surfaceId);
                        model.SetOrigin(document?.Origin ?? "agent", document?.ServerId, document?.ActionTool);
                        _surfaces[surfaceId] = model;
                    }
                    model.SetDataModel(data.Path, data.Value, data.HasValue);
                    changed = true;
                    break;

                case { DeleteSurface: { } delete }:
                    if (_surfaces.TryGetValue(surfaceId, out A2uiSurface? removed))
                        removed.Remove();
                    _surfaces.Remove(surfaceId);
                    changed = true;
                    break;
            }
        }

        if (changed) Changed?.Invoke();
        return changed;
    }

    public JsonNode? Resolve(JsonNode? value, A2uiScope scope)
    {
        if (value is not JsonObject obj)
            return value?.DeepClone();

        if (obj["@path"] is JsonValue pathValue &&
            pathValue.TryGetValue<string>(out string? path))
        {
            return scope.Resolve(path)?.DeepClone();
        }

        if (obj["@call"] is JsonValue callValue &&
            callValue.TryGetValue<string>(out string? call))
        {
            return Evaluate(call, obj, scope);
        }

        // A literal object may still contain nested bindings.
        var copy = new JsonObject();
        foreach ((string key, JsonNode? item) in obj)
            copy[key] = Resolve(item, scope);
        return copy;
    }

    private JsonNode? Evaluate(string call, JsonObject expression, A2uiScope scope)
    {
        string name = call.TrimStart('@');
        if (!string.Equals(name, "index", StringComparison.OrdinalIgnoreCase))
            return null;

        int offset = 0;
        if (expression["args"] is JsonObject args &&
            args["offset"] is JsonValue offsetValue &&
            offsetValue.TryGetValue<int>(out int parsed))
        {
            offset = parsed;
        }

        return JsonValue.Create((scope.Index ?? 0) + offset);
    }

    public string? ResolveString(JsonNode? value, A2uiScope scope)
    {
        JsonNode? resolved = Resolve(value, scope);
        return resolved switch
        {
            null => null,
            JsonValue scalar when scalar.TryGetValue<string>(out string? text) => text,
            JsonValue scalar => scalar.ToJsonString(),
            _ => resolved.ToJsonString(),
        };
    }

    public bool ResolveBool(JsonNode? value, A2uiScope scope, bool fallback = false)
    {
        JsonNode? resolved = Resolve(value, scope);
        if (resolved is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out bool flag)) return flag;
            if (scalar.TryGetValue<string>(out string? text) && bool.TryParse(text, out bool parsed))
                return parsed;
        }
        return fallback;
    }

    public double? ResolveNumber(JsonNode? value, A2uiScope scope)
    {
        JsonNode? resolved = Resolve(value, scope);
        if (resolved is JsonValue scalar)
        {
            if (scalar.TryGetValue<double>(out double number)) return number;
            if (scalar.TryGetValue<string>(out string? text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    public IReadOnlyList<string> ResolveStringList(JsonNode? value, A2uiScope scope)
    {
        JsonNode? resolved = Resolve(value, scope);
        if (resolved is JsonArray array)
        {
            return [.. array
                .Select(item => item is JsonValue scalar && scalar.TryGetValue<string>(out string? text)
                    ? text
                    : item?.ToJsonString() ?? "")
                .Where(item => item.Length > 0)];
        }
        if (resolved is JsonValue single && single.TryGetValue<string>(out string? text) &&
            !string.IsNullOrWhiteSpace(text))
        {
            return [text];
        }
        return [];
    }

    public IReadOnlyList<A2uiChild> Children(
        A2uiSurface surface,
        A2uiComponent component,
        A2uiScope scope)
    {
        if (component.Raw["child"] is JsonValue child &&
            child.TryGetValue<string>(out string? childId) &&
            !string.IsNullOrWhiteSpace(childId))
        {
            return [new A2uiChild(childId, scope, childId)];
        }

        if (component.Raw["children"] is JsonArray array)
        {
            var children = new List<A2uiChild>();
            foreach (JsonNode? item in array)
            {
                if (item is JsonValue value &&
                    value.TryGetValue<string>(out string? id) &&
                    !string.IsNullOrWhiteSpace(id))
                {
                    children.Add(new A2uiChild(id, scope, id));
                }
            }
            return children;
        }

        if (component.Raw["children"] is JsonObject template &&
            template["componentId"] is JsonValue templateId &&
            templateId.TryGetValue<string>(out string? idValue) &&
            !string.IsNullOrWhiteSpace(idValue) &&
            template["path"] is JsonValue templatePath &&
            templatePath.TryGetValue<string>(out string? path))
        {
            JsonNode? items = scope.Resolve(path);
            if (items is not JsonArray list) return [];

            var children = new List<A2uiChild>();
            for (int index = 0; index < list.Count; index++)
            {
                JsonNode? item = list[index];
                var itemScope = new A2uiScope(
                    scope.Root,
                    item,
                    index,
                    path.TrimEnd('/') + "/" + index.ToString(CultureInfo.InvariantCulture));
                children.Add(new A2uiChild(idValue, itemScope, $"{idValue}#{index}"));
            }
            return children;
        }

        return [];
    }

    public A2uiActionRequest? CreateAction(
        A2uiSurface surface,
        A2uiComponent component,
        A2uiScope scope)
    {
        if (component.Raw["action"] is not JsonObject action) return null;
        if (action["event"] is not JsonObject eventAction) return null;

        string name = A2uiJson.String(eventAction["name"]) ?? "";
        if (name.Length == 0) return null;

        var context = new JsonObject();
        if (eventAction["context"] is JsonObject supplied)
        {
            foreach ((string key, JsonNode? value) in supplied)
                context[key] = Resolve(value, scope);
        }

        return new A2uiActionRequest(
            name,
            surface.Id,
            component.Id,
            context,
            surface.SendDataModel ? (JsonObject)surface.DataModel.DeepClone() : null,
            surface.Origin,
            surface.ServerId,
            surface.ActionTool);
    }

    public void SetDataModel(string surfaceId, string path, JsonNode? value)
    {
        if (!_surfaces.TryGetValue(surfaceId, out A2uiSurface? surface)) return;
        surface.SetDataModel(path, value, hasValue: true);
        Changed?.Invoke();
    }
}

internal static class A2uiJsonPointer
{
    public static string[] Tokens(string path)
    {
        string value = path.Trim();
        if (value.StartsWith('/')) value = value[1..];
        if (value.Length == 0) return [];
        return [.. value.Split('/').Select(Unescape)];
    }

    private static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal)
             .Replace("~0", "~", StringComparison.Ordinal);

    public static void Set(JsonObject root, string[] tokens, JsonNode? value)
    {
        JsonNode? current = root;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            string token = tokens[i];
            if (current is not JsonObject obj) return;

            JsonNode? next = obj[token];
            if (next is null)
            {
                next = new JsonObject();
                obj[token] = next;
            }
            current = next;
        }

        string last = tokens[^1];
        if (current is JsonObject target)
        {
            if (value is null) target.Remove(last);
            else target[last] = value.DeepClone();
        }
        else if (current is JsonArray array &&
                 int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            if (index < 0 || index >= array.Count) return;
            if (value is null) array.RemoveAt(index);
            else array[index] = value.DeepClone();
        }
    }
}
