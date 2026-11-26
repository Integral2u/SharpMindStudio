using System.Text.Json.Nodes;
using SharpMind.Studio.Core.A2ui;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class A2uiTests
{
    private const string Form = """
        [
          {
            "version": "1.0",
            "createSurface": {
              "surfaceId": "form",
              "catalogId": "https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json",
              "sendDataModel": true,
              "dataModel": { "name": "Ada", "agents": [ "a", "b" ] }
            }
          },
          {
            "version": "1.0",
            "updateComponents": {
              "surfaceId": "form",
              "components": [
                { "id": "root", "component": "Column", "children": [ "title", "name", "list", "submit" ] },
                { "id": "title", "component": "Text", "text": { "@path": "/name" } },
                { "id": "name", "component": "TextField", "label": "Name", "value": { "@path": "/name" } },
                { "id": "list", "component": "List", "children": { "componentId": "agent", "path": "/agents" } },
                { "id": "agent", "component": "Row", "children": [ "agent_text" ] },
                { "id": "agent_text", "component": "Text", "text": { "@call": "@index" } },
                {
                  "id": "submit",
                  "component": "Button",
                  "child": "submit_text",
                  "action": { "event": { "name": "save", "context": { "name": { "@path": "/name" } } } }
                },
                { "id": "submit_text", "component": "Text", "text": "Save" }
              ]
            }
          }
        ]
        """;

    [Fact]
    public void Parse_AppliesFormAndResolvesBindings()
    {
        A2uiParseResult parsed = A2uiParser.Parse(Form);
        Assert.True(parsed.Success, string.Join(" ", parsed.Errors));

        var document = new A2uiDocument { Messages = parsed.Messages };
        var store = new A2uiSurfaceStore();
        Assert.True(store.ApplyOnce(document));

        Assert.True(store.TryGet("form", out A2uiSurface surface));
        Assert.Equal("root", surface.Root?.Id);
        Assert.Equal("Ada", store.ResolveString(
            surface.Components["title"].GetNode("text"),
            A2uiScope.ForSurface(surface.DataModel)));

        IReadOnlyList<A2uiChild> children = store.Children(
            surface,
            surface.Components["list"],
            A2uiScope.ForSurface(surface.DataModel));
        Assert.Equal(2, children.Count);
        Assert.Equal("agent#0", children[0].Key);
        Assert.Equal("0", store.ResolveString(
            surface.Components["agent_text"].GetNode("text"),
            children[0].Scope));
        Assert.Equal("1", store.ResolveString(
            surface.Components["agent_text"].GetNode("text"),
            children[1].Scope));
    }

    [Fact]
    public void Action_ResolvesContextAndDataModel()
    {
        A2uiParseResult parsed = A2uiParser.Parse(Form);
        var document = new A2uiDocument { Messages = parsed.Messages };
        var store = new A2uiSurfaceStore();
        store.ApplyOnce(document);
        Assert.True(store.TryGet("form", out A2uiSurface surface));

        A2uiActionRequest? action = store.CreateAction(
            surface,
            surface.Components["submit"],
            A2uiScope.ForSurface(surface.DataModel));

        Assert.NotNull(action);
        Assert.Equal("save", action!.Name);
        Assert.Equal("form", action.SurfaceId);
        Assert.Equal("submit", action.SourceComponentId);
        Assert.Equal("Ada", action.Context["name"]?.GetValue<string>());
        Assert.NotNull(action.DataModel);
    }

    [Fact]
    public void UpdateDataModel_ReplacesAndDeletesValues()
    {
        var store = new A2uiSurfaceStore();
        store.Apply([Envelope("""
            { "version": "1.0", "createSurface": { "surfaceId": "s1", "dataModel": { "a": 1, "b": 2 } } }
            """)]);
        store.Apply([Envelope("""
            { "version": "1.0", "updateDataModel": { "surfaceId": "s1", "path": "/a", "value": null } }
            """)]);

        Assert.True(store.TryGet("s1", out A2uiSurface surface));
        Assert.Null(surface.DataModel["a"]);
        Assert.Equal(2, surface.DataModel["b"]?.GetValue<int>());
    }

    [Fact]
    public void Document_AndArtifact_RoundTrip()
    {
        A2uiParseResult parsed = A2uiParser.Parse(Form);
        var document = new A2uiDocument
        {
            Origin = "mcp",
            ServerId = "recipes",
            ResourceUri = "a2ui://recipe-card",
            ActionTool = "a2ui_action",
            Messages = parsed.Messages,
        };

        var artifact = A2uiArtifact.Create(document);
        Assert.True(A2uiArtifact.TryRead(artifact, out A2uiDocument loaded, out IReadOnlyList<string> errors),
            string.Join(" ", errors));
        Assert.Equal("mcp", loaded.Origin);
        Assert.Equal("recipes", loaded.ServerId);
        Assert.Equal("a2ui://recipe-card", loaded.ResourceUri);
        Assert.Equal("form", loaded.SurfaceId);

        var store = new A2uiSurfaceStore();
        store.ApplyOnce(loaded);
        Assert.True(store.TryGet("form", out A2uiSurface surface));
        Assert.Equal("mcp", surface.Origin);
        Assert.Equal("recipes", surface.ServerId);
    }

    [Fact]
    public void Validator_RejectsMissingRootAndCycles()
    {
        var missingRoot = new A2uiDocument
        {
            Messages =
            [
                Envelope("""
                    { "version": "1.0", "updateComponents": { "surfaceId": "s", "components": [
                        { "id": "a", "component": "Text", "text": "x" }
                    ] } }
                    """),
            ],
        };
        Assert.Contains(A2uiValidator.Validate(missingRoot).Errors,
            error => error.Contains("no root", StringComparison.OrdinalIgnoreCase));

        var cycle = new A2uiDocument
        {
            Messages =
            [
                Envelope("""
                    { "version": "1.0", "updateComponents": { "surfaceId": "s", "components": [
                        { "id": "root", "component": "Column", "children": [ "a" ] },
                        { "id": "a", "component": "Column", "children": [ "root" ] }
                    ] } }
                    """),
            ],
        };
        Assert.Contains(A2uiValidator.Validate(cycle).Errors,
            error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsOversizedText()
    {
        string text = new('x', A2uiProtocol.MaxTextLength + 1);
        var document = new A2uiDocument
        {
            Messages =
            [
                Envelope($$"""
                    { "version": "1.0", "updateComponents": { "surfaceId": "s", "components": [
                        { "id": "root", "component": "Text", "text": {{System.Text.Json.JsonSerializer.Serialize(text)}} }
                    ] } }
                    """),
            ],
        };

        Assert.Contains(A2uiValidator.Validate(document).Errors,
            error => error.Contains("too long", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parser_AllowsHyphenatedSurfaceIds()
    {
        A2uiParseResult parsed = A2uiParser.Parse("""
            { "version": "1.0", "createSurface": { "surfaceId": "recipe-card" } }
            """);

        Assert.True(parsed.Success, string.Join(" ", parsed.Errors));
        Assert.Equal("recipe-card", Assert.Single(parsed.Messages).SurfaceId);
    }

    [Fact]
    public void Parser_RejectsUnknownVersionAndMalformedEnvelope()
    {
        A2uiParseResult version = A2uiParser.Parse("""
            { "version": "0.9", "createSurface": { "surfaceId": "s" } }
            """);
        Assert.False(version.Success);
        Assert.Contains(version.Errors, error => error.Contains("version", StringComparison.OrdinalIgnoreCase));

        A2uiParseResult both = A2uiParser.Parse("""
            { "version": "1.0", "createSurface": { "surfaceId": "s" }, "deleteSurface": { "surfaceId": "s" } }
            """);
        Assert.False(both.Success);
        Assert.Contains(both.Errors, error => error.Contains("exactly one", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Text_HidesCompleteAndOpenBlocks()
    {
        string content = """
            Before
            ```a2ui
            { "version": "1.0", "createSurface": { "surfaceId": "s" } }
            ```
            After
            """;

        A2uiExtraction extracted = A2uiText.Extract(content);
        Assert.Single(extracted.Blocks);
        Assert.DoesNotContain("a2ui", extracted.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Before", extracted.Content);
        Assert.Contains("After", extracted.Content);

        string open = "Before\n```a2ui\n{ \"version\": \"1.0\"";
        string hidden = A2uiText.HideStreaming(open);
        Assert.Contains("Before", hidden);
        Assert.Contains("Building interface", hidden);
        Assert.DoesNotContain("\"version\"", hidden);
    }

    [Fact]
    public void TurnExtractor_SplitsSurfacesAndStripsTheFence()
    {
        string content = """
            Before
            ```a2ui
            [
              { "version": "1.0", "createSurface": { "surfaceId": "a", "dataModel": {} } },
              { "version": "1.0", "updateComponents": { "surfaceId": "a", "components": [
                { "id": "root", "component": "Text", "text": "A" }
              ] } },
              { "version": "1.0", "createSurface": { "surfaceId": "b", "dataModel": {} } },
              { "version": "1.0", "updateComponents": { "surfaceId": "b", "components": [
                { "id": "root", "component": "Text", "text": "B" }
              ] } }
            ]
            ```
            After
            """;

        A2uiTurnResult result = A2uiTurnExtractor.Extract(content);

        Assert.False(result.Rejected);
        Assert.Equal(2, result.Documents.Count);
        Assert.Contains("Before", result.Content);
        Assert.Contains("After", result.Content);
        Assert.DoesNotContain("```a2ui", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "a", "b" },
            result.Documents.Select(document => document.SurfaceId).ToArray());
        Assert.All(result.Documents, document => Assert.Equal("agent", document.Origin));
    }

    [Fact]
    public void TurnExtractor_RejectsInvalidBlockButKeepsSurroundingText()
    {
        string content = """
            Here is the answer.
            ```a2ui
            { "version": "1.0", "updateComponents": { "surfaceId": "s", "components": [
              { "id": "a", "component": "Text", "text": "no root" }
            ] } }
            ```
            """;

        A2uiTurnResult result = A2uiTurnExtractor.Extract(content);

        Assert.True(result.Rejected);
        Assert.Empty(result.Documents);
        Assert.Contains("Here is the answer.", result.Content);
        Assert.DoesNotContain("a2ui", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Service_ExtractsFencedBlockIntoArtifactAndStore()
    {
        await using var host = new SharpMind.Studio.Core.Mcp.McpServerHost();
        using var service = new StudioA2uiService(new ThemeService(), host);
        var message = SharpMind.Inference.Chat.ChatMessage.Agent(
            "Here is the form.\n```a2ui\n" + Form + "\n```\nFill it in.");

        int applied = service.ApplyToMessages([message]);

        Assert.Equal(1, applied);
        Assert.Contains("Here is the form.", message.Content);
        Assert.DoesNotContain("```a2ui", message.Content, StringComparison.OrdinalIgnoreCase);
        SharpMind.Inference.Chat.ChatArtifact artifact = Assert.Single(
            message.Artifacts ?? [],
            A2uiArtifact.IsA2ui);
        Assert.True(A2uiArtifact.TryRead(artifact, out A2uiDocument document, out _));
        Assert.Equal("form", document.SurfaceId);
        Assert.True(service.Store.TryGet("form", out _));
    }

    private static A2uiEnvelope Envelope(string json)
    {
        A2uiParseResult parsed = A2uiParser.Parse(json);
        Assert.True(parsed.Success, string.Join(" ", parsed.Errors));
        return Assert.Single(parsed.Messages);
    }
}
