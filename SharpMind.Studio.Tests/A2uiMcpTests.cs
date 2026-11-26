using SharpMind.Studio.Core.A2ui;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class A2uiMcpTests
{
    [Fact]
    public async Task Service_HydratesTemplateAndUpdateFromMcpTool()
    {
        await using var host = new McpServerHost();
        using var a2ui = new StudioA2uiService(new ThemeService(), host);

        await host.EnsureStartedAsync(
            FakeMcpServer.Settings(),
            () => Task.FromResult(true));

        McpToolResult result = await host.CallAsync("fake", "a2ui", null);
        Assert.False(result.IsError);
        Assert.NotEmpty(result.Content);
        Assert.Contains(result.Content, part => part.MimeType == "application/a2ui+json");
        Assert.Equal("resource",
            result.Content.First(part => part.MimeType == "application/a2ui+json").Type);
        Assert.Equal("a2ui://card",
            result.Meta!["ui"]!["resourceUri"]!.GetValue<string>());
        IReadOnlyList<McpResourceContent> template =
            await host.ReadResourceAsync("fake", "a2ui://card");
        Assert.Contains("createSurface", Assert.Single(template).Text ?? "");
        string? summary = await a2ui.AcceptMcpResultAsync("fake", result);

        Assert.NotNull(summary);
        Assert.Contains("Rendered an A2UI card.", summary);

        A2uiDocument document = Assert.Single(a2ui.DrainPending());
        Assert.Equal("mcp", document.Origin);
        Assert.Equal("fake", document.ServerId);
        Assert.Equal("a2ui://card", document.ResourceUri);
        Assert.Equal("a2ui_action", document.ActionTool);
        Assert.Contains(document.Messages, message => message.CreateSurface is not null);
        Assert.Contains(document.Messages, message => message.UpdateDataModel is not null);

        var store = new A2uiSurfaceStore();
        store.ApplyOnce(document);
        Assert.True(store.TryGet("fake_card", out A2uiSurface surface));
        Assert.Equal("Fake card", store.ResolveString(
            surface.Components["title"].GetNode("text"),
            A2uiScope.ForSurface(surface.DataModel)));
        Assert.Equal("mcp", surface.Origin);
        Assert.Equal("fake", surface.ServerId);

        A2uiActionRequest? action = store.CreateAction(
            surface,
            surface.Components["confirm"],
            A2uiScope.ForSurface(surface.DataModel));
        Assert.NotNull(action);
        Assert.Equal("a2ui_action", action!.ActionTool);

        string response = await a2ui.ExecuteMcpActionAsync(action);
        Assert.Contains("action confirm on fake_card", response);
    }
}
