using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Work;

/// <summary>A reusable skill defined in Settings and inherited by Work projects.</summary>
public sealed record WorkSkill
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Content { get; init; } = "";
    public bool DefaultEnabled { get; init; } = true;
}

public sealed record SystemWorkSkillsDocument
{
    public const string CurrentSchema = "studio-work-skills/1";

    public string Schema { get; init; } = CurrentSchema;
    public List<WorkSkill> Skills { get; init; } = [];
}

public sealed class SystemWorkSkillsStore
{
    public SystemWorkSkillsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
    }

    public string FilePath { get; }

    public IReadOnlyList<WorkSkill> Load()
    {
        if (!File.Exists(FilePath)) return [];

        SystemWorkSkillsDocument document = JsonFile.Read<SystemWorkSkillsDocument>(FilePath);
        if (document.Schema != SystemWorkSkillsDocument.CurrentSchema)
            throw new InvalidDataException(
                $"System Work skills schema '{document.Schema}' is not supported " +
                $"(expected '{SystemWorkSkillsDocument.CurrentSchema}').");

        return [.. (document.Skills ?? [])
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
            .Select(Normalize)];
    }

    public void Save(IEnumerable<WorkSkill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var document = new SystemWorkSkillsDocument
        {
            Skills = [.. skills
                .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
                .Select(Normalize)],
        };
        JsonFile.Write(FilePath, document);
    }

    private static WorkSkill Normalize(WorkSkill skill) => skill with
    {
        Id = string.IsNullOrWhiteSpace(skill.Id) ? Guid.NewGuid().ToString("n") : skill.Id.Trim(),
        Name = skill.Name.Trim(),
        Content = skill.Content?.Trim() ?? "",
    };
}

