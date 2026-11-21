using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SharpMind.Inference.Agent;
using SharpMind.Studio;
using SharpMind.Studio.Core.Agents;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Core.Vault;
using SharpMind.Studio.Core.Work;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// A delegated agent uses the project's enabled tools. MCP tools were only
/// registered on the parent builder, so a specialist silently lost every server
/// tool its parent had. These tests pin the consent rule on both paths: an MCP
/// tool needs an explicit project or session entry, an agent can narrow that
/// set with its allow list, and it can never grant itself one.
/// </summary>
[Collection("studio-paths")]
public sealed class McpAgentToolTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StudioAgentService _agents;
    private readonly WorkToolService _tools;
    private readonly McpServerHost _mcp;
    private readonly TestSecretResolver _secrets;
    private readonly StudioWorkService _work;

    public McpAgentToolTests()
    {
        StudioPaths.UseRoot(_temp.Path);
        var options = Options.Create(new StudioOptions
        {
            ProjectRoot = _temp.Combine("Project"),
            ModelsFolder = _temp.Combine("Models"),
            CorpusFolder = _temp.Combine("Corpus"),
            TrainingFolder = _temp.Combine("Training"),
            PluginsFolder = _temp.Combine("Plugins"),
        });
        var environment = new TestHostEnvironment { ContentRootPath = _temp.Path };
        var workspace = new StudioWorkspaceService(options, environment);
        _agents = new StudioAgentService(workspace);
        _tools = new WorkToolService(workspace);
        _secrets = new TestSecretResolver(("TOKEN", "s3cret-value"));
        _mcp = new McpServerHost(_secrets);
        _work = new StudioWorkService(
            workspace,
            new SystemWorkSkillsService(),
            _tools,
            new StudioDeviceAccess(),
            _agents,
            new InferenceSettingsService(),
            _mcp,
            _secrets);
    }

    [Fact]
    public async Task ParentAndAgent_BothGetAnAllowedMcpTool()
    {
        await ConnectAsync();
        ToolPermissionProfile profile = ConsentedTo(WorkToolPermission.Always);

        var parent = (AgentBuilder)_work.CreateAgent("work", profile: profile)!;
        var agent = (AgentBuilder)_work.CreateAgentTools(Agent(), profile)!;

        Assert.Contains(FakeMcpServer.EchoTool, parent.RegisteredToolNames);
        Assert.Contains(FakeMcpServer.EchoTool, agent.RegisteredToolNames);
    }

    [Fact]
    public async Task Agent_DoesNotGetAnMcpToolNobodyAllowed()
    {
        await ConnectAsync();

        var agent = (AgentBuilder)_work.CreateAgentTools(Agent(), new ToolPermissionProfile())!;

        Assert.DoesNotContain(FakeMcpServer.EchoTool, agent.RegisteredToolNames);
    }

    [Fact]
    public async Task Agent_AllowListNarrowsTheMcpToolsItInherits()
    {
        await ConnectAsync();
        var definition = Agent() with { AllowedTools = ["ReadFile"] };

        var agent = (AgentBuilder)_work.CreateAgentTools(
            definition,
            ConsentedTo(WorkToolPermission.Always))!;

        Assert.DoesNotContain(FakeMcpServer.EchoTool, agent.RegisteredToolNames);
    }

    [Fact]
    public async Task Agent_CannotGrantItselfAnMcpTool()
    {
        await ConnectAsync();
        var definition = Agent() with
        {
            ToolPermissions = new ToolPermissionSet
            {
                Tools = { [FakeMcpServer.EchoTool] = WorkToolPermission.Always },
            },
        };

        var agent = (AgentBuilder)_work.CreateAgentTools(
            definition,
            new ToolPermissionProfile())!;

        Assert.DoesNotContain(FakeMcpServer.EchoTool, agent.RegisteredToolNames);
    }

    [Fact]
    public async Task Agent_DoesNotGetAnMcpToolTurnedOffForTheSession()
    {
        await ConnectAsync();

        var agent = (AgentBuilder)_work.CreateAgentTools(
            Agent(),
            ConsentedTo(WorkToolPermission.Off))!;

        Assert.DoesNotContain(FakeMcpServer.EchoTool, agent.RegisteredToolNames);
    }

    [Fact]
    public async Task Agent_NeverPermissionBlocksTheCall()
    {
        await ConnectAsync();
        AllowRun();

        var agent = (AgentBuilder)_work.CreateAgentTools(
            Agent(),
            ConsentedTo(WorkToolPermission.Never))!;

        Assert.Contains(FakeMcpServer.EchoTool, agent.RegisteredToolNames);

        JsonObject result = await agent.CallToolAsync(new JsonObject
        {
            ["tool"] = FakeMcpServer.EchoTool,
            ["arguments"] = new JsonObject { ["text"] = "hello" },
        });

        Assert.Equal("success", result["status"]!.GetValue<string>());
        Assert.Contains("Permission denied", result["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_ResolvesVaultReferencesInToolArgumentsAndRedactsTheEcho()
    {
        await ConnectAsync();
        AllowRun();

        var agent = (AgentBuilder)_work.CreateAgentTools(
            Agent(),
            ConsentedTo(WorkToolPermission.Always))!;

        JsonObject result = await agent.CallToolAsync(new JsonObject
        {
            ["tool"] = FakeMcpServer.EchoTool,
            ["arguments"] = new JsonObject { ["text"] = "Bearer %vault:TOKEN%" },
        });

        Assert.Equal("success", result["status"]!.GetValue<string>());
        Assert.Equal("Bearer ***", result["data"]!.GetValue<string>());
    }

    /// <summary>
    /// The project gate. Connect asks for Run in the real UI; these tests call
    /// the host directly, so they open Run for the project root themselves.
    /// </summary>
    private void AllowRun()
    {
        var contexts = _work.Settings.Contexts
            .Select(context => context with
            {
                Permissions = context.Permissions with { Execute = WorkPermission.Ask },
            })
            .ToList();

        _work.Save(_work.Settings with { Contexts = contexts }, _work.Memory);
    }

    public void Dispose()
    {
        _mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _agents.Dispose();
        _temp.Dispose();
    }

    private async Task ConnectAsync()
    {
        IReadOnlyList<McpToolInfo> tools = await _mcp.EnsureStartedAsync(
            FakeMcpServer.Settings(),
            () => Task.FromResult(true));
        _tools.RegisterMcpTools(FakeMcpServer.Id, "Fake MCP", tools);
    }

    private static StudioAgentDefinition Agent() => new()
    {
        Name = "Reviewer",
        Description = "Reviews code",
        Rules = "You review code.",
    };

    /// <summary>
    /// The session layer survives <c>ResolveProfile</c>, which rebuilds the
    /// project and system layers from the live settings.
    /// </summary>
    private static ToolPermissionProfile ConsentedTo(WorkToolPermission permission) => new()
    {
        Session = new ToolPermissionSet
        {
            Tools = { [FakeMcpServer.EchoTool] = permission },
        },
    };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
