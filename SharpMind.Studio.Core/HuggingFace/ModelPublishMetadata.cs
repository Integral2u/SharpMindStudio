using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpMind.Model;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Training;

namespace SharpMind.Studio.Core.HuggingFace;

/// <summary>What an exported model says about itself, read without loading weights.</summary>
public sealed record ModelPublishMetadata(
    string SourcePath,
    string Format,
    string FileName,
    long SizeBytes,
    string? Architecture,
    int VocabSize,
    int HiddenDim,
    int NumLayers,
    int NumHeads,
    int NumKvHeads,
    int FfnDim,
    int ContextLength,
    long ParameterCount,
    IReadOnlyList<string> TensorDtypes,
    string? Checksum,
    IReadOnlyDictionary<string, string> Lineage,
    IReadOnlyList<string> Skills,
    bool HasSystemPrompt,
    IReadOnlyList<string> Plugins,
    bool HasChatTemplate)
{
    public bool HasEmbeddedExtras =>
        Plugins.Count > 0 || Skills.Count > 0 || HasSystemPrompt;

    public static ModelPublishMetadata Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Model file not found.", full);

        bool smm = full.EndsWith(".smm", StringComparison.OrdinalIgnoreCase);
        bool gguf = full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
        if (!smm && !gguf)
            throw new NotSupportedException("Publish reads .smm and .gguf model files.");

        ModelMetaData meta = smm ? SmmLoader.LoadMeta(full) : GgufLoader.LoadMeta(full);
        ModelConfig? config = smm ? SmmLoader.LoadConfig(meta) : GgufLoader.LoadConfig(meta);

        SmmContents contents = smm ? SmmContentsReader.Read(full) : SmmContents.None;
        IReadOnlyDictionary<string, string> lineage = smm
            ? SmmLoader.LoadLineageFromMeta(meta)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        long parameters = 0;
        foreach (TensorInfo tensor in meta.Tensors)
        {
            long count = 1;
            foreach (int dimension in tensor.Shape)
                count *= Math.Max(0, dimension);
            parameters += count;
        }

        return new ModelPublishMetadata(
            full,
            smm ? "smm" : "gguf",
            Path.GetFileName(full),
            new FileInfo(full).Length,
            config?.Architecture,
            config?.VocabSize ?? 0,
            config?.HiddenDim ?? 0,
            config?.NumLayers ?? 0,
            config?.NumHeads ?? 0,
            config?.NumKvHeads ?? 0,
            config?.FfnDim ?? 0,
            config?.MaxSeqLen ?? 0,
            parameters,
            [.. meta.Tensors.Select(tensor => tensor.Dtype.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)],
            meta.GetString("checksum"),
            lineage,
            contents.Skills,
            contents.HasSystemPrompt,
            contents.Plugins,
            !string.IsNullOrWhiteSpace(meta.GetChatTemplate()));
    }
}

/// <summary>Run metrics copied into the model card when the export belongs to a run.</summary>
public sealed record ModelPublishRunInfo(
    int FinalStep,
    int TotalSteps,
    double? FinalLoss,
    double? BestValLoss,
    int? BestValStep,
    double? ExamLoss,
    double? ExamPerplexity,
    double? ExamBitsPerByte,
    double ElapsedSeconds)
{
    public static ModelPublishRunInfo? From(TrainingRunRecord? record) => record is null
        ? null
        : new ModelPublishRunInfo(
            record.FinalStep,
            record.TotalSteps,
            record.FinalLoss,
            record.BestValLoss,
            record.BestValStep,
            record.ExamLoss,
            record.ExamPerplexity,
            record.ExamBitsPerByte,
            record.ElapsedSeconds);
}

/// <summary>User-editable fields for a model card.</summary>
public sealed record ModelPublishCard(
    string RepoId,
    string Title,
    string Description,
    string License,
    IReadOnlyList<string> Tags,
    string? BaseModel,
    string ArtifactFileName,
    string? Quantization,
    bool IncludesSmm,
    string? SmmFileName,
    ModelPublishRunInfo? Run);

/// <summary>Builds the README and machine-readable sidecar for an uploaded model.</summary>
public static class ModelPublishCardBuilder
{
    public static string BuildReadme(ModelPublishMetadata metadata, ModelPublishCard card)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(card);

        var tags = new List<string> { "sharp-mind", "gguf", "text-generation" };
        if (card.IncludesSmm) tags.Add("smm");
        tags.AddRange(card.Tags.Where(tag => tag.Length > 0));
        tags = [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)];

        var yaml = new StringBuilder();
        yaml.AppendLine("---");
        yaml.Append("license: ").AppendLine(Yaml(card.License));
        yaml.AppendLine("library_name: sharpmind");
        yaml.AppendLine("pipeline_tag: text-generation");
        if (!string.IsNullOrWhiteSpace(card.BaseModel))
            yaml.Append("base_model: ").AppendLine(Yaml(card.BaseModel.Trim()));
        yaml.AppendLine("tags:");
        foreach (string tag in tags)
            yaml.Append("  - ").AppendLine(Yaml(tag));
        yaml.AppendLine("---");
        yaml.AppendLine();

        var body = new StringBuilder(yaml.ToString());
        body.Append("# ").AppendLine(string.IsNullOrWhiteSpace(card.Title)
            ? Path.GetFileNameWithoutExtension(card.ArtifactFileName)
            : card.Title.Trim());
        body.AppendLine();

        if (!string.IsNullOrWhiteSpace(card.Description))
        {
            body.AppendLine(card.Description.Trim());
            body.AppendLine();
        }

        body.AppendLine("## Model details");
        body.AppendLine();
        body.AppendLine("| Field | Value |");
        body.AppendLine("| --- | --- |");
        body.Append("| Format | ").Append(EscapeTable(metadata.Format.ToUpperInvariant())).AppendLine(" |");
        body.Append("| Architecture | ").Append(EscapeTable(metadata.Architecture ?? "unknown")).AppendLine(" |");
        body.Append("| Parameters | ").Append(EscapeTable(FormatCount(metadata.ParameterCount))).AppendLine(" |");
        body.Append("| Hidden size | ").Append(metadata.HiddenDim.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        body.Append("| Layers | ").Append(metadata.NumLayers.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        body.Append("| Attention heads | ").Append(metadata.NumHeads.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        body.Append("| KV heads | ").Append(metadata.NumKvHeads.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        body.Append("| FFN width | ").Append(metadata.FfnDim.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        body.Append("| Context | ").Append(metadata.ContextLength.ToString(CultureInfo.InvariantCulture)).AppendLine(" tokens |");
        body.Append("| Vocabulary | ").Append(metadata.VocabSize.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        body.Append("| Tensors | ").Append(EscapeTable(string.Join(", ", metadata.TensorDtypes))).AppendLine(" |");
        body.Append("| File | ").Append(EscapeTable(card.ArtifactFileName)).AppendLine(" |");
        if (!string.IsNullOrWhiteSpace(card.Quantization))
            body.Append("| Quantization | ").Append(EscapeTable(card.Quantization)).AppendLine(" |");
        body.AppendLine();

        if (card.Run is { } run)
        {
            body.AppendLine("## Training");
            body.AppendLine();
            body.AppendLine("| Metric | Value |");
            body.AppendLine("| --- | --- |");
            body.Append("| Steps | ").Append(run.FinalStep).Append(" / ").Append(run.TotalSteps).AppendLine(" |");
            if (run.FinalLoss is { } loss)
                body.Append("| Final loss | ").Append(loss.ToString("0.####", CultureInfo.InvariantCulture)).AppendLine(" |");
            if (run.BestValLoss is { } best)
            {
                body.Append("| Best validation loss | ")
                    .Append(best.ToString("0.####", CultureInfo.InvariantCulture));
                if (run.BestValStep is { } step) body.Append(" at step ").Append(step);
                body.AppendLine(" |");
            }
            if (run.ExamLoss is { } exam)
                body.Append("| Exam loss | ").Append(exam.ToString("0.####", CultureInfo.InvariantCulture)).AppendLine(" |");
            if (run.ExamPerplexity is { } perplexity)
                body.Append("| Exam perplexity | ").Append(perplexity.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(" |");
            if (run.ExamBitsPerByte is { } bpb)
                body.Append("| Exam bits/byte | ").Append(bpb.ToString("0.0000", CultureInfo.InvariantCulture)).AppendLine(" |");
            if (run.ElapsedSeconds > 0)
                body.Append("| Elapsed | ").Append(FormatDuration(run.ElapsedSeconds)).AppendLine(" |");
            body.AppendLine();
        }

        body.AppendLine("## Usage");
        body.AppendLine();
        body.AppendLine("### GGUF");
        body.AppendLine();
        body.AppendLine("Load the `.gguf` file with a GGUF-capable runtime such as llama.cpp or a");
        body.AppendLine("compatible desktop application. The file is already quantized; do not re-quantize");
        body.AppendLine("it unless you want a smaller copy.");
        body.AppendLine();
        body.AppendLine("### SharpMind Studio");
        body.AppendLine();
        body.AppendLine("SharpMind Studio can load the GGUF directly, or import the `.smm` source when");
        body.AppendLine("the repository includes it. Put the file in the Models folder and choose it in");
        body.AppendLine("Chat or Work.");
        body.AppendLine();

        if (!string.IsNullOrWhiteSpace(card.BaseModel))
        {
            body.AppendLine("## Base model");
            body.AppendLine();
            body.AppendLine("Fine-tuned from `").Append(card.BaseModel.Trim()).AppendLine("`.");
            body.AppendLine("Check the base model's license before redistributing this model.");
            body.AppendLine();
        }

        if (metadata.HasEmbeddedExtras || card.IncludesSmm)
        {
            body.AppendLine("## Embedded assets");
            body.AppendLine();
            if (card.IncludesSmm)
            {
                body.AppendLine("This repository also includes the SharpMind `.smm` source container.");
                if (metadata.Plugins.Count > 0)
                    body.Append("- Plugin assemblies: ").AppendLine(string.Join(", ", metadata.Plugins));
                if (metadata.Skills.Count > 0)
                    body.Append("- Skills: ").AppendLine(string.Join(", ", metadata.Skills));
                if (metadata.HasSystemPrompt)
                    body.AppendLine("- An embedded system prompt");
                if (!metadata.HasEmbeddedExtras && !metadata.HasChatTemplate)
                    body.AppendLine("- No embedded plugins, skills or prompt were reported.");
            }
            else
            {
                body.AppendLine("The published GGUF does not contain the source model's embedded plugins,");
                body.AppendLine("skills or system prompt.");
            }
            body.AppendLine();
        }

        body.AppendLine("## Limitations");
        body.AppendLine();
        body.AppendLine("This model is a trained artifact, not a general-purpose assistant. Review its");
        body.AppendLine("outputs before relying on them, and check the training-data rights before using");
        body.AppendLine("it commercially.");
        body.AppendLine();

        body.AppendLine("## Provenance");
        body.AppendLine();
        if (!string.IsNullOrWhiteSpace(metadata.Checksum))
            body.AppendLine($"- Training-data fingerprint: `{metadata.Checksum}`");
        if (metadata.Lineage.Count > 0)
        {
            body.AppendLine("- Lineage:");
            foreach ((string key, string value) in metadata.Lineage.OrderBy(pair => pair.Key))
                body.Append("- ").Append(key).Append(": `").Append(SafeLineageValue(value)).AppendLine("`");
        }
        body.AppendLine($"- Source file: `{metadata.FileName}`");
        body.AppendLine($"- Repository: `{card.RepoId}`");
        body.AppendLine();

        return body.ToString();
    }

    public static string BuildMetadataJson(ModelPublishMetadata metadata, ModelPublishCard card) =>
        JsonSerializer.Serialize(new
        {
            schema = "sharp-mind-publish/1",
            repo = card.RepoId,
            artifact = card.ArtifactFileName,
            quantization = card.Quantization,
            includesSmm = card.IncludesSmm,
            smmFile = card.SmmFileName,
            source = new
            {
                format = metadata.Format,
                fileName = metadata.FileName,
                sizeBytes = metadata.SizeBytes,
                architecture = metadata.Architecture,
                parameters = metadata.ParameterCount,
                hiddenDim = metadata.HiddenDim,
                layers = metadata.NumLayers,
                heads = metadata.NumHeads,
                kvHeads = metadata.NumKvHeads,
                ffnDim = metadata.FfnDim,
                contextLength = metadata.ContextLength,
                vocabulary = metadata.VocabSize,
                tensorDtypes = metadata.TensorDtypes,
                checksum = metadata.Checksum,
                lineage = metadata.Lineage.ToDictionary(
                    pair => pair.Key,
                    pair => SafeLineageValue(pair.Value),
                    StringComparer.OrdinalIgnoreCase),
            },
            embedded = new
            {
                plugins = metadata.Plugins,
                skills = metadata.Skills,
                systemPrompt = metadata.HasSystemPrompt,
            },
        }, new JsonSerializerOptions { WriteIndented = true });

    public static string BuildGitAttributes() =>
        "*.gguf filter=lfs diff=lfs merge=lfs -text\n" +
        "*.smm filter=lfs diff=lfs merge=lfs -text\n";

    /// <summary>
    /// Lineage can carry a local base-model path. Publish only the file name so the
    /// repository never discloses the producer's folder layout.
    /// </summary>
    private static string SafeLineageValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.Contains('\\', StringComparison.Ordinal) || Path.IsPathRooted(value))
            return Path.GetFileName(value.TrimEnd('\\', '/'));
        return value;
    }

    private static string Yaml(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string EscapeTable(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string FormatCount(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.##", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
    }
}
