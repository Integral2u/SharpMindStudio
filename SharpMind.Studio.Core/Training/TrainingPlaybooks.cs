namespace SharpMind.Studio.Core.Training;

public enum TrainingPlaybookKind
{
    Pretrain,
    FineTune,
}

/// <summary>Starting values for the Improve form's LoRA controls.</summary>
public sealed record TrainingFineTuneRecipe(
    int Rank,
    float Alpha,
    IReadOnlyList<string> Targets,
    int Steps,
    float LearningRate,
    double ValidationPercent,
    double TestPercent,
    string Output);

/// <summary>
/// A prefab training recipe. Playbooks fill the editor with a coherent starting
/// point; they are not benchmarks and do not claim a result. A pretrain playbook
/// supplies a <see cref="CreateJob"/> factory, a fine-tune playbook supplies
/// <see cref="FineTune"/> values.
/// </summary>
public sealed class TrainingPlaybook
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Eyebrow { get; init; }
    public required string Description { get; init; }
    public required string Outcome { get; init; }
    public required TrainingPlaybookKind Kind { get; init; }

    public Func<TrainingJobSettings>? CreateJob { get; init; }
    public TrainingFineTuneRecipe? FineTune { get; init; }
}

/// <summary>
/// The curated recipes behind **Training → Playbooks**. Each one is a complete,
/// valid set of editor values: the user still supplies the corpus (and, for a
/// fine-tune, the base model).
/// </summary>
public static class TrainingPlaybooks
{
    public static IReadOnlyList<TrainingPlaybook> All { get; } =
    [
        new TrainingPlaybook
        {
            Id = "smoke",
            Name = "Smoke test",
            Eyebrow = "First run",
            Description = "Prove the corpus, tokenizer, checkpoints and export path with a tiny character model. "
                        + "No validation or exam, so it finishes in a minute rather than producing a useful model.",
            Outcome = "A testable .smm and a clean log.",
            Kind = TrainingPlaybookKind.Pretrain,
            CreateJob = () => Job("smoke-test", job =>
            {
                UsePreset(job, "gpt2");
                Size(job, 32, 1, 4, 4, 64, 64);
                job.TokenizerKind = "Char";
                job.TotalSteps = 20;
                job.LogInterval = 1;
                job.BatchSize = 2;
                job.SeqLen = 32;
                job.LearningRate = 1e-3f;
                job.MinLr = 1e-4f;
                job.WarmupSteps = 2;
                job.ValidationPercent = 0;
                job.TestPercent = 0;
                job.CheckpointInterval = 10;
                job.KeepRecent = 1;
            }),
        },

        new TrainingPlaybook
        {
            Id = "domain-small",
            Name = "Small domain model",
            Eyebrow = "Pretrain",
            Description = "A 128-wide, 4-layer BPE model for a focused corpus. Holds back validation and exam "
                        + "slices so the first real run has curves and an honest comparison later.",
            Outcome = "A packaged .smm with a validation curve and exam split.",
            Kind = TrainingPlaybookKind.Pretrain,
            CreateJob = () => Job("domain-small", job =>
            {
                UsePreset(job, "gpt2");
                Size(job, 128, 4, 8, 8, 512, 256);
                job.TokenizerKind = "Bpe";
                job.TokenizerVocabSize = 4096;
                job.TotalSteps = 1_000;
                job.LogInterval = 25;
                job.BatchSize = 4;
                job.SeqLen = 128;
                job.LearningRate = 3e-4f;
                job.MinLr = 3e-5f;
                job.WarmupSteps = 50;
                job.ValidationPercent = 10;
                job.TestPercent = 10;
                job.CheckpointInterval = 100;
                job.KeepRecent = 3;
            }),
        },

        new TrainingPlaybook
        {
            Id = "chat-base",
            Name = "Chat-ready base",
            Eyebrow = "Pretrain",
            Description = "A LLaMA-style gated model with a wider vocabulary and context. Still a small local "
                        + "model, not a production base; tune it with a LoRA playbook once the curves settle.",
            Outcome = "A F16-trained .smm richer than the tiny/small starters.",
            Kind = TrainingPlaybookKind.Pretrain,
            CreateJob = () => Job("chat-base", job =>
            {
                UsePreset(job, "llama");
                Size(job, 256, 6, 8, 8, 1024, 512);
                job.TokenizerKind = "Bpe";
                job.TokenizerVocabSize = 8192;
                job.TotalSteps = 2_000;
                job.LogInterval = 25;
                job.BatchSize = 4;
                job.SeqLen = 256;
                job.LearningRate = 2e-4f;
                job.MinLr = 2e-5f;
                job.WarmupSteps = 100;
                job.WeightDecay = 0.02f;
                job.ValidationPercent = 10;
                job.TestPercent = 10;
                job.CheckpointInterval = 200;
                job.KeepRecent = 3;
                job.QuantAwareTraining = "F16";
            }),
        },

        new TrainingPlaybook
        {
            Id = "qat-q8",
            Name = "Q8_0 packaging run",
            Eyebrow = "Precision",
            Description = "Train with a Q8_0-simulated forward pass so the result tolerates the packaged "
                        + "precision. Master weights stay F32; check the curves before relying on it.",
            Outcome = "A model trained against Q8_0 rounding and a QAT validation pass.",
            Kind = TrainingPlaybookKind.Pretrain,
            CreateJob = () => Job("qat-q8", job =>
            {
                UsePreset(job, "gpt2");
                Size(job, 128, 4, 8, 8, 512, 256);
                job.TokenizerKind = "Bpe";
                job.TokenizerVocabSize = 4096;
                job.TotalSteps = 500;
                job.LogInterval = 25;
                job.BatchSize = 4;
                job.SeqLen = 128;
                job.LearningRate = 2e-4f;
                job.MinLr = 2e-5f;
                job.WarmupSteps = 25;
                job.ValidationPercent = 10;
                job.TestPercent = 10;
                job.CheckpointInterval = 50;
                job.KeepRecent = 3;
                job.QuantAwareTraining = "Q8_0";
            }),
        },

        new TrainingPlaybook
        {
            Id = "lora-domain",
            Name = "LoRA domain tune",
            Eyebrow = "Improve",
            Description = "Adapt a packaged .smm to new or corrective text. The base stays frozen and the "
                        + "adapters merge into a standalone model at export.",
            Outcome = "A merged .smm that keeps the base tokenizer, prompt and skills.",
            Kind = TrainingPlaybookKind.FineTune,
            FineTune = new TrainingFineTuneRecipe(
                Rank: 8,
                Alpha: 16f,
                Targets: ["q_proj", "k_proj", "v_proj", "o_proj", "up_proj", "down_proj"],
                Steps: 100,
                LearningRate: 2e-4f,
                ValidationPercent: 10,
                TestPercent: 10,
                Output: "merged"),
        },

        new TrainingPlaybook
        {
            Id = "lora-light",
            Name = "Light style tune",
            Eyebrow = "Improve",
            Description = "A small-rank adapter over the attention projections for a light style or tone shift. "
                        + "Save the adapter alone when you want to keep it portable.",
            Outcome = "A merged model or a small .smlora beside the base.",
            Kind = TrainingPlaybookKind.FineTune,
            FineTune = new TrainingFineTuneRecipe(
                Rank: 4,
                Alpha: 8f,
                Targets: ["q_proj", "v_proj"],
                Steps: 50,
                LearningRate: 1e-4f,
                ValidationPercent: 10,
                TestPercent: 10,
                Output: "adapter"),
        },
    ];

    private static TrainingJobSettings Job(string name, Action<TrainingJobSettings> configure)
    {
        TrainingJobSettings job = TrainingJobDefaults.Create();
        job.Name = name;
        configure(job);
        return job;
    }

    private static void UsePreset(TrainingJobSettings job, string key)
        => TrainingModelOptions.Apply(
            job, TrainingModelOptions.Presets.Single(p => p.ArchitecturePresetKey == key));

    private static void Size(
        TrainingJobSettings job, int hidden, int layers, int heads, int kvHeads, int ffn, int context)
    {
        job.HiddenDim = hidden;
        job.NumLayers = layers;
        job.NumHeads = heads;
        job.NumKvHeads = kvHeads;
        job.FfnDim = ffn;
        job.MaxSeqLen = context;
    }
}
