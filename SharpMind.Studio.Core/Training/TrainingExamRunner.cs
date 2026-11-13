using System.Security.Cryptography;
using System.Text;
using SharpMind.Core.Tensors;
using SharpMind.Data;
using SharpMind.Data.Batching;
using SharpMind.Data.Pipeline;
using SharpMind.Data.Records;
using SharpMind.Model;
using SharpMind.Training;
using SharpMind.Training.Loss;
using SharpMind.Training.Sft;

namespace SharpMind.Studio.Core.Training;

/// <summary>One exam document's contribution, used for the paired bootstrap.</summary>
public sealed record ExamDocumentScore(string Key, double Loss, long Tokens, long Bytes);

/// <summary>Held-out exam score for one packaged model.</summary>
public sealed class TrainingExamResult
{
    public required string ModelPath { get; init; }
    public double Loss { get; init; }
    public double Perplexity { get; init; }
    public int Batches { get; init; }
    public long Tokens { get; init; }
    public long Bytes { get; init; }
    public double BitsPerByte { get; init; }
    public List<ExamDocumentScore> Documents { get; init; } = [];
    public DateTime EvaluatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Scores a packaged <c>.smm</c> on the job's held-out exam split. The split is
/// the same deterministic, content-hashed slice the training run excluded, so
/// the document set was never trained on or used to pick a checkpoint.
///
/// Each document is scored once and its loss is retained, which gives the
/// promotion report a paired sample for a bootstrap instead of one aggregate
/// number. The run is bounded by <see cref="MaxDocuments"/> and
/// <see cref="MaxTokens"/> so a large corpus cannot turn an exam into a full
/// evaluation pass.
/// </summary>
public static class TrainingExamRunner
{
    /// <summary>Upper bound on documents scored by one exam.</summary>
    public const int MaxDocuments = 1024;

    /// <summary>Upper bound on loss-bearing tokens scored by one exam.</summary>
    public const long MaxTokens = 200_000;

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

        if (job.IsSft)
            return await RunSftAsync(job, modelPath, pluginsFolder, log, cancellationToken);

        log?.Invoke($"Exam: loading {Path.GetFileName(modelPath)}...");
        using Transformer model = SmmTrainingPipeline.LoadForInference(modelPath, out var tokenizer, out var config);
        log?.Invoke($"Exam: tokenizer {tokenizer.VocabSize}, context {config.MaxSeqLen}.");

        PipelineNode pipeline = TrainingRunner.BuildCleanPipeline(job, pluginsFolder);
        double fraction = Math.Clamp(job.TestPercent, 0, 90) / 100.0;
        int threshold = (int)Math.Round(fraction * DeterministicSplit.Buckets);
        var (testNode, _) = pipeline.Branch(
            "exam", doc => DeterministicSplit.Bucket(doc, job.Seed) < threshold);

        int seqLen = job.SeqLen > 0 ? job.SeqLen : 64;
        if (config.MaxSeqLen > 0) seqLen = Math.Min(seqLen, config.MaxSeqLen);
        seqLen = Math.Max(1, seqLen);

        var evaluator = new Evaluator(model, new CrossEntropyLoss());
        var scores = new List<ExamDocumentScore>();
        double weightedLoss = 0;
        long totalTokens = 0;
        long totalBytes = 0;

        await foreach (string doc in testNode.ReadAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(doc)) continue;

            int[] ids = tokenizer.Encode(doc);
            int rowLen = Math.Min(ids.Length - 1, seqLen);
            if (rowLen <= 0) continue;

            using TrainingBatch batch = BuildTextBatch(ids, rowLen);
            (float loss, _) = evaluator.Evaluate([batch]);
            long bytes = Encoding.UTF8.GetByteCount(doc);

            scores.Add(new ExamDocumentScore(DocumentKey(doc), loss, batch.RealTokenCount, bytes));
            weightedLoss += loss * (double)batch.RealTokenCount;
            totalTokens += batch.RealTokenCount;
            totalBytes += bytes;

            if (scores.Count >= MaxDocuments || totalTokens >= MaxTokens) break;
        }

        if (scores.Count == 0)
        {
            throw new InvalidOperationException(
                "The held-out exam produced no documents. The corpus is too small for the "
                + "requested exam percentage, or the split filtered everything out.");
        }

        double averageLoss = totalTokens > 0 ? weightedLoss / totalTokens : double.NaN;
        double perplexity = double.IsFinite(averageLoss) ? Math.Exp(averageLoss) : double.NaN;
        double bitsPerByte = ExamMetrics.BitsPerByte(averageLoss, totalTokens, totalBytes);

        log?.Invoke($"Exam: loss {averageLoss:0.0000}, ppl {perplexity:0.00}, "
            + $"{bitsPerByte:0.0000} bits/byte on {scores.Count:N0} document(s), {totalTokens:N0} tokens.");

        return new TrainingExamResult
        {
            ModelPath = modelPath,
            Loss = averageLoss,
            Perplexity = perplexity,
            Batches = scores.Count,
            Tokens = totalTokens,
            Bytes = totalBytes,
            BitsPerByte = bitsPerByte,
            Documents = scores,
        };
    }

    /// <summary>
    /// SFT exam: the same deterministic conversation slice the run excluded,
    /// rendered with the same formatter and scored on assistant tokens only.
    /// </summary>
    private static async Task<TrainingExamResult> RunSftAsync(
        TrainingJobSettings job,
        string modelPath,
        string pluginsFolder,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        log?.Invoke($"Exam: loading {Path.GetFileName(modelPath)}...");
        using Transformer model = SmmTrainingPipeline.LoadForInference(modelPath, out var tokenizer, out var config);
        string? chatTemplate = SmmTrainingPipeline.LoadChatTemplate(modelPath);
        log?.Invoke($"Exam: SFT tokenizer {tokenizer.VocabSize}, context {config.MaxSeqLen}.");

        IRecordSource source = TrainingRunner.BuildSftRecordSource(job, pluginsFolder);
        double fraction = Math.Clamp(job.TestPercent, 0, 90) / 100.0;
        int threshold = (int)Math.Round(fraction * DeterministicSplit.Buckets);
        IRecordSource examSource = new WhereRecordSource(
            source,
            record => DeterministicSplit.Bucket(RecordGrouping.SplitKey(record), job.Seed) < threshold,
            "exam");

        int seqLen = job.SeqLen > 0 ? job.SeqLen : 64;
        if (config.MaxSeqLen > 0) seqLen = Math.Min(seqLen, config.MaxSeqLen);
        seqLen = Math.Max(1, seqLen);

        var conversation = new ConversationTokenizer(
            TrainingRunner.ResolveSftFormatter(job.SftFormatter, chatTemplate), tokenizer);
        var evaluator = new Evaluator(model, new CrossEntropyLoss());
        var scores = new List<ExamDocumentScore>();
        double weightedLoss = 0;
        long totalTokens = 0;
        long totalBytes = 0;

        await foreach (DataRecord record in examSource.ReadRecordsAsync(cancellationToken))
        {
            TokenizedExample example = conversation.Encode(record);
            int rowLen = Math.Min(example.TokenIds.Count, seqLen);
            if (rowLen <= 0) continue;

            using TrainingBatch batch = BuildSftBatch(example, rowLen);
            if (batch.RealTokenCount <= 0) continue;

            (float loss, _) = evaluator.Evaluate([batch]);
            long bytes = Encoding.UTF8.GetByteCount(record.Text);

            scores.Add(new ExamDocumentScore(RecordGrouping.SplitKey(record), loss, batch.RealTokenCount, bytes));
            weightedLoss += loss * (double)batch.RealTokenCount;
            totalTokens += batch.RealTokenCount;
            totalBytes += bytes;

            if (scores.Count >= MaxDocuments || totalTokens >= MaxTokens) break;
        }

        if (scores.Count == 0)
        {
            throw new InvalidOperationException(
                "The held-out exam produced no conversations. The corpus is too small for the "
                + "requested exam percentage, or the split filtered everything out.");
        }

        double averageLoss = totalTokens > 0 ? weightedLoss / totalTokens : double.NaN;
        double perplexity = double.IsFinite(averageLoss) ? Math.Exp(averageLoss) : double.NaN;
        double bitsPerByte = ExamMetrics.BitsPerByte(averageLoss, totalTokens, totalBytes);

        log?.Invoke($"Exam: loss {averageLoss:0.0000}, ppl {perplexity:0.00}, "
            + $"{bitsPerByte:0.0000} bits/byte on {scores.Count:N0} conversation(s), {totalTokens:N0} tokens.");

        return new TrainingExamResult
        {
            ModelPath = modelPath,
            Loss = averageLoss,
            Perplexity = perplexity,
            Batches = scores.Count,
            Tokens = totalTokens,
            Bytes = totalBytes,
            BitsPerByte = bitsPerByte,
            Documents = scores,
        };
    }

    private static TrainingBatch BuildTextBatch(int[] ids, int rowLen)
    {
        var tokenIds = new Tensor<int>(1, rowLen);
        var labels = new Tensor<int>(1, rowLen);
        var attentionMask = new Tensor<float>(1, rowLen);

        Span<int> tokens = tokenIds.RowSpan(0);
        Span<int> targets = labels.RowSpan(0);
        Span<float> mask = attentionMask.RowSpan(0);

        for (int i = 0; i < rowLen; i++)
        {
            tokens[i] = ids[i];
            targets[i] = ids[i + 1];
            mask[i] = 1f;
        }

        return new TrainingBatch(tokenIds, labels, attentionMask, rowLen);
    }

    private static TrainingBatch BuildSftBatch(TokenizedExample example, int rowLen)
    {
        var tokenIds = new Tensor<int>(1, rowLen);
        var labels = new Tensor<int>(1, rowLen);
        var attentionMask = new Tensor<float>(1, rowLen);

        Span<int> tokens = tokenIds.RowSpan(0);
        Span<int> targets = labels.RowSpan(0);
        Span<float> mask = attentionMask.RowSpan(0);

        int scored = 0;
        for (int i = 0; i < rowLen; i++)
        {
            tokens[i] = example.TokenIds[i];
            mask[i] = 1f;
            if (i + 1 < rowLen && example.IsAssistant(i + 1))
            {
                targets[i] = example.TokenIds[i + 1];
                scored++;
            }
            else
            {
                targets[i] = -100;
            }
        }

        return new TrainingBatch(tokenIds, labels, attentionMask, scored);
    }

    private static string DocumentKey(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
