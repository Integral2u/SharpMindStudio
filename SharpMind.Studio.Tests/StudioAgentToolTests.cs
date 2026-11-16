using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SharpMind.Inference.Agent;
using SharpMind.Studio;
using SharpMind.Studio.Core.Agents;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// The parent builder must expose delegate/spawn_agent and route them to the
/// Studio executor; a registered agent tool that returns nothing is the failure
/// seen when a model prints a schema instead of making the call.
/// </summary>
[Collection("studio-paths")]
public sealed class StudioAgentToolTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StudioAgentService _agents;
    private readonly StudioWorkService _work;

    public StudioAgentToolTests()
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
        _agents.Upsert(new StudioAgentDefinition
        {
            Name = "Reviewer",
            Description = "Reviews code",
            Rules = "You review code.",
        });
        _work = new StudioWorkService(
            workspace,
            new SystemWorkSkillsService(),
            new WorkToolService(workspace),
            new StudioDeviceAccess(),
            _agents);
    }

    [Fact]
    public async Task DelegateAndSpawn_ForwardToTheAgentExecutor()
    {
        var calls = new List<(string Tool, JsonObject Arguments)>();
        _work.AgentToolExecutor = (tool, arguments, _) =>
        {
            calls.Add((tool, (JsonObject)arguments.DeepClone()));
            return Task.FromResult("agent answer");
        };

        IAgentBuilder builder = _work.CreateAgent("chat")
            ?? throw new InvalidOperationException("No agent builder.");

        JsonObject delegated = await builder.CallToolAsync(new JsonObject
        {
            ["tool"] = "delegate",
            ["arguments"] = new JsonObject
            {
                ["agent"] = "Reviewer",
                ["task"] = "Check this loop.",
            },
        });
        JsonObject spawned = await builder.CallToolAsync(new JsonObject
        {
            ["tool"] = "spawn_agent",
            ["arguments"] = new JsonObject
            {
                ["name"] = "Temp",
                ["purpose"] = "One task",
                ["rules"] = "Be brief.",
                ["task"] = "Summarise.",
            },
        });

        Assert.Equal(2, calls.Count);
        Assert.Equal("delegate", calls[0].Tool);
        Assert.Equal("Reviewer", calls[0].Arguments["agent"]!.GetValue<string>());
        Assert.Equal("spawn_agent", calls[1].Tool);
        Assert.Equal("Temp", calls[1].Arguments["name"]!.GetValue<string>());
        Assert.Contains("agent answer", delegated.ToJsonString());
        Assert.Contains("agent answer", spawned.ToJsonString());
    }

    public void Dispose()
    {
        _agents.Dispose();
        _temp.Dispose();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
