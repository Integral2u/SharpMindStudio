using SharpMind.Data;
using SharpMind.Data.Batching;
using SharpMind.Data.Pipeline;
using SharpMind.Model;
using SharpMind.Training;
using SharpMind.Training.Loss;

namespace SharpMind.Studio.Core.Training;

/// <summary>Held-out exam score for one packaged model.</summary>
public sealed class TrainingExamResult
{
    public required string ModelPath { get; init; }
    public double Loss { get; init; }
    public double Perplexity { get; init; }
    public int Batches { get; init; }
    public long Tokens { get; init; }
    public DateTime EvaluatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Scores a packaged <c>.smm</c> on the job's held-out exam split. The split is
/// the same deterministic, content-hashed slice the training run excluded, so
/// the document set was never trained on or used to pick a checkpoint.
///
/// The exam loads the model and its embedded tokenizer, rebuilds the job's
/// cleaned corpus pipeline and reads a bounded number of test batches. It is
/// deliberately separate from <see cref="TrainingRunner"/>: training must never
/// look at this slice, and the exam must be runnable later against any exported
/// candidate.
/// </summary>
public static class TrainingExamRunner
{
    public const int MaxBatches = 8;

    public static async Task<TrainingExamResult> RunAsync(
        TrainingJobSettings job,
        string modelPath,
        string pluginsFolder,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Exam model not found: {modelPath}", modelPath);
        if (job.TestPercent <= 0)
            throw new InvalidOperationException(
                "This job has no held-out exam split. Set the held-out exam percentage and run "
                + "the model again so the exam documents are excluded from training.");
        if (job.Sources.Count == 0)
            throw new InvalidOperationException("No data sources configured for this job.");

        log?.Invoke($"Exam: loading {Path.GetFileName(modelPath)}...");
        using Transformer model = SmmTrainingPipeline.LoadForInference(modelPath, out var tokenizer, out var config);
        log?.Invoke($"Exam: tokenizer {tokenizer.VocabSize}, context {config.MaxSeqLen}.");

        PipelineNode pipeline = TrainingRunner.BuildCleanPipeline(job, pluginsFolder);
        double fraction = Math.Clamp(job.TestPercent, 0, 90) / 100.0;
        int threshold = (int)Math.Round(fraction * DeterministicSplit.Buckets);
        var (testNode, _) = pipeline.Branch(
            "exam", doc => DeterministicSplit.Bucket(doc, TrainingRunner.Seed) < threshold);

        int seqLen = job.SeqLen > 0 ? job.SeqLen : 64;
        if (config.MaxSeqLen > 0) seqLen = Math.Min(seqLen, config.MaxSeqLen);

        var batcher = new RandomWindowBatcher(
            batchSize: Math.Max(1, job.BatchSize),
            seqLen: Math.Max(1, seqLen),
            seed: TrainingRunner.Seed + 2);
        var loader = new DataLoader(testNode, s => tokenizer.Encode(s), batcher, prefetchBuffer: 1);

        var batches = new List<TrainingBatch>();
        try
        {
            await foreach (TrainingBatch batch in loader.LoadAsync(cancellationToken: cancellationToken))
            {
                batches.Add(batch);
                if (batches.Count >= MaxBatches) break;
            }

            if (batches.Count == 0)
                throw new InvalidOperationException(
                    "The held-out exam produced no documents. The corpus is too small for the "
                    + "requested exam percentage, or the split filtered everything out.");

            var evaluator = new Evaluator(model, new CrossEntropyLoss());
            (float loss, float perplexity) = evaluator.Evaluate(batches);
            long tokens = batches.Sum(batch => (long)batch.RealTokenCount);

            log?.Invoke($"Exam: loss {loss:0.0000}, ppl {perplexity:0.00} "
                + $"on {batches.Count} batch(es), {tokens:N0} tokens.");

            return new TrainingExamResult
            {
                ModelPath = modelPath,
                Loss = loss,
                Perplexity = perplexity,
                Batches = batches.Count,
                Tokens = tokens,
            };
        }
        finally
        {
            foreach (TrainingBatch batch in batches) batch.Dispose();
        }
    }
}
