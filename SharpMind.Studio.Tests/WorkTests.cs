using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Work;
using SharpMind.Inference.Agent;
using System.Text.Json.Nodes;

namespace SharpMind.Studio.Tests;

public sealed class WorkFileToolsTests
{
    [Fact]
    public async Task ReadAndWrite_StayInsideTheProject()
    {
        using var temp = new TempDirectory();
        var tools = new WorkFileTools(temp.Path, _ => Task.FromResult(true));

        string write = await tools.WriteFile("notes/today.md", "hello");
        string read = await tools.ReadFile("notes/today.md");

        Assert.Contains("Wrote", write);
        Assert.Equal("hello", read);
        Assert.Equal("hello", await File.ReadAllTextAsync(temp.Combine("notes", "today.md")));
    }

    [Fact]
    public async Task PathTraversal_IsDeniedBeforeTheFileSystem()
    {
        using var temp = new TempDirectory();
        var tools = new WorkFileTools(temp.Combine("project"), _ => Task.FromResult(true));

        string result = await tools.ReadFile("../outside.txt");

        Assert.Contains("outside the allowed project locations", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbsolutePathOutsideTheProject_IsDenied()
    {
        using var temp = new TempDirectory();
        var tools = new WorkFileTools(temp.Combine("project"), _ => Task.FromResult(true));
        string outside = temp.Combine("outside.txt");

        string result = await tools.ReadFile(outside);

        Assert.Contains("outside the allowed project locations", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PermissionDenial_PreventsTheWrite()
    {
        using var temp = new TempDirectory();
        var tools = new WorkFileTools(temp.Path, access =>
            Task.FromResult(access.Category != WorkToolCategory.Write));

        string result = await tools.WriteFile("secret.txt", "no");
        string listed = await tools.ListFiles(".");

        Assert.Contains("Permission denied", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(temp.Combine("secret.txt")));
        Assert.DoesNotContain("secret.txt", listed);
    }

    [Fact]
    public void UiTools_RegisterChoiceAndUtilityToolsAndRespectDisabledTools()
    {
        var tools = new WorkUiTools((_, _, _) => Task.FromResult("ok"));
        var builder = new AgentBuilder("Work") { DisabledTools = ["Echo"] }.WithTools(tools);

        Assert.Contains("UIShowOptionSelection", builder.RegisteredToolNames);
        Assert.Contains("UIGetFreeMemory", builder.RegisteredToolNames);
        Assert.Contains("GetCurrentDateTime", builder.RegisteredToolNames);
        Assert.DoesNotContain("Echo", builder.RegisteredToolNames);
    }
    [Fact]
    public async Task UiChoiceTool_DispatchesThroughTheAgent()
    {
        var builder = new AgentBuilder("Work").WithTools(
            new WorkUiTools((prompt, options, free) =>
            {
                Assert.Equal("Pick one", prompt);
                Assert.Equal(["one", "two"], options);
                return Task.FromResult("two");
            }));

        System.Text.Json.Nodes.JsonObject result = await builder.CallToolAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["tool"] = "UIShowOptionSelection",
            ["arguments"] = new System.Text.Json.Nodes.JsonObject
            {
                ["prompt"] = "Pick one",
                ["options"] = new System.Text.Json.Nodes.JsonArray("one", "two"),
            },
        });

        Assert.Equal("success", result["status"]?.GetValue<string>());
        Assert.Equal("two", result["data"]?.GetValue<string>());
    }
    [Fact]
    public async Task MultipleAllowedRoots_AllowASecondContext()
    {
        using var temp = new TempDirectory();
        string project = temp.Combine("project");
        string external = temp.Combine("external");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(external);
        string file = Path.Combine(external, "note.txt");
        await File.WriteAllTextAsync(file, "context");

        var tools = new WorkFileTools(project, [project, external], _ => Task.FromResult(true));
        string result = await tools.ReadFile(file);

        Assert.Equal("context", result);
    }
    [Fact]
    public void AgentBuilder_RegistersTheWorkTools()
    {
        using var temp = new TempDirectory();
        var builder = new AgentBuilder("Work")
            .WithTools(new WorkFileTools(temp.Path, _ => Task.FromResult(true)));

        Assert.Contains(nameof(WorkFileTools.ReadFile), builder.RegisteredToolNames);
        Assert.Contains(nameof(WorkFileTools.WriteFile), builder.RegisteredToolNames);
        Assert.Contains("ReadFile", builder.BuildAgentPrompt());
    }

    [Fact]
    public async Task AgentDispatch_InvokesAWorkTool()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(temp.Combine("hello.txt"), "hello");
        var builder = new AgentBuilder("Work")
            .WithTools(new WorkFileTools(temp.Path, _ => Task.FromResult(true)));

        JsonObject result = await builder.CallToolAsync(new JsonObject
        {
            ["tool"] = "ReadFile",
            ["arguments"] = new JsonObject { ["relativePath"] = "hello.txt" },
        });

        Assert.Equal("success", result["status"]?.GetValue<string>());
        Assert.Equal("hello", result["data"]?.GetValue<string>());
    }
    [Fact]
    public async Task Search_ReturnsRelativeMatchesAndSkipsBuildFolders()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("src"));
        Directory.CreateDirectory(temp.Combine("bin"));
        Directory.CreateDirectory(temp.Combine("obj"));
        await File.WriteAllTextAsync(temp.Combine("src", "a.txt"), "first\nneedle here\n");
        await File.WriteAllTextAsync(temp.Combine("bin", "b.txt"), "needle in bin");

        var tools = new WorkFileTools(temp.Path, _ => Task.FromResult(true));
        string result = await tools.SearchFiles("needle");

        Assert.Contains("src/a.txt:2", result);
        Assert.DoesNotContain("bin/b.txt", result);
    }
}

public sealed class SystemWorkSkillsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsNamedSkills()
    {
        using var temp = new TempDirectory();
        var store = new SystemWorkSkillsStore(temp.Combine("work-skills.json"));

        store.Save([
            new WorkSkill { Name = "Review", Content = "Check the diff." },
            new WorkSkill { Id = "fixed", Name = "Tone", Content = "Be terse." },
        ]);

        IReadOnlyList<WorkSkill> skills = store.Load();

        Assert.Equal(2, skills.Count);
        Assert.All(skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Id)));
        Assert.Contains(skills, skill => skill.Id == "fixed" && skill.Name == "Tone");
    }

    [Fact]
    public void Save_DropsBlankNamesAndLoadRejectsUnknownSchema()
    {
        using var temp = new TempDirectory();
        var store = new SystemWorkSkillsStore(temp.Combine("work-skills.json"));
        store.Save([new WorkSkill { Name = " ", Content = "ignored" }]);
        Assert.Empty(store.Load());

        JsonFile.Write(store.FilePath, new SystemWorkSkillsDocument { Schema = "studio-work-skills/999" });
        Assert.Throws<InvalidDataException>(() => store.Load());
    }
}
public sealed class WorkSkillRepositoryTests
{
    [Fact]
    public void ReplaceAll_WritesFilesAndKeepsStableIdsAndDefaults()
    {
        using var temp = new TempDirectory();
        var repo = new WorkSkillRepository(temp.Combine("skills"), temp.Combine("work-skills.json"));

        IReadOnlyList<WorkSkill> saved = repo.ReplaceAll([
            new WorkSkill { Name = "Review", Content = "Check the diff.", DefaultEnabled = true },
            new WorkSkill { Name = "Tone", Content = "Be terse.", DefaultEnabled = false },
        ]);

        Assert.Equal(2, saved.Count);
        Assert.True(File.Exists(Path.Combine(repo.Folder, "review.md")));
        Assert.Contains(saved, skill => skill.Name == "Tone" && !skill.DefaultEnabled);

        string reviewId = saved.Single(skill => skill.Name == "Review").Id;
        IReadOnlyList<WorkSkill> edited = repo.ReplaceAll([
            new WorkSkill { Id = reviewId, Name = "Review", Content = "Changed.", DefaultEnabled = true },
        ]);

        Assert.Single(edited);
        Assert.Equal("Changed.", edited[0].Content);
        Assert.False(File.Exists(Path.Combine(repo.Folder, "tone.md")));
    }

    [Fact]
    public void List_MigratesTheLegacyJsonSkillList()
    {
        using var temp = new TempDirectory();
        string state = temp.Combine("work-skills.json");
        JsonFile.Write(state, new SystemWorkSkillsDocument
        {
            Skills = [new WorkSkill { Name = "Legacy", Content = "old content" }],
        });
        var repo = new WorkSkillRepository(temp.Combine("skills"), state);

        WorkSkill skill = Assert.Single(repo.List());

        Assert.Equal("Legacy", skill.Name);
        Assert.Equal("old content", skill.Content);
        Assert.True(File.Exists(Path.Combine(repo.Folder, skill.Id)));
    }

    [Fact]
    public void List_PicksUpFilesCopiedIntoTheFolder()
    {
        using var temp = new TempDirectory();
        var repo = new WorkSkillRepository(temp.Combine("skills"), temp.Combine("work-skills.json"));
        File.WriteAllText(Path.Combine(repo.Folder, "copied-skill.md"), "From disk.");

        WorkSkill skill = Assert.Single(repo.List());
        Assert.Equal("copied skill", skill.Name);
        Assert.Equal("From disk.", skill.Content);
        Assert.True(skill.DefaultEnabled);
    }

    [Fact]
    public void Delete_RemovesFileAndSelectionState()
    {
        using var temp = new TempDirectory();
        var repo = new WorkSkillRepository(temp.Combine("skills"), temp.Combine("work-skills.json"));
        WorkSkill skill = repo.ReplaceAll([
            new WorkSkill { Name = "Delete me", Content = "x" },
        ])[0];

        repo.Delete(skill.Id);

        Assert.Empty(repo.List());
        Assert.False(File.Exists(Path.Combine(repo.Folder, skill.Id)));
    }
}
public sealed class WorkSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsPromptPermissionsMemoryAndSkills()
    {
        using var temp = new TempDirectory();
        var store = new WorkSettingsStore(temp.Path);
        var settings = new WorkSettings
        {
            SystemPrompt = "Be concise.",
            Permissions = new WorkPermissions
            {
                Read = WorkPermission.Always,
                Write = WorkPermission.Ask,
                Delete = WorkPermission.Never,
            },
            DisabledSystemSkills = ["system-review"],
        };

        store.Save(settings);
        store.WriteMemory("Client prefers metric units.");
        store.WriteSkills("## Review\nCheck the diff.");

        WorkSettings loaded = store.Load();

        Assert.Equal("Be concise.", loaded.SystemPrompt);
        Assert.Equal(WorkPermission.Always, loaded.Permissions.Read);
        Assert.Equal(WorkPermission.Ask, loaded.Permissions.Write);
        Assert.Equal(WorkPermission.Never, loaded.Permissions.Delete);
        Assert.Equal(["system-review"], loaded.DisabledSystemSkills);
        Assert.Equal("Client prefers metric units.", store.ReadMemory());
        Assert.Contains("Check the diff.", store.ReadSkills());

        string json = File.ReadAllText(store.WorkFile);
        Assert.Contains("\"Always\"", json);
    }

    [Fact]
    public void Load_AddsTheProjectRootContextWhenMissing()
    {
        using var temp = new TempDirectory();
        var store = new WorkSettingsStore(temp.Path);
        store.Save(new WorkSettings());

        WorkSettings loaded = store.Load();

        WorkContext context = Assert.Single(loaded.Contexts);
        Assert.Equal(".", context.Path);
        Assert.Equal(WorkPermission.Ask, context.Permissions.Read);
    }

    [Fact]
    public void Load_RejectsAnUnknownSchema()
    {
        using var temp = new TempDirectory();
        var store = new WorkSettingsStore(temp.Path);
        JsonFile.Write(store.WorkFile, new WorkSettings { Schema = "studio-work/999" });

        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void ClearMemoryAndSkills_DeletesTheirFiles()
    {
        using var temp = new TempDirectory();
        var store = new WorkSettingsStore(temp.Path);
        store.WriteMemory("old");
        store.WriteSkills("old");

        store.WriteMemory(null);
        store.WriteSkills("");

        Assert.False(File.Exists(store.MemoryFile));
        Assert.False(File.Exists(store.SkillsFile));
    }
}












