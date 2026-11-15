using SharpMind.Studio.Core.Agents;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class AgentPromptTextTests
{
    [Fact]
    public void Parent_ListsTheSavedAgentsAndBothTools()
    {
        string text = AgentPromptText.Parent(
        [
            new StudioAgentDefinition { Name = "Researcher", Description = "Finds sources" },
            new StudioAgentDefinition { Name = "Writer", Description = "Drafts copy" },
        ]);

        Assert.Contains("`delegate`", text);
        Assert.Contains("`spawn_agent`", text);
        Assert.Contains("Researcher: Finds sources", text);
        Assert.Contains("Writer: Drafts copy", text);
        Assert.Contains("cannot see this conversation", text);
        Assert.Contains("give the user the final response", text);
    }

    [Fact]
    public void Parent_WithoutSavedAgents_OnlyMentionsSpawnAgent()
    {
        string text = AgentPromptText.Parent([]);

        Assert.Contains("`spawn_agent`", text);
        Assert.DoesNotContain("Saved agents:", text);
        Assert.DoesNotContain("`delegate` runs", text);
    }

    [Fact]
    public void DelegatedTask_BindsTheSpecialistToItsBrief()
    {
        Assert.Contains("cannot ask the user", AgentPromptText.DelegatedTask);
        Assert.Contains("self-contained", AgentPromptText.DelegatedTask);
        Assert.Contains("Do not delegate back", AgentPromptText.DelegatedTask);
    }
}
