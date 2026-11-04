using System.Text;
using System.Text.Json;
using SharpMind.Core;
using SharpMind.Core.Plugins;
using SharpMind.Core.Quantization;
using SharpMind.Core.Training;
using SharpMind.Data;
using SharpMind.Data.Batching;
using SharpMind.Data.Metadata;
using SharpMind.Data.Pipeline;
using SharpMind.Data.Records;
using SharpMind.Data.Sources;
using SharpMind.Inference.Chat.PromptFormatters;
using SharpMind.Model;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Tokenization;
using SharpMind.Tokenization.Serialisation;
using SharpMind.Training;
using SharpMind.Training.LoRA;
using SharpMind.Training.Loss;
using SharpMind.Training.Optimizers;
using SharpMind.Training.Schedulers;
using SharpMind.Training.Sft;

namespace SharpMind.Studio.Core.Training;

/// <summary>Result of a full training run.</summary>
public sealed class TrainingRunResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ExportPath { get; init; }

    /// <summary>Saved adapter file, when the run asked for one (merged or adapter-only).</summary>
    public string? AdapterPath { get; init; }

    /// <summary>True when the content-addressed recipe cache supplied the export.</summary>
    public bool Cached { get; init; }

    /// <summary>
    /// Which phase was reused: <c>train</c> for an identical run, <c>package</c>
    /// when a cached model was copied to a new export path, or <c>checkpoint</c>
    /// when a cached checkpoint continued (or supplied) the trained weights.
    /// </summary>
    public string? CachedPhase { get; init; }

    /// <summary>Training recipe hash, when caching was active for this run.</summary>
    public string? RecipeHash { get; init; }
    public int FinalStep { get; init; }

    /// <summary>Lowest validation loss seen during the run, when validation was enabled.</summary>
    public double? BestValLoss { get; init; }

    /// <summary>Step at which <see cref="BestValLoss"/> was recorded.</summary>
    public int? BestValStep { get; init; }

    /// <summary>
    /// True when an incremental run found no new or changed files to train on;
    /// the run never entered the training loop and produced no export.
    /// </summary>
    public bool NothingToTrain { get; init; }

    /// <summary>
    /// Set when an accelerator was explicitly requested (its sanity-checked name in
    /// <see cref="AcceleratorRequested"/>) but its engine factory declined â€” e.g. the ILGPU plugin was
    /// chosen but no CUDA/OpenCL device is present. The CUI turns this into a consent picker (CPU +
    /// every other capable plugin, from <see cref="AcceleratorReason"/> as the why) rather than failing
    /// the run outright. Never set alongside <see cref="Success"/>.
    /// </summary>
    public string? AcceleratorRequested { get; init; }
    public string? AcceleratorReason { get; init; }
}

/// <summary>
/// Runs one training job end-to-end: rebuilds the source + stage chain from
/// <see cref="TrainingJobSettings"/>, trains (or loads) the BPE tokenizer,
/// constructs the model, runs <see cref="TrainLoop"/> with checkpoints and QAT,
/// exports each retained checkpoint to a testable .smm, and finally exports the
/// trained model. Must be called from a background task; the caller marshals
/// progress callbacks onto the UI thread.
/// </summary>
public static class TrainingRunner
{
    public const int Seed = 1234;

    /// <summary>
    /// Runs <paramref name="job"/> off the UI thread. <paramref name="status"/>
    /// receives free-form log lines; <paramref name="progress"/> receives a 0..1
    /// overall completion figure; <paramref name="onStep"/> receives each logged
    /// optimizer step. Returns the export path on success.
    /// </summary>
    public static async Task<TrainingRunResult> RunAsync(
        TrainingJobSettings job,
        string pluginsFolder,
        IProgress<string> status,
        IProgress<float> progress,
        Action<TrainStepResult>? onStep = null,
        Action<int>? onResume = null,
        CancellationToken cancellationToken = default,
        string? recipeFolder = null)
    {
        void Log(string line) => status.Report(line);

        try
        {
            Log("Resolving data pipelineâ€¦");
            var components = ComponentRegistry.ScanFolder(pluginsFolder, out var pluginWarnings);
            foreach (var w in pluginWarnings) Log($"Plugin: {w}");

            if (job.Sources.Count == 0)
                throw new InvalidOperationException("No data sources configured for this job.");

            // Incremental mode decides which sources (and which files within a
            // file-based source) feed this run. The plan carries, per source, the
            // rebuilt IDataSource restricted to its new+changed files; unchanged
            // sources contribute no node at all. Deltas are computed by diffing
            // the current per-file hashes against the previous run's map.
            // "Force resume latest" is applied later in the resume-resolution.
            if (job.IncrementalMode && job.SourceFileHashes.Count == 0)
            {
                var persisted = IncrementalStore.Load(job);
                if (persisted.Count > 0)
                {
                    job.SourceFileHashes = persisted;
                    Log($"Incremental: loaded {persisted.Count} source hash map(s) recorded by a previous run.");
                }
            }

            var plan = IncrementalPlanner.Build(job, components, Log);
            if (plan.NothingToTrain)
            {
                Log($"Incremental: no new or changed files since the last run â€” nothing to train.");
                return new TrainingRunResult { Success = true, NothingToTrain = true };
            }

            var sources = plan.Sources;
            var sourceNodes = new List<PipelineNode>();
            for (int i = 0; i < job.Sources.Count; i++)
            {
                if (plan.SkipSource[i]) continue;
                var node = PipelineNode.From(sources[i]);
                foreach (var stage in job.Sources[i].Stages)
                {
                    var descriptor = ComponentRegistry.Find(stage.TypeName, components)
                        ?? throw new InvalidOperationException($"Unknown stage '{stage.TypeName}'.");
                    node = node.Pipe((ICleaningStage)ComponentRegistry.Build<ICleaningStage>(descriptor, stage.Args));
                }
                sourceNodes.Add(node);
            }
            PipelineNode pipeline = sourceNodes.Count == 1
                ? sourceNodes[0]
                : CleaningPipeline.Merge(sourceNodes);
            foreach (var g in job.GlobalStages)
            {
                var descriptor = ComponentRegistry.Find(g.TypeName, components)
                    ?? throw new InvalidOperationException($"Unknown stage '{g.TypeName}'.");
                pipeline = pipeline.Pipe((ICleaningStage)ComponentRegistry.Build<ICleaningStage>(descriptor, g.Args));
            }

            var compositeSource = sources.Count == 1 ? sources[0] : new CompositeSource(sources);
            Log(job.IsSft
                ? $"Sources: {sources.Count} chat JSONL source(s): "
                  + string.Join(", ", job.Sources.Select(source =>
                      source.Component.Args.TryGetValue("path", out string? sourcePath) ? sourcePath : "?"))
                : $"Sources: {string.Join(", ", sources.Select(s => s.Description))}");

            // Source fingerprints: record what this job trains on, warn when it
            // has changed since the last run (the tokenizer cache is reused on
            // path alone, so a stale corpus would silently poison the mapping),
            // and remember them for stamping the exported model's metadata.
            var sourceHashes = SourceHasher.Compute(job.Sources);
            var changed = job.SourceHashes.Count > 0
                && sourceHashes.Count > 0
                && !job.SourceHashes.OrderBy(kv => kv.Key).SequenceEqual(sourceHashes.OrderBy(kv => kv.Key));
            // Compact identity for the corpus this run trains on; stamped into every
            // checkpoint so a later resume can tell whether its batch cursor still
            // indexes the same stream (see TrainConfig.DataFingerprint).
            string? dataFingerprint = SourceHasher.Combined(sourceHashes);
            foreach (var kv in sourceHashes.Where(kv => kv.Value is not null))
                Log($"Source hash {kv.Key}: {kv.Value![..Math.Min(12, kv.Value == null ? 0 : kv.Value.Length)]}â€¦");
            if (changed)
                Log("WARNING: training sources changed since the last run â€” the cached tokenizer may be stale "
                    + "for this corpus. The data cursor is reset, so this run starts at the head of the new corpus.");
            if (sourceHashes.Count > 0)
                job.SourceHashes = sourceHashes;

            progress.Report(0.05f);

            // Pre-flight QAT validation â€” fail before the tokenizer and model are
            // built if the chosen target can't fit the planned dimensions, so the
            // user sees one clear message instead of a deep run-time exception. The
            // model vocab is padded to the target's block alignment later (see
            // TrainingModelOptions.ResolveModelConfig), so only HiddenDim can block.
            if (!job.IsFineTune && TrainingModelOptions.ValidateQat(job.QuantAwareTraining, job.HiddenDim) is { } qatProblem)
            {
                Log(qatProblem);
                return new TrainingRunResult { Success = false, Error = qatProblem };
            }

            // 1. Tokenizer â€” BPE trained on the corpus and cached on disk, or a
            // character-level tokenizer derived from the corpus characters. The
            // vocab must always reflect the FULL corpus, never just the deltas of
            // an incremental run, so when the cache is missing we train it over the
            // complete configured sources (a no-op for fresh jobs, where the
            // deltas ARE the whole corpus anyway).
            // The exam and validation slices must not consume the whole corpus.
            if (Math.Max(0, job.ValidationPercent) + Math.Max(0, job.TestPercent) >= 100)
                return new TrainingRunResult
                {
                    Success = false,
                    Error = "Validation and held-out exam splits together must stay below 100%.",
                };

            // A fine-tune needs somewhere for the trained adapters to go: a merged
            // model, an adapter file, or both. A rehearsal produces neither.
            if (!job.Rehearsal && job.IsFineTune && !job.MergeOnExport && !job.SaveAdapterFile)
                return new TrainingRunResult
                {
                    Success = false,
                    Error = "A fine-tune must export a merged .smm, an adapter .smlora, or both.",
                };

            // SFT is a fine-tune mode: the base supplies the tokenizer, chat
            // template and frozen weights the assistant-only loss trains against.
            if (job.SftMode && !job.IsFineTune)
                return new TrainingRunResult
                {
                    Success = false,
                    Error = "Instruction tuning (SFT) needs a fine-tune base. Pick a packaged .smm first.",
                };

            if (!job.Rehearsal && job.IsFineTune && job.MergeOnExport && job.SaveAdapterFile
                && string.Equals(
                    Path.GetFullPath(job.AdapterFilePath),
                    Path.GetFullPath(job.ExportFilePath),
                    StringComparison.OrdinalIgnoreCase))
                return new TrainingRunResult
                {
                    Success = false,
                    Error = "The adapter and merged model resolve to the same file. Pick different destinations.",
                };

            Tokenizer tokenizer = null!;
            ModelConfig modelConfig;
            bool grewTokenizer = false;
            Transformer? fineTuneModel = null;
            string? baseChatTemplate = null;

            // Content-addressed recipe check before loading a base or tokenizer:
            // identical data + settings return the existing packaged model. The
            // package phase is separate: if only the destination moved, copy the
            // cached .smm instead of training again.
            string recipeDataHash = TrainingRecipeHash.Data(job, dataFingerprint ?? "none");
            string recipeTokenizerHash = TrainingRecipeHash.Tokenizer(job, recipeDataHash);
            string recipeTrainBaseHash = !job.Rehearsal && !job.IncrementalMode && !job.IsFineTune
                ? TrainingRecipeHash.TrainBase(job, recipeDataHash, recipeTokenizerHash)
                : "";
            string? recipeTrainHash = null;
            if (!job.IncrementalMode && !job.Rehearsal && job.MergeOnExport && !job.SaveAdapterFile)
            {
                recipeTrainHash = TrainingRecipeHash.Train(job, recipeDataHash, recipeTokenizerHash);
                if (TrainingRecipeStore.TryGet("train", recipeTrainHash, out TrainingRecipeArtifact cached, recipeFolder))
                {
                    string requestedExport = Path.GetFullPath(job.ExportFilePath);
                    string cachedExport = Path.GetFullPath(cached.ArtifactPath);
                    if (!string.Equals(requestedExport, cachedExport, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(requestedExport)!);
                        File.Copy(cachedExport, requestedExport, overwrite: true);
                        Log($"Recipe: cache hit {TrainingRecipeHash.Short(recipeTrainHash)} - "
                            + $"package phase copied {Path.GetFileName(cachedExport)} to {requestedExport}.");
                        progress.Report(1f);
                        return new TrainingRunResult
                        {
                            Success = true,
                            ExportPath = requestedExport,
                            FinalStep = job.TotalSteps,
                            Cached = true,
                            CachedPhase = "package",
                            RecipeHash = recipeTrainHash,
                        };
                    }

                    Log($"Recipe: cache hit {TrainingRecipeHash.Short(recipeTrainHash)} - "
                        + $"reusing {Path.GetFileName(cachedExport)}.");
                    progress.Report(1f);
                    return new TrainingRunResult
                    {
                        Success = true,
                        ExportPath = cachedExport,
                        FinalStep = job.TotalSteps,
                        Cached = true,
                        CachedPhase = "train",
                        RecipeHash = recipeTrainHash,
                    };
                }
                Log($"Recipe: {TrainingRecipeHash.Short(recipeTrainHash)} - no cached model.");
            }

            bool tokenizerFromCache = false;
            if (!job.IsFineTune && !job.UsesCharacterTokenizer && !job.IncrementalMode && !job.Rehearsal
                && TrainingRecipeStore.TryGet("tokenizer", recipeTokenizerHash, out TrainingRecipeArtifact tokenizerArtifact, recipeFolder))
            {
                tokenizer = TokenizationPipeline.Load(tokenizerArtifact.ArtifactPath);
                tokenizerFromCache = true;
                Log($"Recipe: reused tokenizer {TrainingRecipeHash.Short(recipeTokenizerHash)}.");
            }

            if (job.IsFineTune)
            {
                string basePath = job.BaseModelPath!;
                if (!File.Exists(basePath))
                    throw new InvalidOperationException($"Base model not found: {basePath}");
                if (job.IncrementalMode)
                    throw new InvalidOperationException(
                        "A LoRA fine-tune cannot use incremental mode; point it at the new corpus directly.");

                Log($"Fine-tune: loading base {Path.GetFileName(basePath)}...");
                int lastLoadPct = -1;
                fineTuneModel = SmmTrainingPipeline.LoadForTraining(
                    basePath,
                    new TrainingLoadOptions
                    {
                        Progress = new Progress<float>(p =>
                        {
                            int pct = (int)(p * 10);
                            if (pct == lastLoadPct) return;
                            lastLoadPct = pct;
                            Log($"Fine-tune: base weights {pct * 10}%");
                        }),
                    },
                    out tokenizer,
                    out modelConfig);
                baseChatTemplate = SmmTrainingPipeline.LoadChatTemplate(basePath);
                Log($"Fine-tune: base vocab={tokenizer.VocabSize}, H={modelConfig.HiddenDim}, "
                    + $"L={modelConfig.NumLayers}, context={modelConfig.MaxSeqLen}. "
                    + "Base weights are frozen; only the adapters train.");
                job.TrainedTokenizerVocab = tokenizer.VocabSize;

                if (TrainingModelOptions.ValidateQat(job.QuantAwareTraining, modelConfig.HiddenDim) is { } fineTuneQatProblem)
                {
                    Log(fineTuneQatProblem);
                    return new TrainingRunResult { Success = false, Error = fineTuneQatProblem };
                }
            }
            else
            {
                if (!tokenizerFromCache)
                {
                if (job.UsesCharacterTokenizer)
                {
                    tokenizer = await TokenizationPipeline.TrainCharacterAsync(
                        job.IncrementalMode ? FullCorpusComposite(job, components) : compositeSource);
                }
                else
                {
                    string tokenizerPath = job.TokenizerFilePath;
                    bool existed = File.Exists(tokenizerPath);
                    tokenizer = existed
                        ? TokenizationPipeline.Load(tokenizerPath)
                        : await TokenizationPipeline.TrainAndSaveAsync(
                            job.IncrementalMode ? FullCorpusComposite(job, components) : compositeSource,
                            tokenizerPath, job.TokenizerVocabSize);

                    // A larger Vocab target than the cached tokenizer means growth is
                    // required, so do it automatically rather than making the user press
                    // a button. Grow is append-only: old token IDs are preserved, only
                    // new merges are appended. The widened embedding then forces a warm
                    // restart (handled during resume resolution below).
                    if (existed && job.TokenizerVocabSize > tokenizer.VocabSize)
                    {
                        int priorVocab = tokenizer.VocabSize;
                        Log($"Tokenizer: target {job.TokenizerVocabSize} > cached {priorVocab} â€” growing append-only.");
                        int grown = await GrowTokenizerAsync(job, pluginsFolder, job.TokenizerVocabSize, Log, cancellationToken);
                        if (grown > priorVocab)
                        {
                            tokenizer = TokenizationPipeline.Load(tokenizerPath);
                            grewTokenizer = true;
                            Log($"Tokenizer: grew {priorVocab} â†’ {tokenizer.VocabSize} tokens (old IDs unchanged).");
                        }
                        else
                        {
                            Log($"Tokenizer: could not grow beyond {priorVocab} (the corpus offers no further merges).");
                        }
                    }
                }
                }

                if (!tokenizerFromCache && !job.UsesCharacterTokenizer)
                {
                    string tokenizerPath = job.TokenizerFilePath;
                    if (File.Exists(tokenizerPath))
                    {
                        TrainingRecipeStore.Save(new TrainingRecipeArtifact(
                            "tokenizer", recipeTokenizerHash, tokenizerPath, job.Name, DateTime.UtcNow), recipeFolder);
                    }
                }

                Log($"Tokenizer: vocab={tokenizer.VocabSize} ({(job.UsesCharacterTokenizer ? "char" : "bpe")})");
                job.TrainedTokenizerVocab = tokenizer.VocabSize;

                // 2. Model config.
                modelConfig = TrainingModelOptions.ResolveModelConfig(job, tokenizer.VocabSize);
                if (modelConfig.VocabSize != tokenizer.VocabSize)
                    Log($"QAT: padded model vocab {tokenizer.VocabSize} â†’ {modelConfig.VocabSize} "
                        + $"for {TrainingModelOptions.ParseQat(job.QuantAwareTraining)} block alignment.");
                Log($"Model: H={job.HiddenDim} L={job.NumLayers} heads={job.NumHeads} ffn={job.FfnDim}");
            }

            using var fineTuneOwner = fineTuneModel;
            progress.Report(0.1f);

            // The data phase is cached separately from the tokenizer/train phases:
            // the cleaned corpus only changes when the sources, stages or splits
            // change. A hit skips re-reading and re-cleaning the source files.
            if (!job.IsSft)
            {
                pipeline = await CacheOrReuseDataAsync(
                    job, pipeline, recipeDataHash, recipeFolder, Log, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                Log("SFT: reading conversation records directly; the packed-text data cache is skipped.");
            }

            // 3. Data pipeline â€” clean â†’ tokenise â†’ TrainingBatches. Random-window
            // batching samples contiguous windows from one flat corpus stream
            // (nanoGPT-style); PackingBatcher is the default document packer.
            // Two deterministic, content-hashed slices can be removed:
            //   * exam (TestPercent) - never trained on or used for model choice;
            //     scored only after a run, for the promotion report.
            //   * validation (ValidationPercent) - scored during the run.
            // Both use the same seed and disjoint bucket ranges, so a document is
            // never in two slices, and every point on a curve scores a fixed set.
            // The tokenizer still trains over the full corpus: it learns the token
            // inventory, not the loss being measured.
            List<TrainingBatch> validationBatches = [];
            ITrainingBatchSource? sftLoader = null;
            DataLoader? stringLoader = null;
            string? loaderDescription = null;
            double testFraction = Math.Clamp(job.TestPercent, 0, 90) / 100.0;
            double valFraction = Math.Clamp(job.ValidationPercent, 0, 90) / 100.0;
            if (job.IsSft)
            {
                int sftSeqLen = Math.Max(1, job.SeqLen);
                if (modelConfig.MaxSeqLen > 0)
                    sftSeqLen = Math.Min(sftSeqLen, modelConfig.MaxSeqLen);

                IRecordSource records = BuildSftRecordSource(job, components);
                IRecordSource trainRecords = records;
                int examThreshold = (int)Math.Round(testFraction * DeterministicSplit.Buckets);
                int valThreshold = (int)Math.Round((testFraction + valFraction) * DeterministicSplit.Buckets);

                if (testFraction > 0)
                {
                    Log($"Exam: {job.TestPercent:0.#}% of conversations held out for the promotion exam "
                        + "(excluded from training and validation).");
                }

                var conversation = new ConversationTokenizer(
                    ResolveSftFormatter(job.SftFormatter, baseChatTemplate), tokenizer);

                if (valFraction > 0)
                {
                    IRecordSource valRecords = new WhereRecordSource(
                        records,
                        record =>
                        {
                            int bucket = DeterministicSplit.Bucket(record.Text, Seed);
                            return bucket >= examThreshold && bucket < valThreshold;
                        },
                        "val");

                    var valBatcher = new SftBatcher(
                        batchSize: Math.Max(1, job.BatchSize),
                        maxSeqLen: sftSeqLen,
                        padTokenId: tokenizer.PadId);
                    var valLoader = new RecordDataLoader(valRecords, valBatcher, conversation.Encode);
                    try
                    {
                        await foreach (TrainingBatch held in valLoader.LoadAsync(cancellationToken: cancellationToken))
                        {
                            validationBatches.Add(held);
                            if (validationBatches.Count >= 4) break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log($"Validation: could not read the held-out split ({ex.Message}) - "
                            + "training will run without a val curve.");
                        foreach (TrainingBatch held in validationBatches) held.Dispose();
                        validationBatches.Clear();
                    }

                    if (validationBatches.Count == 0)
                    {
                        Log($"Validation: {job.ValidationPercent:0.#}% requested but the split produced no held-out "
                            + "conversations - training will run without a val curve.");
                    }
                    else
                    {
                        Log($"Validation: {job.ValidationPercent:0.#}% of conversations held out, "
                            + $"{validationBatches.Count} batch(es) fixed for every evaluation.");
                    }
                }

                if (examThreshold > 0 || valThreshold > 0)
                {
                    trainRecords = new WhereRecordSource(
                        records,
                        record => DeterministicSplit.Bucket(record.Text, Seed) >= valThreshold,
                        "train");
                }

                // Fail early with a clear message instead of running zero steps
                // and exporting an untrained adapter when the JSONL has no usable
                // assistant turns.
                int probeAll = 0;
                await foreach (DataRecord _ in records.ReadRecordsAsync(cancellationToken))
                {
                    probeAll++;
                    break;
                }
                if (probeAll == 0)
                {
                    return new TrainingRunResult
                    {
                        Success = false,
                        Error = "The SFT corpus produced no conversation records. Each JSONL line needs an "
                            + "assistant turn (messages, conversations, prompt/response or instruction/output).",
                    };
                }

                int probe = 0;
                await foreach (DataRecord _ in trainRecords.ReadRecordsAsync(cancellationToken))
                {
                    probe++;
                    break;
                }
                if (probe == 0)
                {
                    return new TrainingRunResult
                    {
                        Success = false,
                        Error = "Every usable conversation landed in the validation or exam split. "
                            + "Lower the held-out percentages or add more chat records.",
                    };
                }

                var sftBatcher = new SftBatcher(
                    batchSize: Math.Max(1, job.BatchSize),
                    maxSeqLen: sftSeqLen,
                    padTokenId: tokenizer.PadId);
                sftLoader = new RecordDataLoader(
                    trainRecords,
                    sftBatcher,
                    conversation.Encode,
                    maxBatches: job.TotalSteps * job.GradAccumSteps,
                    skipProgress: (done, total) =>
                        Log($"Data cursor: replayed {done:N0}/{total:N0} batches..."));
                loaderDescription = $"{trainRecords.Description} -> {sftBatcher.GetType().Name} "
                    + $"(batch {job.BatchSize} x {sftSeqLen}, assistant-only loss)";
                Log($"SFT: assistant-only loss over {sftSeqLen}-token conversations.");
            }
            else
            {
            // RandomWindowBatcher concatenates documents; PackingBatcher inserts
            // EOS itself. Give the concatenating batcher the tokenizer's boundary
            // so JSONL/text sources never need a literal <|endoftext|> marker.
            bool appendDocumentBoundary = job.UsesRandomWindowBatching;
            Func<string, int[]> tokenise = text =>
                TrainingTokenization.EncodeDocument(tokenizer, text, appendDocumentBoundary);

            if (testFraction > 0)
            {
                int examThreshold = (int)Math.Round(testFraction * DeterministicSplit.Buckets);
                var (_, trainVal) = pipeline.Branch(
                    "exam", doc => DeterministicSplit.Bucket(doc, Seed) < examThreshold);
                pipeline = trainVal;
                Log($"Exam: {job.TestPercent:0.#}% of documents held out for the promotion exam "
                    + "(excluded from training and validation).");
            }

            if (valFraction > 0)
            {
                // The val range starts above the exam's, so the two slices stay
                // disjoint even though both are keyed by the same content hash.
                int valThreshold = (int)Math.Round((testFraction + valFraction) * DeterministicSplit.Buckets);
                var (valNode, trainNode) = pipeline.Branch(
                    "val", doc => DeterministicSplit.Bucket(doc, Seed) < valThreshold);
                pipeline = trainNode;

                IBatchStrategy valBatcher = job.UsesRandomWindowBatching
                    ? new RandomWindowBatcher(batchSize: job.BatchSize, seqLen: job.SeqLen, seed: Seed + 1)
                    : new PackingBatcher(
                        batchSize: job.BatchSize,
                        maxSeqLen: job.SeqLen,
                        eosTokenId: tokenizer.EosId,
                        padTokenId: tokenizer.PadId);
                var valLoader = new DataLoader(valNode, tokenise, valBatcher, prefetchBuffer: 1);
                try
                {
                    await foreach (TrainingBatch held in valLoader.LoadAsync(cancellationToken: cancellationToken))
                    {
                        validationBatches.Add(held);
                        if (validationBatches.Count >= 4) break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A source that cannot be read a second time (a remote stream,
                    // for example) must not sink the run: train without a val curve.
                    Log($"Validation: could not read the held-out split ({ex.Message}) - "
                        + "training will run without a val curve.");
                    foreach (TrainingBatch held in validationBatches) held.Dispose();
                    validationBatches.Clear();
                }

                if (validationBatches.Count == 0)
                {
                    Log($"Validation: {job.ValidationPercent:0.#}% requested but the split produced no held-out "
                        + "documents - training will run without a val curve.");
                }
                else
                {
                    Log($"Validation: {job.ValidationPercent:0.#}% of documents held out, "
                        + $"{validationBatches.Count} batch(es) fixed for every evaluation.");
                }
            }

            IBatchStrategy batcher = job.UsesRandomWindowBatching
                ? new RandomWindowBatcher(batchSize: job.BatchSize, seqLen: job.SeqLen, seed: Seed)
                : new PackingBatcher(
                    batchSize: job.BatchSize,
                    maxSeqLen: job.SeqLen,
                    eosTokenId: tokenizer.EosId,
                    padTokenId: tokenizer.PadId);
            var dataLoader = new DataLoader(pipeline, tokenise, batcher, prefetchBuffer: 4,
                maxBatches: job.TotalSteps * job.GradAccumSteps,
                skipProgress: (done, total) =>
                    Log($"Data cursor: replayed {done:N0}/{total:N0} batchesâ€¦"));
            stringLoader = dataLoader;
            loaderDescription = dataLoader.Describe();
            }

            ITrainingBatchSource loader = sftLoader ?? stringLoader!;
            Log($"Data pipeline: {loaderDescription}");

            // 4. Model â€” empty float weights, randomised unless resuming.
            var sharpConfig = job.IsFineTune
                ? modelConfig.ForModel(HardwareTier.Auto)
                : TrainingModelOptions.ResolveSharpConfig(job);

            // Resume resolution: an explicit ResumeFrom wins; otherwise if any
            // checkpoint exists under the job's derived checkpoint folder we
            // auto-resume the latest one, so an interrupted run can always be
            // picked back up without re-entering the path manually. StartFresh
            // forces a from-scratch run regardless of what exists on disk.
            // Incremental runs ALWAYS continue from the newest checkpoint â€”
            // "force resume latest" â€” ignoring StartFresh/ResumeFrom, because
            // the delta pipeline only delivers unseen data: building on top of
            // the latest weights is the entire point.
            string checkpointDir = job.Rehearsal
                ? Path.Combine(Path.GetTempPath(), "sharpmind-rehearsal-" + Guid.NewGuid().ToString("N"))
                : job.CheckpointDir;

            // A cached checkpoint carries the train phase. Prefer a job-local
            // checkpoint when one exists; otherwise fall through to the recipe
            // checkpoint so a different job name or export folder can still
            // continue the same weights.
            string? recipeCheckpointDir = null;
            if (!job.IsFineTune
                && !job.IncrementalMode
                && !job.Rehearsal
                && !job.StartFresh
                && string.IsNullOrWhiteSpace(job.ResumeFrom)
                && string.IsNullOrWhiteSpace(job.WarmRestartFrom)
                && TrainingRecipeStore.TryGet(
                    "checkpoint", recipeTrainBaseHash, out TrainingRecipeArtifact recipeCheckpoint, recipeFolder)
                && Directory.Exists(recipeCheckpoint.ArtifactPath))
            {
                recipeCheckpointDir = recipeCheckpoint.ArtifactPath;
            }

            string? resumeDir;
            string? warmRestartDir = null;
            int? warmRestartPriorVocab = null;
            int warmRestartStartStep = 0;
            string? adapterResumeFile = null;
            int adapterResumeStep = 0;
            if (job.IsFineTune)
            {
                resumeDir = null;
                if (job.StartFresh)
                {
                    Log("Fine-tune: starting from the base model (fresh run).");
                }
                else
                {
                    adapterResumeFile = ResolveAdapterResume(job, checkpointDir, out adapterResumeStep);
                    if (adapterResumeFile is null)
                    {
                        Log("Fine-tune: no adapter checkpoint found - starting from the base model.");
                    }
                    else
                    {
                        if (adapterResumeStep > 0 && job.TotalSteps <= adapterResumeStep)
                            throw new InvalidOperationException(
                                $"Total steps ({job.TotalSteps}) must be greater than the resumed adapter step "
                                + $"({adapterResumeStep}). Raise the step count to continue training.");
                        warmRestartStartStep = adapterResumeStep;
                        Log(adapterResumeStep > 0
                            ? $"Fine-tune: resuming adapters from step {adapterResumeStep} ({Path.GetFileName(adapterResumeFile)}). "
                              + "Weights continue; the optimizer and data cursor restart."
                            : $"Fine-tune: resuming adapters from {Path.GetFileName(adapterResumeFile)} (step unknown; fresh schedule).");
                    }
                }
            }
            else if (job.IncrementalMode)
            {
                resumeDir = Checkpoint.FindLatest(checkpointDir);
                if (resumeDir is null)
                    Log("Incremental: no prior checkpoint found â€” starting from random weights on the new/changed files only.");
            }
            else if (!job.StartFresh && !string.IsNullOrWhiteSpace(job.WarmRestartFrom)
                     && Directory.Exists(job.WarmRestartFrom))
            {
                // Warm restart: reuse the weights as an initialisation under a
                // fresh schedule. Never a strict resume â€” the embedding may have
                // been widened by an append-only tokenizer grow, and the optimizer
                // state is intentionally dropped.
                resumeDir = null;
                warmRestartDir = job.WarmRestartFrom;
                Log($"Warm restart from {Path.GetFileName(warmRestartDir)} â€” reusing weights, fresh optimizer (step 0).");
            }
            else
            {
                resumeDir = job.StartFresh ? null
                    : !string.IsNullOrWhiteSpace(job.ResumeFrom) ? job.ResumeFrom
                    : Checkpoint.FindLatest(checkpointDir);
            }

            bool recipeCheckpointReused = false;
            if (resumeDir is null && warmRestartDir is null && recipeCheckpointDir is not null)
            {
                resumeDir = recipeCheckpointDir;
                recipeCheckpointReused = true;
                int reusedStep = Checkpoint.ReadMeta(resumeDir).Step;
                Log($"Recipe: reusing checkpoint at step {reusedStep} for "
                    + $"{TrainingRecipeHash.Short(recipeTrainBaseHash)}; requested {job.TotalSteps} steps.");
            }

            // A grown tokenizer widens the embedding, so a strict resume cannot load
            // a checkpoint built at the old size. Reuse those weights as a warm
            // restart instead (fresh optimizer, step 0). Deliberately not persisted
            // to job.WarmRestartFrom: this run writes new checkpoints at the grown
            // vocab, and later runs should resume those normally.
            if (grewTokenizer && resumeDir is not null && Directory.Exists(resumeDir))
            {
                warmRestartDir = resumeDir;
                resumeDir = null;
                job.ResumeFrom = null; // this run writes new-vocab checkpoints; later runs resume those
                Log($"Warm restart from {Path.GetFileName(warmRestartDir)} â€” embedding widened, fresh optimizer (step 0).");
            }

            // Safety net: a checkpoint built for a different vocabulary cannot be
            // strictly resumed (the embedding shape differs). This happens when a
            // tokenizer grew on an earlier run that failed before writing a
            // checkpoint. Reuse those weights as a warm restart instead of failing.
            if (resumeDir is not null && Directory.Exists(resumeDir))
            {
                var meta = Checkpoint.ReadMeta(resumeDir);
                int? recordedVocab = null;
                if (meta.Extra.TryGetValue("vocabSize", out var recordedText)
                    && int.TryParse(recordedText, System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out int parsedVocab))
                {
                    recordedVocab = parsedVocab;
                }
                else
                {
                    // Older checkpoints predate the vocabSize metadata. Recover
                    // the embedding width from model.bin so they warm-restart too.
                    long? elements = Checkpoint.ReadTensorElementCount(resumeDir, "EmbeddingTable.weight");
                    if (elements is > 0 && elements.Value % modelConfig.HiddenDim == 0)
                        recordedVocab = (int)(elements.Value / modelConfig.HiddenDim);
                }

                if (recordedVocab is int priorVocab && priorVocab != modelConfig.VocabSize)
                {
                    warmRestartDir = resumeDir;
                    warmRestartPriorVocab = priorVocab;
                    resumeDir = null;
                    job.ResumeFrom = null; // see the grown-tokenizer case above
                    Log($"Checkpoint vocab {priorVocab} â†’ model vocab {modelConfig.VocabSize} "
                        + "â€” warm restart instead of strict resume.");
                }
            }

            int resumeStep = 0;
            double? resumeValLoss = null;
            if (resumeDir is not null)
            {
                // A recipe checkpoint is a cache pointer, not a job setting; do not
                // persist it into the .smmt, where a later cleanup could break it.
                if (!recipeCheckpointReused) job.ResumeFrom = resumeDir;
                var resumeMeta = Checkpoint.ReadMeta(resumeDir);
                resumeStep = resumeMeta.Step;
                if (resumeMeta.Extra.TryGetValue("valLoss", out string? valText)
                    && double.TryParse(valText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsedValLoss))
                {
                    resumeValLoss = parsedValLoss;
                }
                Log($"Resuming from checkpoint {Path.GetFileName(resumeDir)} (step {resumeMeta.Step})");
                onResume?.Invoke(resumeMeta.Step);
            }
            else
            {
                onResume?.Invoke(warmRestartStartStep);
            }

            bool reuseCompletedCheckpoint = recipeCheckpointReused
                && resumeDir is not null
                && resumeStep >= job.TotalSteps;

            using TransformerWeights? pretrainWeights = job.IsFineTune
                ? null
                : ModelFactory.CreateForTraining(modelConfig, sharpConfig);
            TransformerWeights weights;
            Transformer model;
            IReadOnlyList<Parameter> parameters;
            LoRAModel? lora = null;

            if (job.IsFineTune)
            {
                model = fineTuneModel!;
                weights = model.Weights;
                if (adapterResumeFile is not null)
                {
                    // The adapter file carries rank, alpha, targets and a base
                    // fingerprint; LoadAdapter rebuilds the LoRA model from those
                    // and refuses a base it was not trained against.
                    lora = LoRAModel.LoadAdapter(model, adapterResumeFile);
                    Log($"Fine-tune: loaded {lora.AdapterCount} adapters (rank {lora.Config.Rank}, "
                        + $"alpha {lora.Config.Alpha}) from {Path.GetFileName(adapterResumeFile)}; "
                        + "the adapter file defines the targets.");
                }
                else
                {
                    int rank = Math.Max(1, job.LoRARank);
                    float alpha = job.LoRAAlpha > 0 ? job.LoRAAlpha : rank * 2f;
                    lora = new LoRAModel(model, new LoRAConfig
                    {
                        Rank = rank,
                        Alpha = alpha,
                        TargetModules = job.LoRATargetModules.Count > 0 ? [.. job.LoRATargetModules] : null,
                    }, seed: Seed);
                    Log($"Fine-tune: {lora.AdapterCount} adapters (rank {rank}, alpha {alpha}).");
                }

                parameters = lora.LoRAParameters();
                Log($"{parameters.Sum(p => p.Data.ElementCount):N0} trainable values "
                    + $"({lora.TrainableRatio():P2} of the base).");
            }
            else
            {
                weights = pretrainWeights!;
                if (resumeDir is null)
                {
                    WeightInitializer.InitializeRandomly(weights, Seed);
                    Log("Weights initialised.");
                }
                model = ModelFactory.CreateTrainingTransformer(weights, sharpConfig);
                if (resumeDir is null && model.Config.NumExperts > 0)
                {
                    WeightInitializer.InitializeModelMoE(model, Seed + 1013);
                    Log("MoE router/expert weights initialised.");
                }
                parameters = model.Parameters().ToList();
            }

            // The pretrain transformer is ours to dispose; the fine-tune model is
            // owned by fineTuneOwner above.
            using var pretrainModel = job.IsFineTune ? null : model;
            using var loraOwner = lora;

            var ops = TrainingOpsFactory.Create(sharpConfig);
            using IOptimizer optimizer = TrainingModelOptions.UsesSgd(job)
                ? new SGD(parameters, lr: job.LearningRate, momentum: job.SgdMomentum, weightDecay: job.WeightDecay)
                : new AdamW(parameters, ops, lr: job.LearningRate, weightDecay: job.WeightDecay);

            if (warmRestartDir is not null)
            {
                if (warmRestartPriorVocab is null)
                {
                    long? elements = Checkpoint.ReadTensorElementCount(warmRestartDir, "EmbeddingTable.weight");
                    if (elements is > 0 && elements.Value % modelConfig.HiddenDim == 0)
                        warmRestartPriorVocab = (int)(elements.Value / modelConfig.HiddenDim);
                }

                int priorVocab = warmRestartPriorVocab ?? 0;
                var warmMeta = Checkpoint.LoadForWarmRestart(
                    warmRestartDir, parameters, "EmbeddingTable.weight", optimizer);
                warmRestartStartStep = warmMeta.Step;
                if (warmRestartPriorVocab is null
                    && warmMeta.Extra.TryGetValue("vocabSize", out var rv)
                    && int.TryParse(rv, System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                    priorVocab = parsed;

                // Newly-added token rows start blank; initialise them from the
                // statistics of the reused rows so growth does not begin from an
                // out-of-distribution corner of the embedding space.
                var emb = weights.EmbeddingWeight;
                if (priorVocab > 0 && emb.Shape.Rows > priorVocab)
                    WeightInitializer.InitializeNewEmbeddingRows(emb, priorVocab, Seed + 7919);

                Log(priorVocab > 0
                    ? $"Warm restart: reused weights (checkpoint vocab {priorVocab} â†’ model vocab {modelConfig.VocabSize}); "
                      + $"{Math.Max(0, modelConfig.VocabSize - priorVocab)} new embedding rows initialised."
                    : $"Warm restart: reused weights; embedding rows beyond the checkpoint initialised.");
            }

            // Accelerator plugins live in the same folder as data-pipeline plugins.
            // An explicit accelerator that cannot be honoured (no device, unsupported shape) is
            // reported back to the UI, which shows a consent picker (CPU + every other capable
            // plugin) rather than failing the run â€” never a silent CPU fallback. Only genuinely
            // unfindable/non-capable names fail here with an error.
            var accelerators = AcceleratorLoader.LoadFrom(pluginsFolder, out var acceleratorWarnings);
            foreach (var w in acceleratorWarnings) Log($"Accelerator: {w}");
            var loss = new CrossEntropyLoss(labelSmoothing: job.LabelSmoothing);
            var mapping = GradientMappingFactory.Create(sharpConfig);

            (ITrainingEngine? Engine, string? Refusal, string Name) ResolveEngine()
            {
                string name = job.Accelerator?.Trim() ?? "";
                try
                {
                    var engine = TrainingEngineResolver.Resolve(name, accelerators,
                        new TrainingEngineContext(model, parameters, mapping, sharpConfig, loss,
                            BatchSize: job.BatchSize, SeqLen: job.SeqLen, LabelSmoothing: job.LabelSmoothing));
                    return (engine, null, name);
                }
                catch (AcceleratorUnavailableException ex)
                {
                    return (null, ex.Reason, name);
                }
            }

            ITrainingEngine? engine = null;
            if (!reuseCompletedCheckpoint)
            {
                var resolved = ResolveEngine();
                if (resolved.Refusal is not null)
                    return new TrainingRunResult { Success = false, AcceleratorRequested = resolved.Name, AcceleratorReason = resolved.Refusal };

                engine = resolved.Engine;
                if (engine is not null)
                {
                    // Re-derived from the same name Resolve just matched — FirstOrDefault
                    // rather than First so a future drift between the two lookups logs
                    // nothing instead of throwing "Sequence contains no matching element"
                    // into the generic catch around this run.
                    var chosen = accelerators.FirstOrDefault(p => string.Equals(p.Name, resolved.Name, StringComparison.OrdinalIgnoreCase));
                    if (chosen is not null)
                        Log($"Accelerator: {chosen.Name} — {chosen.Description}");
                    Log($"Engine: {engine.Description}");
                }
            }

            using var engineOwner = engine;

            // 5. Scheduler + loop (the optimizer was created before the warm restart).
            var scheduler = new CosineWithWarmup(
                maxLr: job.LearningRate, minLr: job.MinLr,
                warmupSteps: job.WarmupSteps, decaySteps: job.TotalSteps);

            // "Start fresh" must also ignore (and clear) an earlier run's adapter
            // checkpoints for this job, otherwise a later un-tick would pick up a
            // stale higher step from a longer previous run.
            if (job.IsFineTune && job.StartFresh && Directory.Exists(checkpointDir))
            {
                try
                {
                    Directory.Delete(checkpointDir, recursive: true);
                    Log("Fine-tune: cleared earlier adapter checkpoints (start fresh).");
                }
                catch (Exception ex)
                {
                    Log($"Fine-tune: could not clear earlier checkpoints ({ex.Message}); they are ignored this run.");
                }
            }

            Directory.CreateDirectory(checkpointDir);

            var qat = TrainingModelOptions.ParseQat(job.QuantAwareTraining);

            // Validation is evaluated at the logging cadence so every step the UI
            // receives carries a val loss, and the loop's own best-checkpoint
            // tracking sees the same points.
            Evaluator? evaluator = validationBatches.Count > 0 ? new Evaluator(model, loss) : null;
            double? bestValLoss = null;
            int bestValStep = 0;

            EvalResult Evaluate(int step)
            {
                var (valLossValue, perplexity) = evaluator!.Evaluate(validationBatches);
                if (float.IsFinite(valLossValue) && (bestValLoss is null || valLossValue < bestValLoss))
                {
                    bestValLoss = valLossValue;
                    bestValStep = step;
                }
                Log($"Validation step {step}: loss {valLossValue:0.0000}, ppl {perplexity:0.00}");
                return new EvalResult { Loss = valLossValue, Perplexity = perplexity };
            }

            var loop = new TrainLoop(
                model: model,
                parameters: parameters,
                loader: loader,
                optimizer: optimizer,
                scheduler: scheduler,
                ops: ops,
                loss: loss,
                engine: engine,
                smmConfig: sharpConfig,
                config: new TrainConfig
                {
                    TotalSteps = job.TotalSteps,
                    StartStep = warmRestartStartStep,
                    GradAccumSteps = job.GradAccumSteps,
                    GradClipNorm = job.GradClipNorm,
                    LabelSmoothing = job.LabelSmoothing,
                    LogInterval = Math.Max(1, job.LogInterval),
                    EvalInterval = evaluator is null ? 0 : Math.Max(1, job.LogInterval),
                    CheckpointInterval = job.CheckpointInterval,
                    CheckpointDir = checkpointDir,
                    ResumeFrom = resumeDir,
                    // An incremental run's stream is the delta only, so the cursor
                    // recorded by the previous run points into data this run never
                    // sees - applying it would discard exactly the new files the run
                    // exists to train on. A plain resume replays the same stream, so
                    // it picks the cursor up and continues where it stopped â€” unless
                    // the sources changed, in which case the old cursor no longer
                    // indexes this corpus and replaying it would silently stall.
                    ResumeDataCursor = !job.IncrementalMode && !changed,
                    DataSeed = Seed,
                    DataFingerprint = dataFingerprint,
                    KeepRecent = job.KeepRecent,
                    QuantAwareTraining = qat,
                    // Guard a strict resume against a vocabulary change (the model's
                    // padded vocab is what the checkpoint recorded). Zero during a
                    // warm restart so the guard is skipped â€” the whole point there
                    // is a deliberately widened embedding.
                    ExpectedVocabSize = warmRestartDir is null ? modelConfig.VocabSize : 0,
                });

            int lastStep = warmRestartStartStep;
            if (reuseCompletedCheckpoint)
            {
                // The cached checkpoint already covers the requested horizon. Load
                // its weights and optimizer state, then go straight to export; the
                // train phase is fully reused.
                Checkpoint.Load(resumeDir!, parameters, optimizer);
                lastStep = resumeStep;
                if (resumeValLoss is { } reusedValLoss)
                {
                    bestValLoss = reusedValLoss;
                    bestValStep = resumeStep;
                }
                progress.Report(0.9f);
                Log($"Recipe: train phase reused at step {resumeStep}; "
                    + $"requested {job.TotalSteps} step(s), exporting without more training.");
            }
            else
            {
                string stepPlan = warmRestartStartStep > 0
                    ? $"Continuing to step {job.TotalSteps} (from {warmRestartStartStep})"
                    : $"Training {job.TotalSteps} steps";
                Log(qat is { } q
                    ? $"{stepPlan} with QAT [{q}] — full backprop (loss → gradients → AdamW)…"
                    : $"{stepPlan} (float32) — full backprop (loss → gradients → AdamW)…");
                progress.Report(0.12f);
            }

            try
            {
                if (!reuseCompletedCheckpoint)
                {
                    await loop.RunAsync(
                        onStep: r =>
                        {
                            lastStep = r.Step;
                            onStep?.Invoke(r);
                        },
                        progress: new Progress<float>(p => progress.Report(0.12f + 0.78f * p)),
                        cancellationToken: cancellationToken,
                        onCheckpoint: dir =>
                        {
                            if (job.Rehearsal) return;
                            if (job.IsFineTune)
                            {
                                // The generic checkpoint holds the adapter parameters and
                                // optimizer state; the sidecar .smlora is the portable,
                                // base-fingerprinted form a later run can load.
                                if (lora is not null)
                                {
                                    try
                                    {
                                        lora.SaveAdapter(Path.Combine(dir, "adapters.smlora"));
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"Fine-tune: could not save the adapter checkpoint ({ex.Message}).");
                                    }
                                }
                                Log($"Fine-tune: checkpoint {Path.GetFileName(dir)} saved (adapters only).");
                                return;
                            }
                            ExportCheckpoint(dir, job, modelConfig, sharpConfig, tokenizer, Log);
                        },
                        evaluate: evaluator is null ? null : Evaluate);
                }
            }
            finally
            {
                // Held-out batches hold native tensors and are reused for every
                // evaluation, so they outlive the loop and must be freed here.
                foreach (TrainingBatch held in validationBatches) held.Dispose();
                if (job.Rehearsal) DeleteDirectoryQuietly(checkpointDir);
            }

            if (!reuseCompletedCheckpoint && lastStep <= warmRestartStartStep)
            {
                string? latestCheckpoint = Checkpoint.FindLatest(checkpointDir);
                if (latestCheckpoint is not null)
                {
                    int checkpointStep = Checkpoint.ReadMeta(latestCheckpoint).Step;
                    if (checkpointStep > lastStep) lastStep = checkpointStep;
                }
            }

            if (!job.Rehearsal && !job.IsFineTune && !job.IncrementalMode && !grewTokenizer)
            {
                string? checkpointPath = Checkpoint.FindLatest(checkpointDir);
                if (checkpointPath is not null)
                {
                    TrainingRecipeStore.Save(new TrainingRecipeArtifact(
                        "checkpoint", recipeTrainBaseHash, checkpointPath, job.Name, DateTime.UtcNow), recipeFolder);
                    Log($"Recipe: checkpoint phase {TrainingRecipeHash.Short(recipeTrainBaseHash)} "
                        + $"saved from step {lastStep}.");
                }
            }

            if (job.Rehearsal)
            {
                progress.Report(1f);
                Log($"Rehearsal finished at step {lastStep}.");
                return new TrainingRunResult
                {
                    Success = true,
                    FinalStep = lastStep,
                    BestValLoss = bestValLoss,
                    BestValStep = bestValStep > 0 ? bestValStep : null,
                };
            }

            // 6. A fine-tune can keep the adapters as a portable file and/or fold
            // them into the base weights. Save the adapter before merging: Merge
            // detaches it.
            string? adapterPath = null;
            if (lora is not null && job.SaveAdapterFile)
            {
                adapterPath = job.AdapterFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(adapterPath)!);
                lora.SaveAdapter(adapterPath);
                Log($"Adapter saved: {adapterPath} ({new FileInfo(adapterPath).Length:N0} bytes)");
            }

            if (lora is not null && job.MergeOnExport)
            {
                lora.Merge();
                Log("Fine-tune: adapters merged into the base weights.");
            }
            else if (job.IsFineTune)
            {
                Log("Fine-tune: merged export skipped (adapter-only run).");
            }

            // 7. Export the trained weights + tokenizer to .SMM.
            string? exportPath = null;
            if (!job.IsFineTune || job.MergeOnExport)
            {
                exportPath = job.ExportFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
                SmmTrainingExporter.Export(
                    weights,
                    tokenizer,
                    exportPath,
                    BuildEmbedOptions(job, job.IsSft ? "sft" : job.IsFineTune ? "fine-tune" : "training", SourceHasher.Combined(job.Sources)),
                    chatTemplate: baseChatTemplate,
                    model: model);
                // Keep the resolved location on the job (and therefore in the wizard
                // field when this live job is shown again), matching how the tokenizer
                // path is anchored to the job rather than a shared folder.
                job.ExportPath = exportPath;
                if (recipeTrainHash is not null && !grewTokenizer)
                {
                    TrainingRecipeStore.Save(new TrainingRecipeArtifact(
                        "train", recipeTrainHash, exportPath, job.Name, DateTime.UtcNow), recipeFolder);
                }
                Log($"Saved: {exportPath} ({new FileInfo(exportPath).Length:N0} bytes)");
            }

            progress.Report(1f);

            // Record the corpus as trained-that-far so the next incremental run
            // diffs against EXACTLY what these weights have seen (not what the
            // run started with). Persisted to both the job (in-memory, saved
            // when the wizard saves) and the checkpoint folder (survives app
            // restart even without a manual Save).
            if (job.IncrementalMode)
            {
                job.SourceFileHashes = plan.CurrentFileHashes;
                IncrementalStore.Save(job);
                Log("Incremental: recorded per-file hashes for the corpus just trained.");
            }

            return new TrainingRunResult
            {
                Success = true,
                ExportPath = exportPath,
                AdapterPath = adapterPath,
                FinalStep = lastStep,
                BestValLoss = bestValLoss,
                BestValStep = bestValStep > 0 ? bestValStep : null,
                Cached = recipeCheckpointReused,
                CachedPhase = recipeCheckpointReused ? "checkpoint" : null,
                RecipeHash = recipeTrainHash ?? recipeTrainBaseHash,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("Training interrupted by request.");
            return new TrainingRunResult { Success = false, Error = "cancelled" };
        }
        catch (OperationCanceledException ex)
        {
            // Reached only when the run's token was NOT cancelled, so this is an
            // incidental cancellation from an inner API (framework timeout, a
            // linked token elsewhere), not a user interrupt. Surface it as a
            // failure with its type so the real cause is visible instead of
            // silently returning to the wizard as if interrupted.
            Log($"Training failed: {ex.GetType().Name}: {ex.Message}");
            return new TrainingRunResult { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
        catch (Exception ex)
        {
            Log($"Training failed: {ex.Message}");
            return new TrainingRunResult { Success = false, Error = ex.Message };
        }
    }

    private static async Task<PipelineNode> CacheOrReuseDataAsync(
        TrainingJobSettings job,
        PipelineNode pipeline,
        string dataHash,
        string? recipeFolder,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        // A rehearsal must not leave a large artifact behind, and an incremental
        // run's pipeline is the delta only, not the full cleaned corpus.
        if (job.Rehearsal || job.IncrementalMode) return pipeline;

        if (TrainingRecipeStore.TryGet(
                "data", dataHash, out TrainingRecipeArtifact cached, recipeFolder)
            && File.Exists(cached.ArtifactPath))
        {
            log($"Recipe: reused data phase {TrainingRecipeHash.Short(dataHash)} - "
                + $"{Path.GetFileName(cached.ArtifactPath)}.");
            return PipelineNode.From(new JsonlSource(cached.ArtifactPath));
        }

        string folder = Path.Combine(recipeFolder ?? TrainingRecipeStore.Folder, "data");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, dataHash + ".jsonl");
        string temp = path + ".tmp";
        int documents = 0;

        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65_536, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                await foreach (string document in pipeline.ReadAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { text = document }))
                        .ConfigureAwait(false);
                    documents++;
                    if (documents % 10_000 == 0)
                        log($"Recipe: data phase prepared {documents:N0} document(s)...");
                }
            }

            File.Move(temp, path, overwrite: true);
            TrainingRecipeStore.Save(new TrainingRecipeArtifact(
                "data", dataHash, path, job.Name, DateTime.UtcNow), recipeFolder);
            log($"Recipe: data phase {TrainingRecipeHash.Short(dataHash)} cached "
                + $"{documents:N0} document(s).");
            return PipelineNode.From(new JsonlSource(path));
        }
        catch (Exception ex)
        {
            TryDeleteData(temp);
            log($"Recipe: could not cache data ({ex.Message}); using the source pipeline.");
            return pipeline;
        }
    }

    private static void TryDeleteData(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Resolves where a fine-tune should resume its adapters from: an explicit
    /// <see cref="TrainingJobSettings.ResumeFrom"/> (a <c>.smlora</c> file or a
    /// checkpoint folder containing one), otherwise the latest step checkpoint,
    /// otherwise the best-validation checkpoint. Returns null when there is
    /// nothing to resume, so the run starts from the base.
    /// </summary>
    private static string? ResolveAdapterResume(
        TrainingJobSettings job, string checkpointDir, out int step)
    {
        step = 0;

        if (!string.IsNullOrWhiteSpace(job.ResumeFrom))
        {
            string from = job.ResumeFrom.Trim();
            if (File.Exists(from))
            {
                if (!from.EndsWith(TrainingJobSettings.AdapterExtension, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"An adapter resume source must be a {TrainingJobSettings.AdapterExtension} file "
                        + $"or a checkpoint folder, not '{from}'.");
                return from;
            }

            if (Directory.Exists(from))
            {
                string file = Path.Combine(from, "adapters.smlora");
                if (!File.Exists(file))
                    throw new InvalidOperationException(
                        $"Adapter checkpoint '{from}' contains no adapters.smlora. Pick a fine-tune "
                        + "checkpoint, or tick Start fresh.");
                step = Checkpoint.ReadMeta(from).Step;
                return file;
            }

            throw new DirectoryNotFoundException($"Adapter resume source not found: {from}");
        }

        string? latest = Checkpoint.FindLatest(checkpointDir);
        if (latest is not null)
        {
            string file = Path.Combine(latest, "adapters.smlora");
            if (File.Exists(file))
            {
                step = Checkpoint.ReadMeta(latest).Step;
                return file;
            }
        }

        // With CheckpointInterval = 0 the loop still writes a "best" checkpoint
        // whenever validation improves; that adapter is resumable too.
        string bestDir = Path.Combine(checkpointDir, "best");
        string bestFile = Path.Combine(bestDir, "adapters.smlora");
        if (File.Exists(bestFile))
        {
            step = Checkpoint.ReadMeta(bestDir).Step;
            return bestFile;
        }

        return null;
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A rehearsal's temp checkpoints are disposable; a locked file is not a run failure.
        }
    }

    private static IDataSource BuildSource(JobComponent component, IReadOnlyList<ComponentDescriptor> registry)
    {
        if (component is null)
            throw new InvalidOperationException("No data source configured for this job.");
        var descriptor = ComponentRegistry.Find(component.TypeName, registry)
            ?? throw new InvalidOperationException($"Unknown data source '{component.TypeName}'.");
        return (IDataSource)ComponentRegistry.Build<IDataSource>(descriptor, component.Args);
    }

    /// <summary>
    /// Builds the record source an SFT run trains on: each configured job source
    /// is read as chat JSONL, its per-source cleaning stages run over the turn
    /// text, and the resulting streams are concatenated before the global stages.
    /// Exposed so the held-out exam can rebuild exactly the same records.
    /// </summary>
    public static IRecordSource BuildSftRecordSource(TrainingJobSettings job, string pluginsFolder)
        => BuildSftRecordSourceSet(job, pluginsFolder).Records;

    /// <inheritdoc cref="BuildSftRecordSource(TrainingJobSettings, string)"/>
    public static SftRecordSourceSet BuildSftRecordSourceSet(
        TrainingJobSettings job, string pluginsFolder)
    {
        ArgumentNullException.ThrowIfNull(job);
        var registry = ComponentRegistry.ScanFolder(pluginsFolder, out _);
        return BuildSftRecordSourceSet(job, registry);
    }

    /// <summary>
    /// The record source plus its JSONL readers. Callers that only need the
    /// stream use <see cref="BuildSftRecordSource(TrainingJobSettings, IReadOnlyList{ComponentDescriptor})"/>;
    /// the preflight uses the readers' file and skip counts.
    /// </summary>
    public static SftRecordSourceSet BuildSftRecordSourceSet(
        TrainingJobSettings job, IReadOnlyList<ComponentDescriptor> components)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Sources.Count == 0)
            throw new InvalidOperationException("No data sources configured for this job.");

        var sources = new List<IRecordSource>();
        var jsonlSources = new List<SftJsonlSource>();
        foreach (JobSource source in job.Sources)
        {
            if (!source.Component.Args.TryGetValue("path", out string? path)
                || string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    "An SFT source has no path. Add a JSONL file or glob to each source.");
            }

            var jsonl = new SftJsonlSource(path);
            jsonlSources.Add(jsonl);
            IRecordSource records = jsonl;
            foreach (JobComponent stage in source.Stages)
            {
                var descriptor = ComponentRegistry.Find(stage.TypeName, components)
                    ?? throw new InvalidOperationException($"Unknown stage '{stage.TypeName}'.");
                var cleaning = (ICleaningStage)ComponentRegistry.Build<ICleaningStage>(descriptor, stage.Args);
                records = ApplySftStage(records, cleaning);
            }
            sources.Add(records);
        }

        IRecordSource merged = sources.Count == 1 ? sources[0] : new CompositeRecordSource(sources);
        foreach (JobComponent stage in job.GlobalStages)
        {
            var descriptor = ComponentRegistry.Find(stage.TypeName, components)
                ?? throw new InvalidOperationException($"Unknown stage '{stage.TypeName}'.");
            var cleaning = (ICleaningStage)ComponentRegistry.Build<ICleaningStage>(descriptor, stage.Args);
            merged = ApplySftStage(merged, cleaning);
        }

        return new SftRecordSourceSet(merged, jsonlSources);
    }

    /// <inheritdoc cref="BuildSftRecordSourceSet(TrainingJobSettings, IReadOnlyList{ComponentDescriptor})"/>
    public static IRecordSource BuildSftRecordSource(
        TrainingJobSettings job, IReadOnlyList<ComponentDescriptor> components)
        => BuildSftRecordSourceSet(job, components).Records;

    /// <summary>
    /// Runs a string cleaning stage over every conversation turn, then rebuilds
    /// the transcript. A record is dropped when the stage drops any turn.
    /// </summary>
    private static IRecordSource ApplySftStage(IRecordSource source, ICleaningStage stage)
    {
        return source.Pipe(stage.Name, record =>
        {
            if (record.Turns is { Count: > 0 })
            {
                var turns = new List<ConversationTurn>(record.Turns.Count);
                foreach (ConversationTurn turn in record.Turns)
                {
                    string? cleaned = stage.Process(turn.Content);
                    if (string.IsNullOrWhiteSpace(cleaned)) return null;
                    turns.Add(turn with { Content = cleaned });
                }

                return record with
                {
                    Turns = turns,
                    Text = string.Join('\n', turns.Select(turn => $"{turn.Role}: {turn.Content}")),
                };
            }

            string? text = stage.Process(record.Text);
            return text is null ? null : record with { Text = text };
        });
    }

    /// <summary>
    /// Resolves the SFT chat formatter. "auto" (and null/blank) uses the base
    /// model's embedded chat template; a named format can override it for data
    /// in a different skeleton. Falls back to the plain formatter when no
    /// template is available.
    /// </summary>
    public static IChatPromptFormatter ResolveSftFormatter(string? strategy, string? chatTemplate)
    {
        string key = strategy?.Trim().ToLowerInvariant() ?? "";
        return key switch
        {
            "chatml" => string.IsNullOrWhiteSpace(chatTemplate)
                ? new SimpleFormatter()
                : new ChatMLFormatter(chatTemplate),
            "simple" => new SimpleFormatter(),
            "llama3" or "llama-3" => new Llama3Formatter(),
            "alpaca" => new AlpacaFormatter(),
            "questionanswer" or "question-answer" or "qa" => new QuestionAnswerFormatter(),
            "raw" => new RawTemplateFormatter(),
            _ => ChatPromptFormatterFactory.Create(chatTemplate),
        };
    }

    /// <summary>
    /// A composite over every configured source, unrestricted â€” used to train the
    /// tokenizer over the complete corpus even when the training pipeline of an
    /// incremental run is restricted to the delta files.
    /// </summary>
    private static IDataSource FullCorpusComposite(TrainingJobSettings job, IReadOnlyList<ComponentDescriptor> registry)
    {
        var all = job.Sources.Select(s => BuildSource(s.Component, registry)).ToList();
        return all.Count == 1 ? all[0] : new CompositeSource(all);
    }

    /// <summary>
    /// Builds the job's full corpus source (all sources merged) for a caller
    /// outside the runner â€” the wizard's vocabulary analysis and tokenizer-grow
    /// buttons. Throws when the job has no source or a source can't be resolved.
    /// </summary>
    public static IDataSource BuildCorpusSource(TrainingJobSettings job, string pluginsFolder)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Sources.Count == 0)
            throw new InvalidOperationException("No data sources configured for this job.");
        var registry = ComponentRegistry.ScanFolder(pluginsFolder, out _);
        return FullCorpusComposite(job, registry);
    }

    /// <summary>
    /// Builds the cleaned full-corpus pipeline (every source plus its stages and
    /// the job's global stages) without the incremental delta restriction. The
    /// exam uses this so it scores the same documents the run trained on, minus
    /// the held-out slice.
    /// </summary>
    public static PipelineNode BuildCleanPipeline(TrainingJobSettings job, string pluginsFolder)
    {
        ArgumentNullException.ThrowIfNull(job);
        var registry = ComponentRegistry.ScanFolder(pluginsFolder, out _);
        var nodes = new List<PipelineNode>();
        foreach (JobSource source in job.Sources)
        {
            if (source.Component is null) continue;
            PipelineNode node = PipelineNode.From(BuildSource(source.Component, registry));
            foreach (JobComponent stage in source.Stages)
            {
                var descriptor = ComponentRegistry.Find(stage.TypeName, registry)
                    ?? throw new InvalidOperationException($"Unknown stage '{stage.TypeName}'.");
                node = node.Pipe((ICleaningStage)ComponentRegistry.Build<ICleaningStage>(descriptor, stage.Args));
            }
            nodes.Add(node);
        }

        if (nodes.Count == 0)
            throw new InvalidOperationException("No data sources configured for this job.");

        PipelineNode pipeline = nodes.Count == 1 ? nodes[0] : CleaningPipeline.Merge(nodes);
        foreach (JobComponent stage in job.GlobalStages)
        {
            var descriptor = ComponentRegistry.Find(stage.TypeName, registry)
                ?? throw new InvalidOperationException($"Unknown stage '{stage.TypeName}'.");
            pipeline = pipeline.Pipe((ICleaningStage)ComponentRegistry.Build<ICleaningStage>(descriptor, stage.Args));
        }
        return pipeline;
    }

    /// <summary>
    /// Grows the job's cached tokenizer in place, appending only new merge tokens
    /// up to <paramref name="newTargetVocabSize"/> (see
    /// <see cref="BpeTrainer.GrowAsync"/>). Existing token IDs are preserved, which
    /// is what makes the corresponding embedding rows safe to reuse on a warm
    /// restart. Returns the resulting vocabulary size. Runs over the FULL corpus.
    /// </summary>
    public static async Task<int> GrowTokenizerAsync(
        TrainingJobSettings job,
        string pluginsFolder,
        int newTargetVocabSize,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        string path = job.TokenizerFilePath;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No cached tokenizer to grow at '{path}'. Train the tokenizer first.", path);

        var existing = TokenizerFile.Load(path);
        var source = BuildCorpusSource(job, pluginsFolder);
        var trainer = new SharpMind.Tokenization.Bpe.BpeTrainer(newTargetVocabSize);
        var grown = await trainer.GrowAsync(existing, source.ReadAsync(), newTargetVocabSize, cancellationToken);
        TokenizerFile.Save(grown, path);
        job.TrainedTokenizerVocab = grown.Vocab.Size;
        log?.Invoke($"Tokenizer grown: {existing.Vocab.Size} â†’ {grown.Vocab.Size} tokens ({path}).");
        return grown.Vocab.Size;
    }

    /// <summary>Exports a saved checkpoint directory as a testable .smm inside it.</summary>
    private static void ExportCheckpoint(
        string dir, TrainingJobSettings job, ModelConfig config, SharpMindConfig sharpConfig, Tokenizer tokenizer, Action<string> log)
    {
        try
        {
            using var weights = ModelFactory.CreateForTraining(config, sharpConfig);
            using var model = ModelFactory.CreateTrainingTransformer(weights, sharpConfig);
            var parameters = model.Parameters().ToList();
            var meta = Checkpoint.Load(dir, parameters, optimizer: null);

            string smmPath = Path.Combine(dir, "model.smm");
            SmmTrainingExporter.Export(weights, tokenizer, smmPath, BuildEmbedOptions(job, "checkpoint", SourceHasher.Combined(job.Sources)), model: model);
            log($"Checkpoint {Path.GetFileName(dir)} â†’ {Path.GetFileName(smmPath)} (step {meta.Step})");
        }
        catch (Exception ex)
        {
            log($"Checkpoint export failed for {dir}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the <see cref="SmmWriteOptions"/> that embed the job's configured
    /// system prompt file, skills folder, and plugin DLLs into the .smm. Files
    /// are read eagerly, so missing input at export time fails the run loudly
    /// instead of silently producing an unannotated model.
    /// </summary>
    private static SmmWriteOptions BuildEmbedOptions(TrainingJobSettings job, string source, string? checksum = null)
    {
        string? systemPrompt = null;
        if (!string.IsNullOrWhiteSpace(job.SystemPromptPath))
        {
            if (!File.Exists(job.SystemPromptPath))
                throw new InvalidOperationException($"System prompt file not found: {job.SystemPromptPath}");
            systemPrompt = File.ReadAllText(job.SystemPromptPath).Trim();
        }
        else if (job.IsFineTune)
        {
            // A merged fine-tune should stay as usable as its base: keep the
            // system prompt embedded in the base when the job does not override it.
            string? inherited = SmmLoader.LoadSystemPrompt(job.BaseModelPath!);
            if (!string.IsNullOrWhiteSpace(inherited)) systemPrompt = inherited;
        }

        List<string>? skills = null;
        if (!string.IsNullOrWhiteSpace(job.SkillsFolder))
        {
            if (!Directory.Exists(job.SkillsFolder))
                throw new InvalidOperationException($"Skills folder not found: {job.SkillsFolder}");
            skills = [.. Directory.GetFiles(job.SkillsFolder, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f))
                .Select(File.ReadAllText)];
            if (skills.Count == 0)
                throw new InvalidOperationException($"No *.md skill files found in: {job.SkillsFolder}");
        }
        else if (job.IsFineTune)
        {
            List<string> inherited = SmmLoader.LoadSkills(job.BaseModelPath!);
            if (inherited.Count > 0) skills = inherited;
        }

        List<SmmPluginEntry>? plugins = null;
        if (job.PluginDllPaths is { Count: > 0 })
        {
            plugins = [];
            foreach (var dll in job.PluginDllPaths)
            {
                if (!File.Exists(dll))
                    throw new InvalidOperationException($"Plugin DLL not found: {dll}");
                plugins.Add(new SmmPluginEntry { Name = Path.GetFileName(dll), AssemblyBytes = File.ReadAllBytes(dll) });
            }
        }

        return new SmmWriteOptions
        {
            Source = source,
            Checksum = checksum,
            Outputs = plugins is { Count: > 0 } ? SmmOutputs.Default | SmmOutputs.Plugins : SmmOutputs.Default,
            SystemPrompt = systemPrompt,
            Skills = skills,
            Plugins = plugins,
            Lineage = BuildLineage(job, checksum),
        };
    }

    /// <summary>
    /// Provenance stamped onto every .smm this runner exports: which recipe (job)
    /// produced it, which dataset fingerprint it trained on, when, and with what
    /// tool version. Parsed back via <see cref="SmmLoader.LoadLineage"/>.
    /// </summary>
    private static Dictionary<string, string> BuildLineage(TrainingJobSettings job, string? checksum)
    {
        var lineage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = typeof(TrainingRunner).Assembly.GetName().Version?.ToString() ?? "unknown",
            ["created"] = DateTime.UtcNow.ToString("O"),
        };
        if (!string.IsNullOrWhiteSpace(job.Name) && job.Name != "Untitled")
            lineage["recipe"] = job.Name;
        if (!string.IsNullOrWhiteSpace(checksum))
            lineage["dataset"] = checksum;
        if (job.IsFineTune)
        {
            lineage["base"] = job.BaseModelPath!;
            lineage["method"] = job.IsSft ? "sft" : "lora";
            lineage["lora_rank"] = job.LoRARank.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lineage["lora_alpha"] = job.LoRAAlpha.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (job.IsSft)
            {
                lineage["sft"] = "true";
                lineage["sft_formatter"] = string.IsNullOrWhiteSpace(job.SftFormatter)
                    ? "auto"
                    : job.SftFormatter!;
            }
        }
        return lineage;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "job" : name.Trim();
    }
}
