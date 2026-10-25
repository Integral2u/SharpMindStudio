using System.Collections.Concurrent;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Inference;

public sealed record ModelFamily(string Id, string DisplayName);

public sealed record ModelDescriptor
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
    public required string DisplayName { get; init; }
    public required ModelFamily Family { get; init; }
    public string? Architecture { get; init; }
    public int ContextLength { get; init; }
}

public sealed class ModelCatalog
{
    private readonly object _gate = new();
    private readonly string _folder;
    private IReadOnlyList<ModelDescriptor>? _models;

    public ModelCatalog(string folder)
    {
        _folder = System.IO.Path.GetFullPath(folder);
    }

    public async Task<IReadOnlyList<ModelDescriptor>> GetAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!forceRefresh && _models is not null) return _models;
        }

        IReadOnlyList<ModelDescriptor> models = await ScanAsync(_folder, cancellationToken).ConfigureAwait(false);
        lock (_gate) _models = models;
        return models;
    }

    private static async Task<List<ModelDescriptor>> ScanAsync(string folder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder)) return [];

        string[] paths = [.. Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".smm", StringComparison.OrdinalIgnoreCase))];
        var models = new ConcurrentBag<ModelDescriptor>();
        await Parallel.ForEachAsync(paths, new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = cancellationToken,
        }, (path, _) =>
        {
            try { models.Add(Describe(path)); }
            catch { }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        return [.. models.OrderBy(model => model.FileName, StringComparer.OrdinalIgnoreCase)];
    }
    public static ModelDescriptor Describe(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        ModelFormat format = fullPath.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)
            ? ModelFormat.Smm
            : ModelFormat.Gguf;
        var helper = ModelFormatHelpers.GetModelMetaHelperFor(format);
        ModelMetaData meta = helper.LoadMeta(fullPath);
        ModelConfig config = helper.LoadConfig(meta)
            ?? throw new InvalidDataException($"Could not read model config from {fullPath}.");

        string architecture = config.Architecture
            ?? meta.GetString("general.architecture")
            ?? "";
        string basename = meta.GetString("general.basename");
        string name = meta.GetString("general.name");
        string finetune = meta.GetString("general.finetune");
        string fileName = System.IO.Path.GetFileName(fullPath);
        string display = FirstNonEmpty(basename, name, System.IO.Path.GetFileNameWithoutExtension(fileName));
        ModelFamily family = FamilyDetector.Detect(architecture, $"{basename} {name} {finetune} {fileName}");

        return new ModelDescriptor
        {
            Path = fullPath,
            FileName = fileName,
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            DisplayName = display,
            Family = family,
            Architecture = string.IsNullOrWhiteSpace(architecture) ? null : architecture,
            ContextLength = config.MaxSeqLen,
        };
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "model";
}

public static class FamilyDetector
{
    public static ModelFamily Detect(string? architecture, string? hints)
    {
        string arch = (architecture ?? "").Trim().ToLowerInvariant();
        string text = $"{arch} {hints}".ToLowerInvariant();
        string compact = new(text.Where(char.IsLetterOrDigit).ToArray());
        string id = Normalize(arch);

        if (id.Length == 0)
        {
            string[] known = ["qwen3", "qwen2", "qwen", "llama", "gemma3", "gemma", "mistral", "phi3", "phi", "deepseek", "bert", "gpt2"];
            id = Normalize(known.FirstOrDefault(compact.Contains) ?? "unknown");
        }

        return new ModelFamily(id, DisplayName(id));
    }

    private static string Normalize(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        string result = new string(chars);
        while (result.Contains("--", StringComparison.Ordinal))
            result = result.Replace("--", "-", StringComparison.Ordinal);
        return result.Trim('-');
    }

    private static string DisplayName(string id) => id switch
    {
        "qwen3" => "Qwen3",
        "qwen2" => "Qwen2",
        "qwen" => "Qwen",
        "llama" => "Llama",
        "gemma3" => "Gemma 3",
        "gemma" => "Gemma",
        "mistral" => "Mistral",
        "phi3" => "Phi-3",
        "phi" => "Phi",
        "deepseek2" => "DeepSeek 2",
        "deepseek" => "DeepSeek",
        "bert" => "BERT",
        "gpt2" => "GPT-2",
        "unknown" => "Unknown",
        _ => id.Replace('-', ' '),
    };
}



