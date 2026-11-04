using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpMind.Studio.Core.Storage;
using SharpMind.Training;

namespace SharpMind.Studio.Core.Training;

/// <summary>A reusable artifact keyed by a content hash.</summary>
public sealed record TrainingRecipeArtifact(
    string Kind,
    string Hash,
    string ArtifactPath,
    string JobName,
    DateTime CreatedUtc);

/// <summary>
/// A tiny content-addressed recipe cache. Tokenizers, checkpoint pointers and
/// exported models are stored by hash under <c>Training/recipes</c>; a rerun
/// with identical inputs can skip the tokenizer, continue a checkpoint or reuse
/// the whole packaged model.
/// </summary>
public static class TrainingRecipeStore
{
    public static string Folder => Path.Combine(StudioPaths.Training, "recipes");

    public static bool TryGet(
        string kind,
        string hash,
        out TrainingRecipeArtifact artifact,
        string? folder = null)
    {
        artifact = null!;
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(hash)) return false;

        string path = PathFor(kind, hash, folder ?? Folder);
        if (!File.Exists(path)) return false;

        try
        {
            TrainingRecipeArtifact? saved = JsonSerializer.Deserialize<TrainingRecipeArtifact>(
                File.ReadAllText(path), TrainingRuleSet.Json);
            if (saved is null || string.IsNullOrWhiteSpace(saved.ArtifactPath)) return false;
            if (!File.Exists(saved.ArtifactPath) && !Directory.Exists(saved.ArtifactPath)) return false;

            artifact = saved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(TrainingRecipeArtifact artifact, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(artifact.Kind) || string.IsNullOrWhiteSpace(artifact.Hash))
            return;

        string path = PathFor(artifact.Kind, artifact.Hash, folder ?? Folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TrainingRecipeArtifact stored = artifact with
        {
            ArtifactPath = Path.GetFullPath(artifact.ArtifactPath),
            CreatedUtc = artifact.CreatedUtc == default ? DateTime.UtcNow : artifact.CreatedUtc,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(stored, TrainingRuleSet.Json));
    }

    private static string PathFor(string kind, string hash, string folder)
        => Path.Combine(folder, kind, hash + ".json");
}

/// <summary>
/// Stable hashes for the reusable recipe phases: data, tokenizer, the train
/// base (before the schedule horizon) and the exact full run including package
/// metadata. Volatile fields (names, export paths, fork labels) are excluded;
/// the base model and resume checkpoints are hashed by content.
/// </summary>
public static class TrainingRecipeHash
{
    public static string Data(TrainingJobSettings job, string? sourceFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        string source = sourceFingerprint ?? TrySourceFingerprint(job);
        return HashOf(new
        {
            Source = source,
            Sources = job.Sources.Select(source => new
            {
                Component = Canonical(source.Component),
                Stages = source.Stages.Select(Canonical).ToList(),
            }).ToList(),
            GlobalStages = job.GlobalStages.Select(Canonical).ToList(),
            job.ValidationPercent,
            job.TestPercent,
        });
    }

    private static string TrySourceFingerprint(TrainingJobSettings job)
    {
        try
        {
            return SourceHasher.Combined(job.Sources) ?? "none";
        }
        catch
        {
            return "unreadable";
        }
    }

    public static string Tokenizer(TrainingJobSettings job, string dataHash)
    {
        ArgumentNullException.ThrowIfNull(job);
        return HashOf(new
        {
            Data = dataHash,
            job.TokenizerKind,
            job.TokenizerVocabSize,
            job.UsesCharacterTokenizer,
        });
    }

    public static string Train(TrainingJobSettings job, string dataHash, string tokenizerHash)
    {
        ArgumentNullException.ThrowIfNull(job);

        return HashOf(new
        {
            Data = dataHash,
            Tokenizer = tokenizerHash,
            Base = job.BaseModelPath is { Length: > 0 } basePath ? FileFingerprint(basePath) : "none",
            Resume = job.StartFresh ? null : CheckpointFingerprint(job.ResumeFrom),
            WarmRestart = job.StartFresh ? null : CheckpointFingerprint(job.WarmRestartFrom),
            job.StartFresh,
            job.HiddenDim,
            job.NumLayers,
            job.NumHeads,
            job.NumKvHeads,
            job.FfnDim,
            job.MaxSeqLen,
            job.NormEps,
            job.ArchitecturePreset,
            job.Activation,
            job.Gate,
            job.Ffn,
            job.Attention,
            job.Norm,
            job.Arch,
            job.PositionalEncoding,
            job.NumExperts,
            job.TopKExperts,
            job.TotalSteps,
            job.LogInterval,
            job.BatchSize,
            job.SeqLen,
            job.GradAccumSteps,
            job.BatchingKind,
            job.Optimizer,
            job.SgdMomentum,
            job.LearningRate,
            job.MinLr,
            job.WarmupSteps,
            job.WeightDecay,
            job.GradClipNorm,
            job.LabelSmoothing,
            job.QuantAwareTraining,
            job.MergeOnExport,
            job.SaveAdapterFile,
            // Package metadata belongs to the export phase, not the weights, but a
            // full cache hit must still match it or the cached .smm would carry the
            // wrong prompt, skills or tool DLLs. Hash by content, not by path.
            Package = PackageInputs(job),
            LoRA = job.IsFineTune
                ? new
                {
                    job.LoRARank,
                    job.LoRAAlpha,
                    Targets = job.LoRATargetModules
                        .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                }
                : null,
        });
    }

    /// <summary>
    /// The train phase before its schedule horizon and package settings. Two jobs
    /// with the same base, data, tokenizer, model shape and optimizer share this
    /// hash, so a longer run can continue a cached checkpoint instead of starting
    /// over. Steps, logging cadence, checkpoint retention and export metadata are
    /// deliberately excluded.
    /// </summary>
    public static string TrainBase(TrainingJobSettings job, string dataHash, string tokenizerHash)
    {
        ArgumentNullException.ThrowIfNull(job);

        return HashOf(new
        {
            Data = dataHash,
            Tokenizer = tokenizerHash,
            Base = job.BaseModelPath is { Length: > 0 } basePath ? FileFingerprint(basePath) : "none",
            job.HiddenDim,
            job.NumLayers,
            job.NumHeads,
            job.NumKvHeads,
            job.FfnDim,
            job.MaxSeqLen,
            job.NormEps,
            job.ArchitecturePreset,
            job.Activation,
            job.Gate,
            job.Ffn,
            job.Attention,
            job.Norm,
            job.Arch,
            job.PositionalEncoding,
            job.NumExperts,
            job.TopKExperts,
            job.BatchSize,
            job.SeqLen,
            job.GradAccumSteps,
            job.BatchingKind,
            job.Optimizer,
            job.SgdMomentum,
            job.LearningRate,
            job.MinLr,
            job.WarmupSteps,
            job.WeightDecay,
            job.GradClipNorm,
            job.LabelSmoothing,
            job.QuantAwareTraining,
            LoRA = job.IsFineTune
                ? new
                {
                    job.LoRARank,
                    job.LoRAAlpha,
                    Targets = job.LoRATargetModules
                        .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                }
                : null,
        });
    }

    public static string Short(string hash)
        => string.IsNullOrEmpty(hash) ? "" : hash[..Math.Min(8, hash.Length)];

    private static string HashOf(object value)
        => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(value, TrainingRuleSet.Json)));

    private static object PackageInputs(TrainingJobSettings job) => new
    {
        Prompt = job.SystemPromptPath is { Length: > 0 } prompt ? FileFingerprint(prompt) : null,
        Skills = SkillsFingerprint(job.SkillsFolder),
        Plugins = job.PluginDllPaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{path}:{FileFingerprint(path)}")
            .ToList(),
    };

    private static List<string>? SkillsFingerprint(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        if (!Directory.Exists(folder)) return [$"missing:{folder}"];

        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => $"{Path.GetFileName(path)}:{FileFingerprint(path)}")];
        }
        catch
        {
            return [$"unreadable:{folder}"];
        }
    }

    private static object Canonical(JobComponent component) => new
    {
        component.TypeName,
        Args = component.Args
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new { pair.Key, pair.Value })
            .ToList(),
    };

    private static string FileFingerprint(string path)
    {
        try
        {
            if (!File.Exists(path)) return $"missing:{path}";
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return $"unreadable:{path}";
        }
    }

    private static string? CheckpointFingerprint(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        try
        {
            CheckpointMeta meta = Checkpoint.ReadMeta(directory);
            string model = Path.Combine(directory, "model.bin");
            string optimizer = Path.Combine(directory, "optimizer.bin");
            return $"{meta.Step}:{FileFingerprint(model)}:{FileFingerprint(optimizer)}";
        }
        catch
        {
            return $"unreadable:{directory}";
        }
    }
}
