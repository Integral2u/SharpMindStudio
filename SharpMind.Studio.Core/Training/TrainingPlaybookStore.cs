using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Training;

/// <summary>A user-saved copy of the current pretrain job or LoRA recipe.</summary>
public sealed record SavedTrainingPlaybook
{
    public const string CurrentSchema = "studio-playbook/1";

    public string Schema { get; init; } = CurrentSchema;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required TrainingPlaybookKind Kind { get; init; }
    public string Description { get; init; } = "";
    public string Outcome { get; init; } = "";
    public TrainingJobSettings? Job { get; init; }
    public TrainingFineTuneRecipe? FineTune { get; init; }

    public static SavedTrainingPlaybook FromJob(string name, TrainingJobSettings job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new SavedTrainingPlaybook
        {
            Id = "",
            Name = string.IsNullOrWhiteSpace(name) ? job.Name : name.Trim(),
            Kind = TrainingPlaybookKind.Pretrain,
            Description = "A saved New model job.",
            Outcome = $"{job.HiddenDim} hidden, {job.NumLayers} layers, {job.TotalSteps} steps.",
            Job = job.Clone(),
        };
    }

    public static SavedTrainingPlaybook FromFineTune(
        string name,
        TrainingFineTuneRecipe recipe,
        string? baseModel = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new SavedTrainingPlaybook
        {
            Id = "",
            Name = name.Trim(),
            Kind = TrainingPlaybookKind.FineTune,
            Description = "A saved LoRA recipe."
                + (string.IsNullOrWhiteSpace(baseModel) ? "" : $" Saved with base {Path.GetFileName(baseModel)}."),
            Outcome = $"Rank {recipe.Rank}, alpha {recipe.Alpha:0.#}, {recipe.Steps} steps, {recipe.Output} output.",
            FineTune = recipe,
        };
    }
}

/// <summary>
/// One JSON file per custom playbook under the Training folder. Built-in
/// playbooks stay in code; these are the user's saved pretrain/LoRA recipes.
/// </summary>
public sealed class TrainingPlaybookStore
{
    public TrainingPlaybookStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public IReadOnlyList<SavedTrainingPlaybook> List()
    {
        Directory.CreateDirectory(Folder);
        var playbooks = new List<SavedTrainingPlaybook>();
        foreach (string path in Directory.EnumerateFiles(Folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                SavedTrainingPlaybook? playbook = JsonFile.Read<SavedTrainingPlaybook>(path);
                if (playbook is not null
                    && playbook.Kind is TrainingPlaybookKind.Pretrain or TrainingPlaybookKind.FineTune
                    && (playbook.Job is not null || playbook.FineTune is not null))
                {
                    playbooks.Add(playbook);
                }
            }
            catch
            {
                // A corrupt or half-written file is skipped, not fatal.
            }
        }

        return [.. playbooks.OrderBy(playbook => playbook.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public SavedTrainingPlaybook Save(SavedTrainingPlaybook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);

        string baseId = string.IsNullOrWhiteSpace(playbook.Id)
            ? Slug(playbook.Name)
            : Slug(playbook.Id);
        if (baseId.Length == 0) baseId = "playbook";

        var used = new HashSet<string>(
            Directory.EnumerateFiles(Folder, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path)),
            StringComparer.OrdinalIgnoreCase);

        string id = baseId;
        for (int suffix = 2; used.Contains(id); suffix++)
            id = $"{baseId}-{suffix}";

        SavedTrainingPlaybook saved = playbook with { Id = id };
        JsonFile.Write(Path.Combine(Folder, id + ".json"), saved);
        return saved;
    }

    public void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        string path = Path.Combine(Folder, Slug(id) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    public static TrainingPlaybook ToPlaybook(SavedTrainingPlaybook saved)
    {
        ArgumentNullException.ThrowIfNull(saved);

        return new TrainingPlaybook
        {
            Id = "custom:" + saved.Id,
            Name = saved.Name,
            Eyebrow = "Custom",
            Description = string.IsNullOrWhiteSpace(saved.Description)
                ? "A playbook saved from this Studio."
                : saved.Description,
            Outcome = string.IsNullOrWhiteSpace(saved.Outcome)
                ? "Fills the form with your saved values."
                : saved.Outcome,
            Kind = saved.Kind,
            CreateJob = saved.Job is null ? null : saved.Job.Clone,
            FineTune = saved.FineTune,
        };
    }

    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string slug = string.Concat(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-'));
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
