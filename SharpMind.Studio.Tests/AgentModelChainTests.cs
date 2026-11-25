using SharpMind.Studio.Core.Agents;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class AgentModelChainTests
{
    [Fact]
    public void LegacyModelPath_BecomesLocalThenInherit()
    {
        var agent = new StudioAgentDefinition
        {
            Name = "legacy",
            ModelPath = @"C:\models\missing.gguf",
        };

        IReadOnlyList<AgentModelChoice> chain = agent.EffectiveModelChain();

        Assert.Equal(2, chain.Count);
        Assert.Equal(AgentModelKind.Local, chain[0].Kind);
        Assert.Equal(AgentModelKind.Inherit, chain[1].Kind);
    }

    [Fact]
    public void EmptyDefinition_InheritsByDefault()
    {
        var agent = new StudioAgentDefinition { Name = "new" };

        AgentModelChoice choice = Assert.Single(agent.EffectiveModelChain());
        Assert.Equal(AgentModelKind.Inherit, choice.Kind);
    }

    [Fact]
    public void ExplicitChain_KeepsItsOrderAndTargets()
    {
        var agent = new StudioAgentDefinition
        {
            Name = "ordered",
            ModelChain =
            [
                new AgentModelChoice
                {
                    Kind = AgentModelKind.Remote,
                    ProviderId = "opencode-go",
                    Model = "deepseek-v4.1-flash",
                },
                new AgentModelChoice
                {
                    Kind = AgentModelKind.Local,
                    Model = @"C:\models\missing.gguf",
                },
                new AgentModelChoice { Kind = AgentModelKind.Inherit },
            ],
        };

        IReadOnlyList<AgentModelChoice> chain = agent.EffectiveModelChain();

        Assert.Equal(3, chain.Count);
        Assert.Equal(AgentModelKind.Remote, chain[0].Kind);
        Assert.Equal(AgentModelKind.Local, chain[1].Kind);
        Assert.Equal(AgentModelKind.Inherit, chain[2].Kind);
    }

    [Fact]
    public void Availability_DefaultsToBothModes()
    {
        var agent = new StudioAgentDefinition { Name = "both" };

        Assert.True(agent.AvailableInChat);
        Assert.True(agent.AvailableInWork);
    }

    [Fact]
    public void Store_PreservesAvailabilityFlags()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"sharp-mind-agents-modes-{Guid.NewGuid():n}");
        try
        {
            var store = new StudioAgentStore(root);
            store.Save(new StudioAgentsDocument
            {
                Agents =
                [
                    new StudioAgentDefinition
                    {
                        Name = "chat-only",
                        AvailableInChat = true,
                        AvailableInWork = false,
                    },
                ],
            });

            StudioAgentDefinition loaded = Assert.Single(store.Load().Agents);
            Assert.True(loaded.AvailableInChat);
            Assert.False(loaded.AvailableInWork);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_NormalizesLegacyPathAndPreservesChainOrder()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"sharp-mind-agents-{Guid.NewGuid():n}");
        try
        {
            var store = new StudioAgentStore(root);
            store.Save(new StudioAgentsDocument
            {
                Agents =
                [
                    new StudioAgentDefinition
                    {
                        Name = "legacy",
                        ModelPath = @"C:\models\old.gguf",
                    },
                    new StudioAgentDefinition
                    {
                        Name = "ordered",
                        ModelChain =
                        [
                            new AgentModelChoice
                            {
                                Kind = AgentModelKind.Compute,
                                ProviderId = "host",
                                Model = "model",
                            },
                            new AgentModelChoice { Kind = AgentModelKind.Inherit },
                        ],
                    },
                ],
            });

            StudioAgentsDocument loaded = store.Load();
            StudioAgentDefinition legacy = loaded.Agents.Single(agent => agent.Name == "legacy");
            StudioAgentDefinition ordered = loaded.Agents.Single(agent => agent.Name == "ordered");

            Assert.Equal([AgentModelKind.Local, AgentModelKind.Inherit],
                legacy.ModelChain.Select(choice => choice.Kind));
            Assert.Equal([AgentModelKind.Compute, AgentModelKind.Inherit],
                ordered.ModelChain.Select(choice => choice.Kind));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
