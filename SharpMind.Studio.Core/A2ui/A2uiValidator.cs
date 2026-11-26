using System.Text;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.A2ui;

public sealed class A2uiValidationResult
{
    public A2uiValidationResult(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        Errors = errors;
        Warnings = warnings;
    }

    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Structural checks that must pass before a document is persisted or rendered.
/// Missing child references are warnings because A2UI allows progressive
/// rendering; missing roots, cycles and oversized payloads are errors.
/// </summary>
public static class A2uiValidator
{
    private const int MaxComponentBytes = 64 * 1024;

    public static A2uiValidationResult Validate(A2uiDocument document)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (document.Messages.Count == 0)
            errors.Add("The A2UI document contains no messages.");

        var surfaces = new Dictionary<string, Dictionary<string, A2uiComponent>>(
            StringComparer.Ordinal);

        foreach (A2uiEnvelope message in document.Messages)
        {
            string surfaceId = message.SurfaceId ?? "";
            if (surfaceId.Length == 0)
            {
                errors.Add("An A2UI message has no surface id.");
                continue;
            }

            if (message.CreateSurface is { } create)
            {
                if (create.CatalogId is { Length: > 0 } catalog &&
                    !string.Equals(catalog, A2uiProtocol.BasicCatalogId, StringComparison.Ordinal))
                {
                    warnings.Add(
                        $"Surface '{surfaceId}' uses catalog '{catalog}', which this renderer does not support.");
                }

                if (create.DataModel is not null)
                {
                    int bytes = Encoding.UTF8.GetByteCount(create.DataModel.ToJsonString());
                    if (bytes > A2uiProtocol.MaxDataModelBytes)
                        errors.Add($"Surface '{surfaceId}' data model is too large.");
                }

                AddComponents(surfaceId, create.Components, surfaces, errors, warnings);
            }
            else if (message.UpdateComponents is { } update)
            {
                AddComponents(surfaceId, update.Components, surfaces, errors, warnings);
            }
            else if (message.UpdateDataModel is { } data && data.HasValue && data.Value is JsonObject value)
            {
                int bytes = Encoding.UTF8.GetByteCount(value.ToJsonString());
                if (bytes > A2uiProtocol.MaxDataModelBytes)
                    errors.Add($"Surface '{surfaceId}' data model is too large.");
            }
        }

        if (surfaces.Count > A2uiProtocol.MaxSurfaces)
            errors.Add($"An A2UI document may contain at most {A2uiProtocol.MaxSurfaces} surfaces.");

        foreach ((string surfaceId, Dictionary<string, A2uiComponent> components) in surfaces)
        {
            if (components.Count > A2uiProtocol.MaxComponentsPerSurface)
            {
                errors.Add(
                    $"Surface '{surfaceId}' has more than {A2uiProtocol.MaxComponentsPerSurface} components.");
            }

            if (components.Count == 0) continue;
            if (!components.ContainsKey("root"))
            {
                errors.Add($"Surface '{surfaceId}' has no root component.");
                continue;
            }

            foreach (A2uiComponent component in components.Values)
            {
                foreach (string reference in References(component))
                {
                    if (!components.ContainsKey(reference))
                    {
                        warnings.Add(
                            $"A2UI component '{component.Id}' references missing component '{reference}'.");
                    }
                }
            }

            int depth = 0;
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            if (HasCycle("root", components, visiting, visited, ref depth))
                errors.Add($"Surface '{surfaceId}' contains a component cycle.");
            if (depth > A2uiProtocol.MaxDepth)
                errors.Add($"Surface '{surfaceId}' is nested more than {A2uiProtocol.MaxDepth} levels.");
        }

        return new A2uiValidationResult(errors, warnings);
    }

    private static void AddComponents(
        string surfaceId,
        IReadOnlyList<A2uiComponent> components,
        Dictionary<string, Dictionary<string, A2uiComponent>> surfaces,
        List<string> errors,
        List<string> warnings)
    {
        if (!surfaces.TryGetValue(surfaceId, out Dictionary<string, A2uiComponent>? known))
        {
            known = new Dictionary<string, A2uiComponent>(StringComparer.Ordinal);
            surfaces[surfaceId] = known;
        }

        foreach (A2uiComponent component in components)
        {
            int bytes = Encoding.UTF8.GetByteCount(component.Raw.ToJsonString());
            if (bytes > MaxComponentBytes)
                errors.Add($"A2UI component '{component.Id}' is too large.");
            if (!A2uiProtocol.IsSupportedComponent(component.Type))
                warnings.Add($"A2UI component '{component.Type}' is not supported and will be shown as a placeholder.");

            string? text = component.GetString("text");
            if (text is { Length: > A2uiProtocol.MaxTextLength })
                errors.Add($"A2UI text in component '{component.Id}' is too long.");

            known[component.Id] = component;
        }
    }

    private static IEnumerable<string> References(A2uiComponent component)
    {
        if (component.Raw["child"] is JsonValue child &&
            child.TryGetValue<string>(out string? childId) &&
            !string.IsNullOrWhiteSpace(childId))
        {
            yield return childId;
        }

        if (component.Raw["children"] is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonValue value &&
                    value.TryGetValue<string>(out string? id) &&
                    !string.IsNullOrWhiteSpace(id))
                {
                    yield return id;
                }
            }
        }
        else if (component.Raw["children"] is JsonObject template &&
                 template["componentId"] is JsonValue templateId &&
                 templateId.TryGetValue<string>(out string? id) &&
                 !string.IsNullOrWhiteSpace(id))
        {
            yield return id;
        }

        if (component.Raw["trigger"] is JsonValue trigger &&
            trigger.TryGetValue<string>(out string? triggerId) &&
            !string.IsNullOrWhiteSpace(triggerId))
        {
            yield return triggerId;
        }

        if (component.Raw["content"] is JsonValue content &&
            content.TryGetValue<string>(out string? contentId) &&
            !string.IsNullOrWhiteSpace(contentId))
        {
            yield return contentId;
        }
    }

    private static bool HasCycle(
        string id,
        Dictionary<string, A2uiComponent> components,
        HashSet<string> visiting,
        HashSet<string> visited,
        ref int depth)
    {
        if (visited.Contains(id)) return false;
        if (!visiting.Add(id)) return true;
        if (!components.TryGetValue(id, out A2uiComponent? component))
        {
            visiting.Remove(id);
            return false;
        }

        depth = Math.Max(depth, visiting.Count);
        foreach (string reference in References(component))
        {
            if (HasCycle(reference, components, visiting, visited, ref depth))
                return true;
        }

        visiting.Remove(id);
        visited.Add(id);
        return false;
    }
}
