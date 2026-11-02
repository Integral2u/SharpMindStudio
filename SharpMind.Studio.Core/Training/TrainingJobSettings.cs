using System.Text.Json;
using System.Text.Json.Serialization;
using SharpMind.Core.Quantization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Training;

/// <summary>A configured training job Ã¢â‚¬â€ the persisted "what &amp; how to train" document.</summary>
public sealed class TrainingJobSettings
{
    /// <summary>File extension for saved training jobs (<c>*.smmt</c>).</summary>
    public const string JobExtension = ".smmt";

    /// <summary>
    /// Current on-disk container version. Bumped only when the wrapped
    /// <see cref="TrainingJobDocument"/> shape changes incompatibly; the job's own
    /// field additions remain additive and don't require a bump. Readers reject
    /// files written by a newer build and (via <see cref="Load"/>) transparently
    /// migrate unwrapped v1 files saved by older builds.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Display name; also the file name (<c>&lt;name&gt;.smmt</c>) under <see cref="DefaultFolder"/>.</summary>
    public string Name { get; set; } = "Untitled";

    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    // --- What: sources + stage chains --------------------------------

    /// <summary>Each configured data source with its own per-source stages.</summary>
    public List<JobSource> Sources { get; set; } = [];

    /// <summary>Global cleaning stages applied to the merged stream of all sources.</summary>
    public List<JobComponent> GlobalStages { get; set; } = [];

    /// <summary>
    /// Content fingerprints for each configured source (display name Ã¢â€ â€™ SHA-256),
    /// recorded at the start of a run. Lets a later run detect that the corpus
    /// changed Ã¢â‚¬â€ invalidating the path-only tokenizer cache Ã¢â‚¬â€ and stamps an
    /// audit fingerprint onto exported models. Unhashable sources (remote) are
    /// absent. Empty for jobs that have never run.
    /// </summary>
    public Dictionary<string, string?> SourceHashes { get; set; } = [];

    // --- Incremental training ------------------------------------------

    /// <summary>
    /// True to train only on the corpus deltas since the last run: per-file
    /// hashes (see <see cref="SourceFileHashes"/>) identify exactly which files
    /// are new or changed, and the pipeline is built over only those files.
    /// Runs always resume the latest checkpoint regardless of
    /// <see cref="StartFresh"/>/<see cref="ResumeFrom"/>. When no deltas exist
    /// the run is a clean no-op ("nothing to train").
    /// </summary>
    public bool IncrementalMode { get; set; }

    /// <summary>
    /// Per-source per-file content hashes (display name Ã¢â€ â€™ absolute file path Ã¢â€ â€™
    /// SHA-256), persisted after a successful incremental run. The next run
    /// diffs the current corpus against this map to decide what still needs
    /// training. Persisted on-disk alongside the checkpoints so it survives
    /// app restarts even before the job file is manually saved.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> SourceFileHashes { get; set; } = [];

    // --- Tokenizer ---------------------------------------------------------

    /// <summary>Target BPE vocabulary size (not yet trained).</summary>
    public int TokenizerVocabSize { get; set; } = 1024;

    /// <summary>
    /// Tokenizer kind, an additive choice. "Char" trains a character-level
    /// tokenizer whose vocabulary is the sorted set of distinct characters in
    /// the corpus (a byte/character GPT); "Bpe" (or null/empty) trains the
    /// usual byte-pair-encoding tokenizer. Defaults to BPE so existing jobs
    /// behave exactly as before.
    /// </summary>
    public string? TokenizerKind { get; set; }

    /// <summary>Where the trained tokenizer is cached on disk. Auto-derived from <see cref="Name"/> if null.</summary>
    public string? TokenizerCachePath { get; set; }

    /// <summary>
    /// Last vocabulary size recommended by the "Suggest vocab" analysis (advisory
    /// only; the applied value lives in <see cref="TokenizerVocabSize"/>). Null
    /// until an analysis has run. Survives job save/load so the wizard can show
    /// whether the cached tokenizer is still in line with the corpus.
    /// </summary>
    public int? SuggestedVocabSize { get; set; }

    // --- How: model + hyperparameters ------------------------------------

    /// <summary>True when sizing ran ModelSizer; the result is stored in the config fields below.</summary>
    public bool AutoSized { get; set; }

    public int VocabSize { get; set; }
    public int HiddenDim { get; set; } = 128;
    public int NumLayers { get; set; } = 4;
    public int NumHeads { get; set; } = 8;
    public int NumKvHeads { get; set; } = 8;
    public int FfnDim { get; set; } = 512;
    public int MaxSeqLen { get; set; } = 256;
    public float NormEps { get; set; } = 1e-3f;

    // --- Architecture & kernel options ---
    // Null means "use the architecture preset's default" (see TrainingPresets);
    // explicit values override the preset. Without a preset the resolved config
    // matches SharpMindConfig.Gpt so existing jobs behave exactly as before.

    /// <summary>
    /// Architecture preset key ("gpt2", "llama", "bert", "qwen", "mixtral").
    /// Null = manual/custom (resolves to GPT-2 style unless options are set).
    /// Used to pick activation/gate/ffn/norm defaults and to stamp the exported
    /// model's architecture name so loading reproduces the same config.
    /// </summary>
    public string? ArchitecturePreset { get; set; }

    /// <summary>Activation enum name ("GELU", "SiLU", "ReLU"); null = preset default.</summary>
    public string? Activation { get; set; }

    /// <summary>Gate enum name ("None", "SwiGLU", "GeGLU"); null = preset default.</summary>
    public string? Gate { get; set; }

    /// <summary>FFN enum name ("Dense", "Gated", "MoE"); null = preset default.</summary>
    public string? Ffn { get; set; }

    /// <summary>Attention enum name ("MHA", "GQA", "MQA"); null = derived from heads vs KV heads.</summary>
    public string? Attention { get; set; }

    /// <summary>Norm enum name ("RMSNorm", "LayerNorm"); null = preset default.</summary>
    public string? Norm { get; set; }

    /// <summary>Architecture kind ("Decoder", "Encoder"); null = preset default.</summary>
    public string? Arch { get; set; }

    /// <summary>Positional encoding enum name ("NoPE", "RoPE", "ALiBi", "Learned"); null = RoPE.</summary>
    public string? PositionalEncoding { get; set; }

    /// <summary>
    /// Optimizer name ("AdamW", "SGD"); null/empty = AdamW. SGD uses
    /// <see cref="SgdMomentum"/>; AdamW keeps its internal beta defaults.
    /// </summary>
    public string? Optimizer { get; set; }

    /// <summary>
    /// Accelerator plugin name (<c>IAcceleratorPlugin.Name</c>, case-insensitive)
    /// that supplies the training engine; null/empty/"CPU" = the built-in CPU
    /// engine. Resolved against the plugins folder at run time Ã¢â‚¬â€ a name that is
    /// not found fails the run rather than silently training on the CPU.
    /// </summary>
    public string? Accelerator { get; set; }

    /// <summary>SGD momentum coefficient (ignored by AdamW). Default 0.</summary>
    public float SgdMomentum { get; set; } = 0f;

    /// <summary>Total number of experts when <see cref="Ffn"/> is MoE.</summary>
    public int NumExperts { get; set; } = 8;

    /// <summary>Experts activated per token (top-k) when <see cref="Ffn"/> is MoE.</summary>
    public int TopKExperts { get; set; } = 2;

    public int TotalSteps { get; set; } = 200;
    public int LogInterval { get; set; } = 25;
    public int BatchSize { get; set; } = 1;
    public int SeqLen { get; set; } = 16;
    public int GradAccumSteps { get; set; } = 1;

    /// <summary>
    /// Batch strategy, an additive choice. "RandomWindow" samples each batch of
    /// <see cref="SeqLen"/>-token windows at random offsets from one flat corpus
    /// stream (nanoGPT-style); "Packing" (or null/empty) uses the default
    /// document-packing batcher. Defaults to packing so existing jobs behave
    /// exactly as before.
    /// </summary>
    public string? BatchingKind { get; set; }

    /// <summary>True when the job selects the character-level tokenizer.</summary>
    public bool UsesCharacterTokenizer
        => !string.IsNullOrWhiteSpace(TokenizerKind)
           && TokenizerKind.Equals("Char", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the job selects random-window batch sampling.</summary>
    public bool UsesRandomWindowBatching
        => !string.IsNullOrWhiteSpace(BatchingKind)
           && BatchingKind.Equals("RandomWindow", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Percentage of documents held out for validation (0–99), split by a stable
    /// content hash so the same corpus always splits identically. 0 (default)
    /// disables validation and leaves the training corpus untouched. Validation
    /// runs at the logging cadence, so every logged step carries a val loss.
    /// </summary>
    public double ValidationPercent { get; set; }

    /// <summary>
    /// Percentage of documents reserved for the promotion exam (0–90). The exam
    /// slice is excluded from training and validation and is scored only after a
    /// run, so it can judge candidates honestly. The split uses the same stable
    /// content hash as <see cref="ValidationPercent"/> over a disjoint bucket
    /// range. 0 (default) means no exam.
    /// </summary>
    public double TestPercent { get; set; }

    public float LearningRate { get; set; } = 8e-4f;
    public float MinLr { get; set; } = 3e-5f;
    public int WarmupSteps { get; set; } = 20;
    public float WeightDecay { get; set; } = 0.1f;
    public float GradClipNorm { get; set; } = 1.0f;
    public float LabelSmoothing { get; set; } = 0.1f;

    /// <summary>QAT target as its enum name ("F32" disables QAT); null = F32. See <see cref="QuantDType"/>.</summary>
    public string? QuantAwareTraining { get; set; }

    // --- Where: checkpoints + export -------------------------------------

    public int CheckpointInterval { get; set; } = 50;

    /// <summary>Retained step checkpoints during a run: 0=ALL, N=keep newest N, negative=none (final only).</summary>
    public int KeepRecent { get; set; } = 3;

    /// <summary>Final .smm export path (browseable afterwards). May name a <c>.smm</c>
    /// file or an output folder; when blank the run defaults it beside the job's
    /// <c>.smmt</c> file (<see cref="SourcePath"/>), or under <see cref="DefaultFolder"/>
    /// while the job has not been saved yet.</summary>
    public string? ExportPath { get; set; }

    /// <summary>True when <see cref="ExportPath"/> points at a concrete <c>.smm</c> file rather than an output folder.</summary>
    public static bool IsExportFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Directory.Exists(path)) return false;
        return Path.GetExtension(path).Length > 0 || (!Directory.Exists(Path.GetDirectoryName(path) ?? "") && !path.EndsWith(Path.DirectorySeparatorChar.ToString()));
    }

    /// <summary>
    /// The output folder that receives the <c>checkpoints-{Name}</c> subfolder and,
    /// by default, the final <c>.smm</c>. When <see cref="ExportPath"/> names a file
    /// the folder is its parent; otherwise the path itself is the folder.
    /// A blank path defaults to the folder holding the job's <c>.smmt</c>
    /// (<see cref="SourcePath"/>), falling back to <see cref="DefaultFolder"/>
    /// for a job not yet saved anywhere.
    /// </summary>
    public string ExportFolder
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExportPath)) return SourcePathFolder ?? DefaultFolder;
            if (IsExportFile(ExportPath)) return Path.GetDirectoryName(ExportPath) ?? DefaultFolder;
            return ExportPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>Folder holding this job's <c>.smmt</c> file, when it has one.</summary>
    private string? SourcePathFolder
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourcePath)) return null;
            string dir = Path.GetDirectoryName(SourcePath) ?? "";
            return dir.Length == 0 ? null : dir;
        }
    }

    /// <summary>
    /// Checkpoint directory, always derived from the export path base plus the
    /// job name prefixed with "checkpoints": <c>&lt;ExportFolder&gt;/checkpoints-{Name}</c>.
    /// Follows <see cref="ExportFolder"/>'s default (the job's <c>.smmt</c> folder).
    /// </summary>
    public string CheckpointDir => Path.Combine(ExportFolder, $"checkpoints-{Sanitize(Name)}");

    /// <summary>
    /// Resolves <see cref="ExportPath"/> to a concrete writable <c>.smm</c> file:
    /// a folder is used as-is with <c>{Sanitize(Name)}.smm</c> appended, a file path
    /// is used verbatim, and a blank path defaults beside the job's <c>.smmt</c>
    /// file (or under <see cref="DefaultFolder"/> when the job is unsaved).
    /// </summary>
    public string ExportFilePath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExportPath))
                return SourcePathFolder is { } folder
                    ? Path.Combine(folder, Sanitize(Name) + ".smm")
                    : Path.Combine(DefaultFolder, Sanitize(Name) + ".smm");
            return IsExportFile(ExportPath)
                ? ExportPath
                : Path.Combine(ExportFolder, Sanitize(Name) + ".smm");
        }
    }

    /// <summary>
    /// Resolves where the trained BPE tokenizer is cached: <see cref="TokenizerCachePath"/>
    /// when set, otherwise a <c>{Name}.tokenizer.json</c> beside the job's <c>.smmt</c>
    /// file (or under <see cref="DefaultFolder"/> when the job is unsaved), so a job's
    /// tokenizer sits with its checkpoints and export rather than in the shared folder.
    /// </summary>
    public string TokenizerFilePath => TokenizerCachePath
        ?? (SourcePathFolder is { } folder
            ? Path.Combine(folder, Sanitize(Name) + ".tokenizer.json")
            : Path.Combine(DefaultFolder, Sanitize(Name) + ".tokenizer.json"));

    /// <summary>
    /// Resume source; null = auto (see <see cref="StartFresh"/>). A pretrain uses
    /// a checkpoint directory. A fine-tune accepts either a checkpoint directory
    /// (the adapter sidecar inside it) or a <c>.smlora</c> adapter file.
    /// </summary>
    public string? ResumeFrom { get; set; }

    /// <summary>
    /// Warm restart: reuse this checkpoint's weights and migrated optimizer
    /// state, continuing its step counter/schedule, as opposed to a strict
    /// resume. The run's pre-flight arms this automatically when it grows the
    /// tokenizer (or finds a checkpoint whose vocabulary differs), so a widened
    /// embedding can reuse the old weights' overlapping rows and moments.
    /// Ignored when <see cref="StartFresh"/> is true. Null = no warm restart.
    /// </summary>
    public string? WarmRestartFrom { get; set; }

    /// <summary>
    /// The vocabulary size the tokenizer on disk was actually trained with,
    /// recorded when a run trains or loads it. Lets the wizard (and the run's
    /// pre-flight) detect that the cached tokenizer no longer matches a
    /// grown/applied vocab. Null when unknown (e.g. a tokenizer loaded from an
    /// external file). Distinct from <see cref="TokenizerVocabSize"/> (the target
    /// passed to the BPE trainer).
    /// </summary>
    public int? TrainedTokenizerVocab { get; set; }

    /// <summary>
    /// Runtime-only flag: run a short, disposable rehearsal. Checkpoints go to a
    /// temp folder, nothing is merged or exported, and incremental state is not
    /// recorded. Never persisted with a saved job.
    /// </summary>
    [JsonIgnore]
    public bool Rehearsal { get; set; }

    // --- Fork lineage ------------------------------------------------------
    // A fork is a new job cloned from a finished run; it keeps the parent link
    // for history and reuses the parent checkpoint where possible.

    /// <summary>Run-record id this job was forked from, when it is a fork.</summary>
    public string? ForkedFromRunId { get; set; }

    /// <summary>The first run in the fork tree (itself for an original run).</summary>
    public string? ForkRootId { get; set; }

    /// <summary>Human-readable fork point, e.g. "step 120".</summary>
    public string? ForkPoint { get; set; }

    /// <summary>Optional variant label for the branch.</summary>
    public string? Variant { get; set; }

    /// <summary>
    /// True to train from random weights even when checkpoints exist, ignoring
    /// <see cref="ResumeFrom"/>. False (default) resumes the latest checkpoint
    /// under <see cref="CheckpointDir"/>, or starts fresh when none exist.
    /// </summary>
    public bool StartFresh { get; set; }

    // --- Fine-tune (LoRA) -------------------------------------------------

    /// <summary>
    /// Base <c>.smm</c> to fine-tune with LoRA. Null/blank = ordinary corpus
    /// pretraining from random weights. A fine-tune reuses the base's
    /// architecture, tokenizer and chat template; only the LoRA adapters train,
    /// and they are merged and/or saved as an adapter file at the end.
    /// </summary>
    public string? BaseModelPath { get; set; }

    /// <summary>True when this job fine-tunes <see cref="BaseModelPath"/> instead of pretraining.</summary>
    public bool IsFineTune => !string.IsNullOrWhiteSpace(BaseModelPath);

    /// <summary>File extension for an adapter-only export (<c>*.smlora</c>).</summary>
    public const string AdapterExtension = ".smlora";

    /// <summary>
    /// Fold the trained adapters into the base weights and export an ordinary
    /// <c>.smm</c>. Default true. Set false to save only an adapter file.
    /// </summary>
    public bool MergeOnExport { get; set; } = true;

    /// <summary>
    /// Save the trained adapters as a <c>.smlora</c> file (base fingerprint,
    /// rank, alpha, targets and weights) alongside or instead of the merged
    /// model. Default false.
    /// </summary>
    public bool SaveAdapterFile { get; set; }

    /// <summary>
    /// Destination for the adapter file. Null/blank = beside
    /// <see cref="ExportFilePath"/> with the extension swapped to
    /// <see cref="AdapterExtension"/>.
    /// </summary>
    public string? AdapterPath { get; set; }

    /// <summary>Resolved adapter destination, always with the adapter extension.</summary>
    public string AdapterFilePath
    {
        get
        {
            string path = string.IsNullOrWhiteSpace(AdapterPath) ? ExportFilePath : AdapterPath.Trim();
            return Path.ChangeExtension(path, AdapterExtension);
        }
    }

    /// <summary>LoRA rank (the adapter's bottleneck width).</summary>
    public int LoRARank { get; set; } = 8;

    /// <summary>LoRA alpha; the adapter scale is <see cref="LoRAAlpha"/> / <see cref="LoRARank"/>.</summary>
    public float LoRAAlpha { get; set; } = 16f;

    /// <summary>
    /// Projections to adapt, using Hugging Face module names: q_proj, k_proj,
    /// v_proj, o_proj, gate_proj, up_proj, down_proj. Empty = the LoRAModel default.
    /// </summary>
    public List<string> LoRATargetModules { get; set; } = ["q_proj", "k_proj", "v_proj", "o_proj"];

    // --- Embed in the exported .smm ----------------------------

    /// <summary>
    /// Path to a <c>.txt</c>/<c>.md</c> file whose contents become the embedded
    /// system prompt. The file is read at export time so edits don't force a
    /// wizard round-trip. Optional.
    /// </summary>
    public string? SystemPromptPath { get; set; }

    /// <summary>
    /// Folder containing <c>*.md</c> skill documents; each file is embedded as
    /// one skill. Read at export time. Optional.
    /// </summary>
    public string? SkillsFolder { get; set; }

    /// <summary>Tool DLL paths to embed in the exported .smm file. Optional.</summary>
    public List<string> PluginDllPaths { get; set; } = [];

    // --- Persistence -------------------------------------------------------

    /// <summary>
    /// The file this job was loaded from, or last saved to Ã¢â‚¬â€ in-memory only,
    /// never written into the job file or the dirty-tracking payload, so a
    /// loaded job keeps saving back to exactly where it came from across
    /// wizard round-trips. Null for a new job (its first save lands in
    /// <see cref="DefaultFolder"/>).
    /// </summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }

    public static string DefaultFolder => StudioPaths.Training;

    public bool Save(string path, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SavedAtUtc = DateTime.UtcNow;
            var doc = new TrainingJobDocument
            {
                Version = CurrentFormatVersion,
                SavedAtUtc = SavedAtUtc,
                Job = this,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, Options));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static TrainingJobSettings? Load(string path, out string? error)
    {
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            using var root = JsonDocument.Parse(json);
            var rootElement = root.RootElement;

            // Versioned container produced by this build. Older (v1, unwrapped)
            // files are plain TrainingJobSettings JSON and load straight through.
            if (rootElement.TryGetProperty("version", out var versionProp))
            {
                int version = versionProp.GetInt32();
                if (version > CurrentFormatVersion)
                {
                    error = $"Job file format v{version} is newer than this build supports (v{CurrentFormatVersion}).";
                    return null;
                }

                if (version < CurrentFormatVersion)
                {
                    // Older wrapped container Ã¢â‚¬â€ the inner job shape is the same
                    // payload type, just deserialize it. (Future migrations hook
                    // in here.)
                }

                if (rootElement.TryGetProperty("job", out var jobProp) && jobProp.ValueKind == JsonValueKind.Object)
                    return jobProp.Deserialize<TrainingJobSettings>(Options);
                error = "Job file is missing its 'job' payload.";
                return null;
            }

            return rootElement.Deserialize<TrainingJobSettings>(Options);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Serializes just the job payload so the training wizard can detect unsaved
    /// edits by comparing a baseline snapshot (taken after Load/Save) against
    /// the live job.
    /// </summary>
    public string ToPayloadJson() => JsonSerializer.Serialize(this, Options);

    public static List<string> ListSaved()
        => ListSavedIn(DefaultFolder);

    /// <summary>Lists saved job files in <paramref name="folder"/> matching <see cref="JobExtension"/>.</summary>
    public static List<string> ListSavedIn(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.GetFiles(folder, "*" + JobExtension).OrderByDescending(File.GetLastWriteTimeUtc)];
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly QuantDType[] QatTargets =
    [
        QuantDType.F32, QuantDType.F16, QuantDType.Q8_0, QuantDType.Q4_0,
    ];

    /// <summary>Deep copy via the same JSON shape used for `.smmt` persistence.</summary>
    public TrainingJobSettings Clone()
        => JsonSerializer.Deserialize<TrainingJobSettings>(
               JsonSerializer.Serialize(this, Options), Options)
           ?? throw new InvalidOperationException("Could not clone the training job.");

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "job" : name.Trim();
    }
}

/// <summary>
/// The on-disk wrapper around <see cref="TrainingJobSettings"/>: a stable version
/// stamp plus the saved-at time, with the job's JSON kept whole inside
/// <c>job</c>. The version lets a future format change be detected (and
/// migrated) instead of failing to parse; older builds saved the bare job
/// object, which <see cref="TrainingJobSettings.Load"/> still accepts so legacy
/// <c>*.smmt</c> files (e.g. <c>shakespeare-job.smmt</c>) survive.
/// </summary>
public sealed class TrainingJobDocument
{
    /// <summary>On-disk format version written by this build.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public int Version { get; set; } = TrainingJobSettings.CurrentFormatVersion;

    [System.Text.Json.Serialization.JsonPropertyName("savedAtUtc")]
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonPropertyName("job")]
    public TrainingJobSettings Job { get; set; } = new();
}