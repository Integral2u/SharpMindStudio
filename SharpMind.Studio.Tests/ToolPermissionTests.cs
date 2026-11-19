using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ToolPermissionTests
{
    private static ToolPermissionSet Set(string? tool = null, ToolPermission value = ToolPermission.Ask,
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
            system: Set("WriteFile", ToolPermission.Never),
            family: Set("WriteFile", ToolPermission.Ask),
            model: Set("WriteFile", ToolPermission.Always),
            session: Set("WriteFile", ToolPermission.Never));

        Assert.Equal(ToolPermission.Never, value.Permission);
        Assert.Equal("Session", value.Source);
        Assert.False(value.Escalates);
    }

    [Fact]
    public void Resolver_InheritsThroughSparseLayers()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "ReadFile",
            system: new ToolPermissionSet { Tools = { ["ReadFile"] = ToolPermission.Always } },
            family: new ToolPermissionSet(),
            model: new ToolPermissionSet(),
            session: new ToolPermissionSet());

        Assert.Equal(ToolPermission.Always, value.Permission);
        Assert.Equal("System", value.Source);
    }

    [Fact]
    public void Resolver_FallsBackToTheGroupWithinOneLayer()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "DeleteFile",
            group: "Files · delete",
            session: new ToolPermissionSet { Groups = { ["Files · delete"] = ToolPermission.Never } });

        Assert.Equal(ToolPermission.Never, value.Permission);
        Assert.Equal("Session", value.Source);
    }

    [Fact]
    public void Resolver_PrefersTheToolOverTheGroupInTheSameLayer()
    {
        var session = new ToolPermissionSet
        {
            Groups = { ["Files · write"] = ToolPermission.Always },
            Tools = { ["WriteFile"] = ToolPermission.Ask },
        };

        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile", group: "Files · write", session: session);

        Assert.Equal(ToolPermission.Ask, value.Permission);
    }

    [Fact]
    public void Resolver_LetsAHigherLayerGroupBeatALowerLayerTool()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            group: "Files · write",
            model: new ToolPermissionSet { Tools = { ["WriteFile"] = ToolPermission.Always } },
            session: new ToolPermissionSet { Groups = { ["Files · write"] = ToolPermission.Never } });

        Assert.Equal(ToolPermission.Never, value.Permission);
        Assert.Equal("Session", value.Source);
    }

    [Fact]
    public void Resolver_UsesTheFallbackWhenNoLayerMentionsTheTool()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "UnknownPluginTool",
            system: Set("ReadFile", ToolPermission.Always),
            fallback: ToolPermission.Off);

        Assert.Equal(ToolPermission.Off, value.Permission);
        Assert.Equal(ToolPermissionResolver.DefaultSource, value.Source);
        Assert.False(value.IsVisible);
    }

    [Fact]
    public void Agent_MorePermissiveThanTheSession_FlagsAnEscalation()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            session: Set("WriteFile", ToolPermission.Never),
            agent: Set("WriteFile", ToolPermission.Always));

        Assert.Equal(ToolPermission.Always, value.Permission);
        Assert.Equal("Agent", value.Source);
        Assert.True(value.Escalates);
    }

    [Fact]
    public void Agent_NarrowerThanTheSession_DoesNotEscalate()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            session: Set("WriteFile", ToolPermission.Always),
            agent: Set("WriteFile", ToolPermission.Never));

        Assert.Equal(ToolPermission.Never, value.Permission);
        Assert.False(value.Escalates);
    }

    [Fact]
    public void Agent_MatchingTheSession_DoesNotEscalate()
    {
        ToolPermissionValue value = ToolPermissionResolver.Resolve(
            "WriteFile",
            session: Set("WriteFile", ToolPermission.Ask),
            agent: Set("WriteFile", ToolPermission.Ask));

        Assert.False(value.Escalates);
    }

    [Fact]
    public void ResolveWithoutAgent_IgnoresTheAgentLayer()
    {
        ToolPermission value = ToolPermissionResolver.ResolveWithoutAgent(
            "WriteFile",
            session: Set("WriteFile", ToolPermission.Ask));

        Assert.Equal(ToolPermission.Ask, value);
    }

    [Theory]
    [InlineData(ToolPermission.Always, WorkPermission.Never, ToolPermission.Never)]
    [InlineData(ToolPermission.Always, WorkPermission.Ask, ToolPermission.Ask)]
    [InlineData(ToolPermission.Always, WorkPermission.Always, ToolPermission.Always)]
    [InlineData(ToolPermission.Ask, WorkPermission.Always, ToolPermission.Ask)]
    [InlineData(ToolPermission.Never, WorkPermission.Always, ToolPermission.Never)]
    [InlineData(ToolPermission.Off, WorkPermission.Always, ToolPermission.Off)]
    public void Folder_IsACapNotAnOverride(
        ToolPermission tool,
        WorkPermission folder,
        ToolPermission expected)
    {
        Assert.Equal(expected, ToolPermissionResolver.Cap(tool, folder));
    }

    [Fact]
    public void MostRestrictive_UsesTheEnumOrder()
    {
        Assert.Equal(ToolPermission.Off, ToolPermissionResolver.MostRestrictive(
            ToolPermission.Off, ToolPermission.Always));
        Assert.Equal(ToolPermission.Ask, ToolPermissionResolver.MostRestrictive(
            ToolPermission.Always, ToolPermission.Ask));
        Assert.Equal(ToolPermission.Never, ToolPermissionResolver.MostRestrictive(
            ToolPermission.Never, ToolPermission.Ask));
    }

    [Fact]
    public void Lookup_IsCaseInsensitiveAfterARoundTripLosesTheComparer()
    {
        // A deserialized dictionary gets the default comparer, which is exactly
        // why the lookup falls back to a scan.
        var set = new ToolPermissionSet
        {
            Tools = new Dictionary<string, ToolPermission> { ["WriteFile"] = ToolPermission.Never },
            Groups = new Dictionary<string, ToolPermission> { ["Files · write"] = ToolPermission.Ask },
        };

        Assert.True(set.TryTool("writefile", out ToolPermission tool));
        Assert.Equal(ToolPermission.Never, tool);
        Assert.True(set.TryGroup("FILES · WRITE", out ToolPermission group));
        Assert.Equal(ToolPermission.Ask, group);
    }

    [Fact]
    public void EmptyLayer_IsTreatableAsInherit()
    {
        Assert.True(new ToolPermissionSet().IsEmpty);
        Assert.False(Set("ReadFile", ToolPermission.Always).IsEmpty);
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
            store.SetSystemTools(new ToolPermissionSet { Tools = { ["ReadFile"] = ToolPermission.Always } });
            store.SetFamilyTools("qwen2", new ToolPermissionSet
            {
                Groups = { ["Files · write"] = ToolPermission.Never },
            });
            store.SetModelTools(model, new ToolPermissionSet
            {
                Tools = { ["WriteFile"] = ToolPermission.Ask },
            });

            var reopened = new InferenceSettingsStore(path);
            Assert.Equal(ToolPermission.Always, reopened.SystemToolPermissions.Tools["ReadFile"]);
            Assert.Equal(ToolPermission.Never,
                reopened.FamilyToolPermissions("QWEN2")!.Groups["Files · write"]);
            Assert.Equal(ToolPermission.Ask,
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
}
