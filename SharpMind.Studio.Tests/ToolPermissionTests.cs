using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ToolPermissionTests
{
    private static ToolPermissionSet Set(string? tool = null, WorkToolPermission value = WorkToolPermission.Ask,
        string? group = null)
    {
        var set = new ToolPermissionSet();
        if (tool is not null) set.Tools[tool] = value;
        if (group is not null) set.Groups[group] = value;
        return set;
    }

    [Fact]
    public void Resolver_WalksSessionModelFamilyThenSystem()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            group: "Files · write",
            system: Set("WriteFile", WorkToolPermission.Never),
            family: Set("WriteFile", WorkToolPermission.Ask),
            model: Set("WriteFile", WorkToolPermission.Always),
            session: Set("WriteFile", WorkToolPermission.Never));

        Assert.Equal(WorkToolPermission.Never, value.Permission);
        Assert.Equal("Session", value.Source);
        Assert.False(value.Escalates);
    }

    [Fact]
    public void Resolver_InheritsThroughSparseLayers()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "ReadFile",
            system: new ToolPermissionSet { Tools = { ["ReadFile"] = WorkToolPermission.Always } },
            family: new ToolPermissionSet(),
            model: new ToolPermissionSet(),
            session: new ToolPermissionSet());

        Assert.Equal(WorkToolPermission.Always, value.Permission);
        Assert.Equal("System", value.Source);
    }

    [Fact]
    public void Resolver_FallsBackToTheGroupWithinOneLayer()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "DeleteFile",
            group: "Files · delete",
            session: new ToolPermissionSet { Groups = { ["Files · delete"] = WorkToolPermission.Never } });

        Assert.Equal(WorkToolPermission.Never, value.Permission);
        Assert.Equal("Session", value.Source);
    }

    [Fact]
    public void Resolver_PrefersTheToolOverTheGroupInTheSameLayer()
    {
        var session = new ToolPermissionSet
        {
            Groups = { ["Files · write"] = WorkToolPermission.Always },
            Tools = { ["WriteFile"] = WorkToolPermission.Ask },
        };

        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile", group: "Files · write", session: session);

        Assert.Equal(WorkToolPermission.Ask, value.Permission);
    }

    [Fact]
    public void Resolver_LetsAHigherLayerGroupBeatALowerLayerTool()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            group: "Files · write",
            model: new ToolPermissionSet { Tools = { ["WriteFile"] = WorkToolPermission.Always } },
            session: new ToolPermissionSet { Groups = { ["Files · write"] = WorkToolPermission.Never } });

        Assert.Equal(WorkToolPermission.Never, value.Permission);
        Assert.Equal("Session", value.Source);
    }

    [Fact]
    public void Resolver_UsesTheFallbackWhenNoLayerMentionsTheTool()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "UnknownPluginTool",
            system: Set("ReadFile", WorkToolPermission.Always),
            fallback: WorkToolPermission.Off);

        Assert.Equal(WorkToolPermission.Off, value.Permission);
        Assert.Equal(ToolPermissionResolver.DefaultSource, value.Source);
        Assert.False(value.IsVisible);
    }

    [Fact]
    public void Agent_MorePermissiveThanTheSession_FlagsAnEscalation()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            session: Set("WriteFile", WorkToolPermission.Never),
            agent: Set("WriteFile", WorkToolPermission.Always));

        Assert.Equal(WorkToolPermission.Always, value.Permission);
        Assert.Equal("Agent", value.Source);
        Assert.True(value.Escalates);
    }

    [Fact]
    public void Agent_NarrowerThanTheSession_DoesNotEscalate()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            session: Set("WriteFile", WorkToolPermission.Always),
            agent: Set("WriteFile", WorkToolPermission.Never));

        Assert.Equal(WorkToolPermission.Never, value.Permission);
        Assert.False(value.Escalates);
    }

    [Fact]
    public void Agent_MatchingTheSession_DoesNotEscalate()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            session: Set("WriteFile", WorkToolPermission.Ask),
            agent: Set("WriteFile", WorkToolPermission.Ask));

        Assert.False(value.Escalates);
    }

    [Fact]
    public void ResolveWithoutAgent_IgnoresTheAgentLayer()
    {
        WorkToolPermission value = ToolPermissionResolver.ResolveWithoutAgent(
            "WriteFile",
            session: Set("WriteFile", WorkToolPermission.Ask));

        Assert.Equal(WorkToolPermission.Ask, value);
    }

    [Theory]
    [InlineData(WorkToolPermission.Always, WorkPermission.Never, WorkToolPermission.Never)]
    [InlineData(WorkToolPermission.Always, WorkPermission.Ask, WorkToolPermission.Always)]
    [InlineData(WorkToolPermission.Always, WorkPermission.Always, WorkToolPermission.Always)]
    [InlineData(WorkToolPermission.Ask, WorkPermission.Always, WorkToolPermission.Ask)]
    [InlineData(WorkToolPermission.Ask, WorkPermission.Ask, WorkToolPermission.Ask)]
    [InlineData(WorkToolPermission.Never, WorkPermission.Always, WorkToolPermission.Never)]
    [InlineData(WorkToolPermission.Off, WorkPermission.Always, WorkToolPermission.Off)]
    public void Folder_NeverIsAHardDenyAndOtherwiseTheToolDecides(
        WorkToolPermission tool,
        WorkPermission folder,
        WorkToolPermission expected)
    {
        Assert.Equal(expected, ToolPermissionResolver.ApplyFolder(tool, folder));
    }

    [Fact]
    public void MostRestrictive_UsesTheEnumOrder()
    {
        Assert.Equal(WorkToolPermission.Off, ToolPermissionResolver.MostRestrictive(
            WorkToolPermission.Off, WorkToolPermission.Always));
        Assert.Equal(WorkToolPermission.Ask, ToolPermissionResolver.MostRestrictive(
            WorkToolPermission.Always, WorkToolPermission.Ask));
        Assert.Equal(WorkToolPermission.Never, ToolPermissionResolver.MostRestrictive(
            WorkToolPermission.Never, WorkToolPermission.Ask));
    }

    [Fact]
    public void Lookup_IsCaseInsensitiveAfterARoundTripLosesTheComparer()
    {
        // A deserialized dictionary gets the default comparer, which is exactly
        // why the lookup falls back to a scan.
        var set = new ToolPermissionSet
        {
            Tools = new Dictionary<string, WorkToolPermission> { ["WriteFile"] = WorkToolPermission.Never },
            Groups = new Dictionary<string, WorkToolPermission> { ["Files · write"] = WorkToolPermission.Ask },
        };

        Assert.True(set.TryTool("writefile", out WorkToolPermission tool));
        Assert.Equal(WorkToolPermission.Never, tool);
        Assert.True(set.TryGroup("FILES · WRITE", out WorkToolPermission group));
        Assert.Equal(WorkToolPermission.Ask, group);
    }

    [Fact]
    public void EmptyLayer_IsTreatableAsInherit()
    {
        Assert.True(new ToolPermissionSet().IsEmpty);
        Assert.False(Set("ReadFile", WorkToolPermission.Always).IsEmpty);
    }

    [Fact]
    public void Store_PersistsEveryToolLayer()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-tools-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "inference.json");
        string model = Path.Combine(root, "tiny.gguf");
        try
        {
            var store = new InferenceSettingsStore(path);
            store.SetSystemTools(new ToolPermissionSet { Tools = { ["ReadFile"] = WorkToolPermission.Always } });
            store.SetFamilyTools("qwen2", new ToolPermissionSet
            {
                Groups = { ["Files · write"] = WorkToolPermission.Never },
            });
            store.SetModelTools(model, new ToolPermissionSet
            {
                Tools = { ["WriteFile"] = WorkToolPermission.Ask },
            });

            var reopened = new InferenceSettingsStore(path);
            Assert.Equal(WorkToolPermission.Always, reopened.SystemToolPermissions.Tools["ReadFile"]);
            Assert.Equal(WorkToolPermission.Never,
                reopened.FamilyToolPermissions("QWEN2")!.Groups["Files · write"]);
            Assert.Equal(WorkToolPermission.Ask,
                reopened.ModelToolPermissions(model)!.Tools["WriteFile"]);

            // An empty set is stored as absence so the layer keeps inheriting.
            reopened.SetFamilyTools("qwen2", new ToolPermissionSet());
            Assert.Null(new InferenceSettingsStore(path).FamilyToolPermissions("qwen2"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyProjectToolLists_MigrateToTheFourStateModel()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-work-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WorkSettingsStore(root);
            store.Save(new WorkSettings
            {
                DisabledTools = ["WriteFile"],
                EnabledTools = ["FetchUrl"],
            });

            WorkSettings loaded = store.Load();

            Assert.Equal(WorkToolPermission.Off, loaded.ToolPermissions.Tools["WriteFile"]);
            Assert.Equal(WorkToolPermission.Ask, loaded.ToolPermissions.Tools["FetchUrl"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
