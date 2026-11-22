using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Project-local Work configuration. The system prompt and permissions live in
/// work.json; memory and skills are plain Markdown so they can be edited in the
/// app or by hand without a migration.
/// </summary>
public sealed class WorkSettingsStore
{
    public const string WorkFileName = "work.json";
    public const string MemoryFileName = "memory.md";
    public const string SkillsFileName = "skills.md";

    public WorkSettingsStore(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ProjectRoot = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(ProjectRoot);
    }

    public string ProjectRoot { get; }
    public string WorkFile => Path.Combine(ProjectRoot, WorkFileName);
    public string MemoryFile => Path.Combine(ProjectRoot, MemoryFileName);
    public string SkillsFile => Path.Combine(ProjectRoot, SkillsFileName);

    public WorkSettings Load()
    {
        // A brand-new project has no work.json yet. It still needs the default
        // context, otherwise every file tool is refused because no folder is
        // allowed until the settings are saved once.
        if (!File.Exists(WorkFile))
        {
            return new WorkSettings
            {
                Contexts = [new WorkContext { Id = "project-root", Path = "." }],
            };
        }

        WorkSettings settings = JsonFile.Read<WorkSettings>(WorkFile);
        if (settings.Schema != WorkSettings.CurrentSchema)
            throw new InvalidDataException(
                $"Work schema '{settings.Schema}' is not supported (expected '{WorkSettings.CurrentSchema}').");

        settings = settings with
        {
            Permissions = settings.Permissions ?? new(),
            DisabledSystemSkills = settings.DisabledSystemSkills ?? [],
            EnabledSystemSkills = settings.EnabledSystemSkills ?? [],
            DisabledProjectSkills = settings.DisabledProjectSkills ?? [],
            Skills = settings.Skills ?? [],
            Contexts = settings.Contexts ?? [],
            DisabledTools = settings.DisabledTools ?? [],
            EnabledTools = settings.EnabledTools ?? [],
            AllowedCommands = [.. (settings.AllowedCommands ?? [])
                .Where(command => !string.IsNullOrWhiteSpace(command.Executable))
                .Select(command => command with
                {
                    Name = string.IsNullOrWhiteSpace(command.Name)
                        ? DefaultCommandName(command.Executable)
                        : command.Name.Trim(),
                    Executable = command.Executable.Trim(),
                    ArgumentsPrefix = [.. (command.ArgumentsPrefix ?? [])],
                    Description = (command.Description ?? "").Trim(),
                })],
            ToolPermissions = settings.ToolPermissions ?? new(),
        };

        // A file written before the four-state permissions only has the two
        // lists. Fold them in once so the panel and the resolver agree.
        if (settings.ToolPermissions.IsEmpty)
        {
            var migrated = new ToolPermissionSet();
            foreach (string name in settings.DisabledTools)
                migrated.Tools[name] = WorkToolPermission.Off;
            foreach (string name in settings.EnabledTools)
                migrated.Tools[name] = WorkToolPermission.Ask;
            if (!migrated.IsEmpty)
                settings = settings with { ToolPermissions = migrated };
        }

        if (settings.Contexts.Count == 0)
        {
            settings = settings with
            {
                Contexts =
                [
                    new WorkContext
                    {
                        Id = "project-root",
                        Path = ".",
                        Permissions = settings.Permissions with { Web = WorkPermission.Never },
                    },
                ],
            };
        }

        return settings;
    }

    public void Save(WorkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JsonFile.Write(WorkFile, settings with { Schema = WorkSettings.CurrentSchema });
    }

    public string ReadMemory() => ReadText(MemoryFile);
    public string ReadSkills() => ReadText(SkillsFile);

    public void WriteMemory(string? content) => WriteText(MemoryFile, content);
    public void WriteSkills(string? content) => WriteText(SkillsFile, content);

    public void RetireLegacySkillsFile()
    {
        if (!File.Exists(SkillsFile)) return;
        string retired = SkillsFile + ".imported";
        if (File.Exists(retired)) File.Delete(retired);
        File.Move(SkillsFile, retired);
    }

    private static string DefaultCommandName(string executable)
    {
        string trimmed = executable.Trim();
        string name = Path.GetFileNameWithoutExtension(trimmed);
        return name.Length == 0 ? trimmed : name;
    }

    private static string ReadText(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : "";

    private static void WriteText(string path, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}




