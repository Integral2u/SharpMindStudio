using SharpMind.Model.Config;
using SharpMind.Training;
using SharpMind.Training.LoRA;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Cheap pre-run estimate: parameters, training memory, packaged size, corpus
/// tokens and tokens-per-parameter. Time is intentionally absent until a
/// rehearsal measures real step throughput.
/// </summary>
public sealed record TrainingEstimate(
    long? Parameters,
    double? MemoryGiB,
    double? ModelSizeMb,
    long? CorpusTokens,
    double? TokensPerParameter,
    string Precision)
{
    public static TrainingEstimate Empty { get; } = new(null, null, null, null, null, "F32");
}

public static class TrainingEstimator
{
    public static TrainingEstimate Estimate(
        TrainingJobSettings job,
        TrainingCorpusEstimate corpus,
        bool lora = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(corpus);

        ModelConfig? config = null;
        long? parameters = null;
        try
        {
            int vocab = Math.Max(1, job.VocabSize > 0 ? job.VocabSize : job.TokenizerVocabSize);
            config = TrainingModelOptions.ResolveModelConfig(job, vocab);
            parameters = ModelSizer.CountParameters(config);
        }
        catch
        {
            // Invalid dimensions stay unknown rather than taking the panel down.
        }

        double? memory = null;
        if (config is not null)
        {
            try
            {
                int batch = Math.Max(1, job.BatchSize);
                if (lora)
                {
                    var loraConfig = new LoRAConfig
                    {
                        Rank = Math.Max(1, job.LoRARank),
                        Alpha = job.LoRAAlpha > 0 ? job.LoRAAlpha : Math.Max(1, job.LoRARank) * 2f,
                        TargetModules = job.LoRATargetModules.Count > 0
                            ? [.. job.LoRATargetModules]
                            : ["q_proj", "v_proj"],
                    };
                    memory = TrainingMemoryEstimator
                        .EstimateLora(config, loraConfig, batch, job.SeqLen)
                        .TotalGiB;
                }
                else
                {
                    memory = TrainingMemoryEstimator
                        .EstimateFull(config, batch, job.SeqLen)
                        .TotalGiB;
                }
            }
            catch
            {
                memory = null;
            }
        }

        double? sizeMb = parameters is { } count
            ? count * BytesPerParameter(job.QuantAwareTraining) / 1048576d
            : null;

        double? tokensPerParameter = corpus.Tokens is { } tokens
                                     && parameters is { } count2
                                     && count2 > 0
            ? (double)tokens / count2
            : null;

        return new TrainingEstimate(
            parameters,
            memory,
            sizeMb,
            corpus.Tokens,
            tokensPerParameter,
            PrecisionLabel(job.QuantAwareTraining));
    }

    public static string PrecisionLabel(string? quantAwareTraining)
        => string.IsNullOrWhiteSpace(quantAwareTraining) ? "F32" : quantAwareTraining;

    /// <summary>Approximate bytes per weight for the packaged precision (QAT target).</summary>
    public static double BytesPerParameter(string? quantAwareTraining)
        => quantAwareTraining?.ToUpperInvariant() switch
        {
            null or "" or "F32" => 4.0,
            "F16" => 2.0,
            "Q8_0" or "Q8_1" or "Q8_K" => 1.0625,
            "Q6_K" => 0.8125,
            "Q5_0" or "Q5_1" or "Q5_K" => 0.6875,
            "Q4_0" or "Q4_1" or "Q4_K" => 0.5625,
            "Q3_K" => 0.4375,
            "Q2_K" => 0.3125,
            _ => 4.0,
        };
}
