using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// A reusable architecture design for the Model visualizer. It can be built by
/// hand or seeded from an existing model, saved as JSON, then opened in the New
/// model editor. Full and Sliding train directly; Linear/Recurrent are preserved
/// in the plan but the current training kernel treats them as full attention.
/// </summary>
public sealed record ModelArchitectureTemplate
{
    public string Name { get; init; } = "visualizer-template";
    public string? ArchitecturePreset { get; init; } = "gpt2";
    public int HiddenDim { get; init; } = 128;
    public int NumLayers { get; init; } = 4;
    public int NumHeads { get; init; } = 8;
    public int NumKvHeads { get; init; } = 8;
    public int FfnDim { get; init; } = 512;
    public int MaxSeqLen { get; init; } = 256;
    public int SlidingWindowSize { get; init; } = 0;

    /// <summary>One entry per layer: "Full", "Sliding", "Linear" or "Recurrent".</summary>
    public List<string> LayerTypes { get; init; } = [];

    /// <summary>Linear and Recurrent are visual-plan types, not trained by the current kernel.</summary>
    public bool HasUnsupportedTrainingLayers =>
        LayerTypes.Any(type => Normalize(type) is "Linear" or "Recurrent");

    public static ModelArchitectureTemplate FromDescriptor(
        ModelDescriptor descriptor,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        List<string> layers = [];
        int count = Math.Max(1, descriptor.NumLayers);
        for (int index = 0; index < count; index++)
        {
            bool linear = descriptor.LayerKvHeads is { Length: > 0 } kv &&
                index < kv.Length && kv[index] == 0;
            bool recurrent = descriptor.HasDeltaNet &&
                (index + 1) % (descriptor.FullAttentionInterval ?? 4) != 0;
            if (linear)
            {
                layers.Add("Linear");
                continue;
            }
            if (recurrent)
            {
                layers.Add("Recurrent");
                continue;
            }

            bool sliding;
            if (descriptor.SwaLayers is { Length: > 0 } mask)
            {
                sliding = index < mask.Length && mask[index] &&
                    descriptor.SlidingWindowSize > 0;
            }
            else if (descriptor.SlidingWindowSize <= 0)
            {
                sliding = false;
            }
            else if (descriptor.SlidingWindowPattern is > 0)
            {
                int period = descriptor.SlidingWindowPattern.Value;
                sliding = index % period != period - 1;
            }
            else
            {
                sliding = true;
            }
            layers.Add(sliding ? "Sliding" : "Full");
        }

        return new ModelArchitectureTemplate
        {
            Name = string.IsNullOrWhiteSpace(name) ? descriptor.DisplayName : name,
            ArchitecturePreset = inferPreset(descriptor.Architecture),
            HiddenDim = Math.Max(8, descriptor.HiddenDim),
            NumLayers = count,
            NumHeads = Math.Max(1, descriptor.NumHeads),
            NumKvHeads = Math.Max(1, descriptor.NumKvHeads),
            FfnDim = Math.Max(8, descriptor.FfnDim),
            MaxSeqLen = Math.Max(8, descriptor.ContextLength),
            SlidingWindowSize = Math.Max(0, descriptor.SlidingWindowSize),
            LayerTypes = layers,
        };

        static string? inferPreset(string? architecture)
        {
            if (string.IsNullOrWhiteSpace(architecture)) return null;
            string arch = architecture.Trim().ToLowerInvariant();
            if (arch.Contains("llama")) return "llama";
            if (arch.Contains("bert") || arch.Contains("roberta")) return "bert";
            if (arch.Contains("qwen")) return "qwen3";
            if (arch.Contains("mixtral")) return "mixtral";
            if (arch.Contains("gpt")) return "gpt2";
            return arch;
        }
    }

    public static ModelArchitectureTemplate FromJob(TrainingJobSettings job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new ModelArchitectureTemplate
        {
            Name = string.IsNullOrWhiteSpace(job.Name) ? "visualizer-template" : job.Name,
            ArchitecturePreset = job.ArchitecturePreset,
            HiddenDim = Math.Max(8, job.HiddenDim),
            NumLayers = Math.Max(1, job.NumLayers),
            NumHeads = Math.Max(1, job.NumHeads),
            NumKvHeads = Math.Max(1, job.NumKvHeads),
            FfnDim = Math.Max(8, job.FfnDim),
            MaxSeqLen = Math.Max(8, job.MaxSeqLen),
            SlidingWindowSize = Math.Max(0, job.LayerSlidingWindowSize),
            LayerTypes = job.LayerTypes.Count == job.NumLayers
                ? [.. job.LayerTypes]
                : [.. Enumerable.Repeat("Full", Math.Max(1, job.NumLayers))],
        };
    }

    /// <summary>
    /// Writes the dimensions and layer plan into a training job. Existing
    /// training/optimizer fields are kept.
    /// </summary>
    public TrainingJobSettings ApplyTo(TrainingJobSettings job)
    {
        ArgumentNullException.ThrowIfNull(job);
        int layers = Math.Max(1, NumLayers);
        List<string> plan = LayerTypes.Count == layers
            ? [.. LayerTypes.Select(Normalize)]
            : [.. Enumerable.Repeat("Full", layers)];

        job.Name = string.IsNullOrWhiteSpace(Name) ? job.Name : Name.Trim();
        string? presetKey = string.IsNullOrWhiteSpace(ArchitecturePreset)
            ? null
            : ArchitecturePreset.Trim();
        TrainingPreset? preset = presetKey is null
            ? null
            : TrainingModelOptions.Presets.FirstOrDefault(item =>
                string.Equals(item.ArchitecturePresetKey, presetKey, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
            TrainingModelOptions.Apply(job, preset);
        job.ArchitecturePreset = presetKey;
        job.HiddenDim = Math.Max(8, HiddenDim);
        job.NumLayers = layers;
        job.NumHeads = Math.Max(1, NumHeads);
        job.NumKvHeads = Math.Max(1, NumKvHeads);
        job.FfnDim = Math.Max(8, FfnDim);
        job.MaxSeqLen = Math.Max(8, MaxSeqLen);
        job.LayerSlidingWindowSize = Math.Max(0, SlidingWindowSize);
        job.LayerTypes = plan;
        return job;
    }

    public static string Normalize(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "sliding" => "Sliding",
        "linear" => "Linear",
        "recurrent" => "Recurrent",
        _ => "Full",
    };

    /// <summary>Reads a template from a JSON file.</summary>
    public static ModelArchitectureTemplate Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ModelArchitectureTemplate template = JsonFile.Read<ModelArchitectureTemplate>(path);
        return NormalizePlan(template);
    }

    /// <summary>Writes a template as indented JSON.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonFile.Write(path, NormalizePlan(this));
    }

    private static ModelArchitectureTemplate NormalizePlan(ModelArchitectureTemplate template)
    {
        int layers = Math.Max(1, template.NumLayers);
        List<string> plan = template.LayerTypes.Count == layers
            ? [.. template.LayerTypes.Select(Normalize)]
            : [.. Enumerable.Repeat("Full", layers)];
        return template with { NumLayers = layers, LayerTypes = plan };
    }
}
