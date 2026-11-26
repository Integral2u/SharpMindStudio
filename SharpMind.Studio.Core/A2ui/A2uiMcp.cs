using System.Text.Json.Nodes;

namespace SharpMind.Studio.Core.A2ui;

/// <summary>Capability payloads advertised to MCP servers that can return A2UI.</summary>
public static class A2uiMcp
{
    public static JsonObject Capabilities() => new()
    {
        ["a2ui"] = new JsonObject
        {
            ["rendererCapabilities"] = new JsonObject
            {
                [A2uiProtocol.Version] = new JsonObject
                {
                    ["supportedCatalogIds"] = new JsonArray(A2uiProtocol.BasicCatalogId),
                },
            },
        },
    };

    /// <summary>Per-call metadata for stateless HTTP MCP servers.</summary>
    public static JsonObject CallMeta() => Capabilities();
}
