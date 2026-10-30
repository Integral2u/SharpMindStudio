using System.Text.Json;
using System.Text.RegularExpressions;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// One shared Skill repository. Each Markdown file is a skill; the file name is
/// its stable id. System defaults and display names live in the state file, so
/// projects can safely keep selections when a skill is edited.
/// </summary>
public sealed class WorkSkillRepository
{
    private const string LegacySchema = "studio-work-skills/1";
    private const string CurrentSchema = "studio-work-skills/2";

    public WorkSkillRepository(string folder, string stateFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        Folder = Path.GetFullPath(folder);
        StateFile = Path.GetFullPath(stateFile);
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }
    public string StateFile { get; }

    public IReadOnlyList<WorkSkill> List()
    {
        Directory.CreateDirectory(Folder);
        WorkSkillState state = LoadState();
        return [.. Directory.EnumerateFiles(Folder, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                string id = Path.GetFileName(path);
                return new WorkSkill
                {
                    Id = id,
                    Name = state.Names.TryGetValue(id, out string? name)
                        ? name
                        : Path.GetFileNameWithoutExtension(id).Replace('-', ' '),
                    Content = File.ReadAllText(path).Trim(),
                    DefaultEnabled = !state.DisabledByDefault.Contains(id, StringComparer.OrdinalIgnoreCase),
                };
            })];
    }

    public IReadOnlyList<WorkSkill> ReplaceAll(IEnumerable<WorkSkill> skills)
    {
        Directory.CreateDirectory(Folder);
        WorkSkillState previous = LoadState();
        var desired = new List<(WorkSkill Skill, string FileName)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (WorkSkill skill in skills.Where(skill => !string.IsNullOrWhiteSpace(skill.Name)))
        {
            string fileName = string.IsNullOrWhiteSpace(skill.Id)
                ? UniqueFileName(skill.Name, used)
                : EnsureMarkdownName(skill.Id);
            used.Add(fileName);
            desired.Add((skill with { Id = fileName, Name = skill.Name.Trim() }, fileName));
        }

        foreach (var entry in desired)
            WriteText(Path.Combine(Folder, entry.FileName), entry.Skill.Content);

        // Delete only skills this repository knew about. A file copied in while
        // the editor was open is picked up on the next reload instead of being
        // destroyed by an unrelated save.
        foreach (string known in previous.Names.Keys)
        {
            if (used.Contains(known)) continue;
            string path = Path.Combine(Folder, known);
            if (File.Exists(path)) File.Delete(path);
        }

        var state = new WorkSkillState
        {
            Schema = CurrentSchema,
            DisabledByDefault = [.. desired
                .Where(entry => !entry.Skill.DefaultEnabled)
                .Select(entry => entry.FileName)],
            Names = desired.ToDictionary(
                entry => entry.FileName,
                entry => entry.Skill.Name,
                StringComparer.OrdinalIgnoreCase),
        };
        SaveState(state);
        return List();
    }

    public void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        string fileName = EnsureMarkdownName(id);
        string path = Path.Combine(Folder, fileName);
        if (File.Exists(path)) File.Delete(path);

        WorkSkillState state = LoadState();
        state.Names.Remove(fileName);
        state.DisabledByDefault.RemoveAll(value =>
            value.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        SaveState(state);
    }

    private WorkSkillState LoadState()
    {
        if (!File.Exists(StateFile))
        {
            return new WorkSkillState { Schema = CurrentSchema };
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(StateFile));
        string schema = document.RootElement.TryGetProperty("schema", out JsonElement schemaElement)
            ? schemaElement.GetString() ?? CurrentSchema
            : CurrentSchema;

        if (schema == LegacySchema)
            return MigrateLegacy(document.RootElement);

        return JsonFile.Read<WorkSkillState>(StateFile);
    }

    private WorkSkillState MigrateLegacy(JsonElement root)
    {
        var state = new WorkSkillState { Schema = CurrentSchema };
        if (!root.TryGetProperty("skills", out JsonElement skills) ||
            skills.ValueKind != JsonValueKind.Array)
        {
            return state;
        }

        foreach (JsonElement skill in skills.EnumerateArray())
        {
            string name = skill.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";
            string content = skill.TryGetProperty("content", out JsonElement contentElement)
                ? contentElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(name)) continue;

            string fileName = UniqueFileName(name, [.. state.Names.Keys]);
            WriteText(Path.Combine(Folder, fileName), content);
            state.Names[fileName] = name;
            if (skill.TryGetProperty("defaultEnabled", out JsonElement enabledElement) &&
                enabledElement.ValueKind == JsonValueKind.False)
                state.DisabledByDefault.Add(fileName);
        }

        SaveState(state);
        return state;
    }

    private void SaveState(WorkSkillState state) => JsonFile.Write(StateFile, state);

    private static string UniqueFileName(string name, IEnumerable<string> used)
    {
        var taken = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        string slug = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "skill";
        string candidate = slug + ".md";
        for (int suffix = 2; taken.Contains(candidate); suffix++)
            candidate = $"{slug}-{suffix}.md";
        return candidate;
    }

    private static string EnsureMarkdownName(string id) =>
        id.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(id) : Path.GetFileName(id) + ".md";

    private static void WriteText(string path, string? content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content ?? "");
        File.Move(temp, path, overwrite: true);
    }

    public sealed record WorkSkillState
    {
        public string Schema { get; init; } = CurrentSchema;
        public List<string> DisabledByDefault { get; init; } = [];
        public Dictionary<string, string> Names { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}


