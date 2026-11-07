using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.DataFactory;

public enum DataFactorySourceFormat
{
    Text,
    Jsonl,
}

public enum DataFactoryOutputFormat
{
    ChatJsonl,
    TextJsonl,
}

public enum DataFactoryProviderKind
{
    Local,
    OpenRouter,
}

/// <summary>
/// A saved Data Factory pipeline: source, cleaning/filters, provider transform,
/// and the output shape. Recipes are plain JSON (<c>.smmdf</c>) so a run can be
/// repeated or shared without re-entering the form.
/// </summary>
public sealed record DataFactoryRecipe
{
    public const string Extension = ".smmdf";
    public const string DefaultSystemPrompt =
        "You are a careful dataset generator. Follow the user's instructions and return only the requested result.";
    public const string DefaultChatSystemPrompt = "You are a helpful assistant.";
    public const string DefaultChatTemplate =
        "Turn the source text below into one high-quality instruction/answer pair. "
        + "Return only a JSON object with the fields \"instruction\" and \"answer\", with no markdown fences.\n\n"
        + "Source text:\n{text}";
    public const string DefaultTextTemplate =
        "Rewrite the source text below as a clean, self-contained training document. "
        + "Return only the rewritten text.\n\nSource text:\n{text}";

    public string Name { get; set; } = "Untitled";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    // Source
    public string SourcePath { get; set; } = "";
    public DataFactorySourceFormat SourceFormat { get; set; } = DataFactorySourceFormat.Text;
    public string TextField { get; set; } = "text";

    // Cleaning and filters
    public bool StripHtml { get; set; } = true;
    public bool NormalizeText { get; set; } = true;
    public bool Deduplicate { get; set; } = true;
    public int MinChars { get; set; } = 40;
    public int MaxChars { get; set; } = 20_000;
    public string? DropPattern { get; set; }

    // Provider transform
    public DataFactoryProviderKind Provider { get; set; } = DataFactoryProviderKind.OpenRouter;
    public string? LocalModelPath { get; set; }
    public string? RemoteModel { get; set; }
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    public string UserPromptTemplate { get; set; } = DefaultChatTemplate;

    // Export
    public DataFactoryOutputFormat OutputFormat { get; set; } = DataFactoryOutputFormat.ChatJsonl;
    public string ChatSystemPrompt { get; set; } = DefaultChatSystemPrompt;
    public string OutputPath { get; set; } = "";
    public int MaxRecords { get; set; } = 100;
    public int Variants { get; set; } = 1;
    public float Temperature { get; set; } = 0.7f;
    public int MaxTokens { get; set; } = 512;
    public int? Seed { get; set; }

    /// <summary>Attempts per generation, including the first.</summary>
    public int RetryAttempts { get; set; } = 3;

    /// <summary>Base delay for exponential backoff on transient provider errors.</summary>
    public int RetryDelayMs { get; set; } = 1_000;

    /// <summary>Resume a matching <c>.partial</c> output rather than starting over.</summary>
    public bool ResumePartial { get; set; } = true;
}

/// <summary>Saved recipe files under <see cref="StudioPaths.DataFactory"/>.</summary>
public sealed class DataFactoryRecipeStore
{
    private readonly string _folder;

    public DataFactoryRecipeStore(string? folder = null)
    {
        _folder = Path.GetFullPath(folder ?? StudioPaths.DataFactory);
        Directory.CreateDirectory(_folder);
    }

    public string Folder => _folder;

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_folder)) return [];
        return [.. Directory.GetFiles(_folder, "*" + DataFactoryRecipe.Extension)
            .OrderByDescending(File.GetLastWriteTimeUtc)];
    }

    public static DataFactoryRecipe NewRecipe() => new()
    {
        OutputPath = Path.Combine(StudioPaths.Corpus, "factory-output.jsonl"),
    };

    public DataFactoryRecipe Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return JsonFile.Read<DataFactoryRecipe>(path);
    }

    public string Save(DataFactoryRecipe recipe, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.SavedAtUtc = DateTime.UtcNow;
        string destination = path ?? Path.Combine(_folder, Sanitize(recipe.Name) + DataFactoryRecipe.Extension);
        JsonFile.Write(destination, recipe);
        return destination;
    }

    public void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(_folder, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Refusing to delete a recipe outside the Data Factory folder.");
        if (File.Exists(full)) File.Delete(full);
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "recipe" : name.Trim();
    }
}
