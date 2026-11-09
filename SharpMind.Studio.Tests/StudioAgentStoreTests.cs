using SharpMind.Studio.Core.Agents;
using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class StudioAgentStoreTests
{
    [Fact]
    public void Save_RoundTripsAndDeduplicatesNames()
    {
        using var temp = new TempDirectory();
        var store = new StudioAgentStore(temp.Path);

        store.Save(new StudioAgentsDocument
        {
            MaxDepth = 3,
            Agents =
            [
                new StudioAgentDefinition
                {
                    Name = "Writer",
                    Description = "Writes prose",
                    Rules = "Be concise and vivid.",
                    TtlMinutes = 99_999,
                },
                new StudioAgentDefinition
                {
                    Name = "writer",
                    Description = "duplicate",
                },
            ],
        });

        StudioAgentsDocument loaded = store.Load();

        Assert.Equal(3, loaded.MaxDepth);
        StudioAgentDefinition agent = Assert.Single(loaded.Agents);
        Assert.Equal("Writer", agent.Name);
        Assert.Equal(1440, agent.TtlMinutes);
        Assert.False(string.IsNullOrWhiteSpace(agent.Id));
    }

    [Fact]
    public void Load_MissingFile_ReturnsAnEmptyDocument()
    {
        using var temp = new TempDirectory();
        var store = new StudioAgentStore(temp.Path);

        StudioAgentsDocument document = store.Load();

        Assert.Empty(document.Agents);
        Assert.Equal(2, document.MaxDepth);
    }

    [Fact]
    public void Defaults_AskBeforeReadingWritingDeletingOrBrowsing()
    {
        var agent = new StudioAgentDefinition();

        Assert.Equal(WorkPermission.Ask, agent.Permissions.Read);
        Assert.Equal(WorkPermission.Ask, agent.Permissions.Write);
        Assert.Equal(WorkPermission.Ask, agent.Permissions.Delete);
        Assert.Equal(WorkPermission.Ask, agent.Permissions.Web);
        Assert.True(agent.InheritTools);
        Assert.Empty(agent.AllowedTools);
    }

    [Fact]
    public void Save_RoundTripsPermissionsAndAllowedTools()
    {
        using var temp = new TempDirectory();
        var store = new StudioAgentStore(temp.Path);
        store.Save(new StudioAgentsDocument
        {
            Agents =
            [
                new StudioAgentDefinition
                {
                    Name = "Reader",
                    Rules = "Read only.",
                    InheritTools = false,
                    AllowedTools = ["ReadFile", "ListFiles"],
                    Permissions = new WorkPermissions
                    {
                        Read = WorkPermission.Always,
                        Write = WorkPermission.Never,
                        Delete = WorkPermission.Never,
                        Web = WorkPermission.Ask,
                    },
                },
            ],
        });

        StudioAgentDefinition agent = Assert.Single(store.Load().Agents);

        Assert.False(agent.InheritTools);
        Assert.Equal(["ReadFile", "ListFiles"], agent.AllowedTools);
        Assert.Equal(WorkPermission.Always, agent.Permissions.Read);
        Assert.Equal(WorkPermission.Never, agent.Permissions.Write);
        Assert.Equal(WorkPermission.Never, agent.Permissions.Delete);
        Assert.Equal(WorkPermission.Ask, agent.Permissions.Web);
    }

    [Fact]
    public void Save_ClampsDepthAndKeepsAStableId()
    {
        using var temp = new TempDirectory();
        var store = new StudioAgentStore(temp.Path);
        store.Save(new StudioAgentsDocument
        {
            MaxDepth = 99,
            Agents = [new StudioAgentDefinition { Name = "Critic", Rules = "Find flaws." }],
        });

        StudioAgentDefinition first = Assert.Single(store.Load().Agents);
        store.Save(store.Load() with { MaxDepth = 0 });
        StudioAgentDefinition second = Assert.Single(store.Load().Agents);

        Assert.Equal(1, store.Load().MaxDepth);
        Assert.Equal(first.Id, second.Id);
    }
}
