using SharpMind.Data.Metadata;
using SharpMind.Data.Pipeline;
using SharpMind.Data.Records;
using SharpMind.Inference.Chat.PromptFormatters;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Tokenization;
using SharpMind.Training;
using SharpMind.Training.Sft;

namespace SharpMind.Studio.Core.Training;

/// <summary>One rendered conversation sample shown by the SFT preflight.</summary>
public sealed record SftPreviewExample(
    string Layout,
    int Turns,
    int Tokens,
    int AssistantTokens,
    bool Truncated,
    string Rendered);

/// <summary>
/// A preflight report for an SFT dataset: what the parser found, how the
/// deterministic split will divide it, and a few rendered conversations. The
/// same record source and split rules the runner uses are exercised here, so a
/// start that would fail on empty data is visible before training begins.
/// </summary>
public sealed class SftDatasetPreview
{
    public required string SourceDescription { get; init; }
    public int Files { get; init; }
    public long Records { get; init; }
    public long Skipped { get; init; }
    public bool LimitedScan { get; init; }
    public IReadOnlyDictionary<string, int> Layouts { get; init; } = new Dictionary<string, int>();
    public int TrainRecords { get; init; }
    public int ValidationRecords { get; init; }
    public int ExamRecords { get; init; }
    public bool Tokenized { get; init; }
    public double AverageTokens { get; init; }
    public double AverageAssistantTokens { get; init; }
    public int MaxTokens { get; init; }
    public int TruncatedRecords { get; init; }
    public int SeqLen { get; init; }
    public string Formatter { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<SftPreviewExample> Examples { get; init; } = [];
}

/// <summary>
/// Reads an SFT dataset once and reports the counts the Improve panel needs:
/// valid records, skipped lines, detected layouts, train/validation/exam sizes,
/// token lengths and formatter-rendered samples.
/// </summary>
public static class SftDatasetAnalyzer
{
    public const int DefaultMaxExamples = 3;

    public static async Task<SftDatasetPreview> AnalyzeAsync(
        TrainingJobSettings job,
        string pluginsFolder,
        string? baseModelPath = null,
        int maxRecords = 0,
        int maxExamples = DefaultMaxExamples,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Sources.Count == 0)
            throw new InvalidOperationException("Add a chat JSONL source first.");

        baseModelPath ??= job.BaseModelPath;
        var components = ComponentRegistry.ScanFolder(pluginsFolder, out _);
        SftRecordSourceSet set = TrainingRunner.BuildSftRecordSourceSet(job, components);

        ModelConfig? config = null;
        Tokenizer? tokenizer = null;
        string? chatTemplate = null;
        if (!string.IsNullOrWhiteSpace(baseModelPath) && File.Exists(baseModelPath))
        {
            ModelMetaData meta = SmmLoader.LoadMeta(baseModelPath);
            config = SmmLoader.LoadConfig(meta);
            tokenizer = SmmLoader.LoadTokenizerFromMeta(meta);
            chatTemplate = SmmTrainingPipeline.LoadChatTemplate(baseModelPath);
        }

        IChatPromptFormatter formatter = TrainingRunner.ResolveSftFormatter(job.SftFormatter, chatTemplate);
        ConversationTokenizer? conversation = tokenizer is null
            ? null
            : new ConversationTokenizer(formatter, tokenizer);

        int seqLen = Math.Max(1, job.SeqLen > 0 ? job.SeqLen : 64);
        if (config is { MaxSeqLen: > 0 }) seqLen = Math.Min(seqLen, config.MaxSeqLen);

        double testFraction = Math.Clamp(job.TestPercent, 0, 90) / 100.0;
        double valFraction = Math.Clamp(job.ValidationPercent, 0, 90) / 100.0;
        int examThreshold = (int)Math.Round(testFraction * DeterministicSplit.Buckets);
        int valThreshold = (int)Math.Round((testFraction + valFraction) * DeterministicSplit.Buckets);

        long records = 0;
        long totalTokens = 0;
        long totalAssistantTokens = 0;
        int maxTokens = 0;
        int truncatedRecords = 0;
        int trainRecords = 0;
        int validationRecords = 0;
        int examRecords = 0;
        int encodeFailures = 0;
        bool limitHit = false;
        var layouts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var examples = new List<SftPreviewExample>(Math.Max(0, maxExamples));

        await foreach (DataRecord record in set.Records.ReadRecordsAsync(cancellationToken))
        {
            if (maxRecords > 0 && records >= maxRecords)
            {
                limitHit = true;
                break;
            }

            records++;
            int bucket = DeterministicSplit.Bucket(RecordGrouping.SplitKey(record), TrainingRunner.Seed);
            if (bucket < examThreshold) examRecords++;
            else if (bucket < valThreshold) validationRecords++;
            else trainRecords++;

            string? layoutTag = record.Tags
                .FirstOrDefault(tag => tag.StartsWith("layout:", StringComparison.Ordinal));
            string layout = layoutTag is null ? "unknown" : layoutTag["layout:".Length..];
            layouts[layout] = layouts.TryGetValue(layout, out int count) ? count + 1 : 1;

            int recordTokens = 0;
            int recordAssistant = 0;
            bool recordTruncated = false;
            if (conversation is not null)
            {
                try
                {
                    var tokenized = conversation.Encode(record);
                    recordTokens = tokenized.TokenIds.Count;
                    recordAssistant = tokenized.AssistantMask is null
                        ? recordTokens
                        : tokenized.AssistantMask.Count(value => value);
                    recordTruncated = recordTokens > seqLen;
                    totalTokens += recordTokens;
                    totalAssistantTokens += recordAssistant;
                    maxTokens = Math.Max(maxTokens, recordTokens);
                    if (recordTruncated) truncatedRecords++;
                }
                catch
                {
                    encodeFailures++;
                }
            }
            else
            {
                recordTokens = record.Text.Length;
                recordAssistant = recordTokens;
                totalTokens += recordTokens;
                totalAssistantTokens += recordAssistant;
                maxTokens = Math.Max(maxTokens, recordTokens);
            }

            if (examples.Count < maxExamples)
            {
                string rendered = record.Text;
                if (tokenizer is not null && record.Turns is { Count: > 0 })
                {
                    try
                    {
                        rendered = formatter.Format(
                            ConversationTokenizer.ToMessages(record.Turns),
                            tokenizer,
                            addBos: false);
                    }
                    catch
                    {
                        // Keep the transcript when a template cannot render the sample.
                    }
                }

                examples.Add(new SftPreviewExample(
                    layout,
                    record.Turns?.Count ?? 0,
                    recordTokens,
                    recordAssistant,
                    recordTruncated,
                    Truncate(rendered, 1_500)));
            }

            if (records % 1_000 == 0) progress?.Report(records);
        }

        progress?.Report(records);

        var warnings = new List<string>();
        if (records == 0)
        {
            warnings.Add("No usable conversation records were found. Each line needs an assistant turn.");
        }
        else
        {
            if (trainRecords == 0)
                warnings.Add("Every usable conversation landed in the validation or exam split.");
            if (job.ValidationPercent > 0 && validationRecords == 0)
                warnings.Add("The validation split is empty; the run would have no val curve.");
            if (job.TestPercent > 0 && examRecords == 0)
                warnings.Add("The exam split is empty; the promotion exam could not run.");
            if (conversation is null)
                warnings.Add("Tokenizer unavailable from the base model; token counts are character counts.");
            if (truncatedRecords > 0)
                warnings.Add($"{truncatedRecords:N0} conversation(s) exceed the {seqLen}-token SFT sequence and will be truncated.");
            if (encodeFailures > 0)
                warnings.Add($"{encodeFailures:N0} conversation(s) could not be tokenized and were not counted.");
        }

        return new SftDatasetPreview
        {
            SourceDescription = set.Records.Description,
            Files = set.JsonlSources.Sum(source => source.FileCount),
            Records = records,
            Skipped = set.JsonlSources.Sum(source => source.SkippedRecords),
            LimitedScan = limitHit,
            Layouts = layouts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            TrainRecords = trainRecords,
            ValidationRecords = validationRecords,
            ExamRecords = examRecords,
            Tokenized = conversation is not null,
            AverageTokens = records == 0 ? 0 : (double)totalTokens / records,
            AverageAssistantTokens = records == 0 ? 0 : (double)totalAssistantTokens / records,
            MaxTokens = maxTokens,
            TruncatedRecords = truncatedRecords,
            SeqLen = seqLen,
            Formatter = formatter.GetType().Name,
            Warnings = warnings,
            Examples = examples,
        };
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n… (truncated)";
}
