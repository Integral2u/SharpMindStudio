namespace SharpMind.Studio.Core.Inference;

public enum TunePreset
{
    Performance,
    Balanced,
    Accuracy,
}

public enum TuneTarget
{
    Chat,
    Work,
}

public sealed record TuneEstimate(
    long TotalMemoryBytes,
    long BudgetBytes,
    long ModelBytes,
    long KvBytes,
    int ContextTokens,
    bool FitsFull,
    string LoadMode);

public sealed record TuneRecommendation(
    InferenceOverrides Overrides,
    TuneEstimate Estimate,
    IReadOnlyList<string> Notes);

public static class TunePlanner
{
    public static TuneRecommendation Recommend(
        ModelDescriptor model,
        TuneTarget target,
        TunePreset preset,
        IReadOnlyList<string> availableCaches,
        long? totalMemoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        long totalMemory = totalMemoryOverride ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalMemory <= 0) totalMemory = 16L * 1024 * 1024 * 1024;
        long budget = (long)(totalMemory * 0.90);

        bool int8Available = availableCaches.Contains(
            "Int8KVCacherBuilder", StringComparer.OrdinalIgnoreCase);

        long modelBytes = Math.Max(0, model.SizeBytes);
        int modelContext = model.ContextLength > 0 ? model.ContextLength : 8192;
        int presetContext = preset switch
        {
            TunePreset.Performance => 4096,
            TunePreset.Balanced => 8192,
            _ => 16384,
        };
        int wantedContext = Math.Min(modelContext, presetContext);
        int contextCap = Math.Max(512, modelContext);

        int layers = Math.Max(1, model.NumLayers);
        int kvHeads = Math.Max(1, model.NumKvHeads);
        int keyLength = Math.Max(1, model.KeyLength);
        int valueLength = Math.Max(1, model.ValueLength);
        long kvPerTokenF32 = (long)layers * kvHeads * (keyLength + valueLength) * 4;
        long kvPerTokenInt8 = (long)layers * kvHeads * (keyLength + valueLength);

        // Prefer the float cache whenever it fits: the int8 cache saves memory
        // but is lossy and was not faster on this CPU. Fall back to int8 only
        // when float KV would force a smaller context, and to streaming when the
        // weights themselves do not fit.
        long freeForKv = Math.Max(0, budget - modelBytes);
        int ContextThatFits(long perToken) => perToken > 0
            ? (int)Math.Min(int.MaxValue, freeForKv / perToken)
            : wantedContext;

        int f32Context = ContextThatFits(kvPerTokenF32);
        int int8Context = ContextThatFits(kvPerTokenInt8);

        string cache;
        int context;
        bool fitsFull;

        if (modelBytes > budget)
        {
            cache = int8Available ? "Int8KVCacherBuilder" : "KVCacherBuilder";
            context = Math.Clamp(
                Math.Min(wantedContext, cache == "Int8KVCacherBuilder" ? int8Context : f32Context),
                512, contextCap);
            fitsFull = false;
        }
        else if (f32Context >= wantedContext)
        {
            cache = "KVCacherBuilder";
            context = wantedContext;
            fitsFull = true;
        }
        else if (int8Available && int8Context >= wantedContext)
        {
            cache = "Int8KVCacherBuilder";
            context = wantedContext;
            fitsFull = true;
        }
        else if (int8Available && int8Context > f32Context)
        {
            cache = "Int8KVCacherBuilder";
            context = Math.Clamp(int8Context, 512, contextCap);
            fitsFull = modelBytes + kvPerTokenInt8 * context <= budget;
        }
        else
        {
            cache = "KVCacherBuilder";
            context = Math.Clamp(f32Context, 512, contextCap);
            fitsFull = modelBytes + kvPerTokenF32 * context <= budget;
        }

        long kvBytesPerToken = cache == "Int8KVCacherBuilder" ? kvPerTokenInt8 : kvPerTokenF32;
        long kvBytes = kvBytesPerToken * context;
        string loadMode = fitsFull ? "Full" : "Streaming";

        var notes = new List<string>
        {
            $"Memory budget {Mb(budget)} of {Mb(totalMemory)} (10% system reserve).",
            $"Weights {Mb(modelBytes)}; KV cache {Mb(kvBytes)} at {context:N0} tokens.",
            fitsFull
                ? "Weights and KV fit; Full load selected."
                : "Weights and KV exceed the budget; Streaming selected.",
        };
        if (cache == "Int8KVCacherBuilder")
            notes.Add("Int8 KV selected for memory; it is lossy and may change greedy ids.");
        if (target == TuneTarget.Work)
            notes.Add("Work presets default to StandardGeneratorBuilder; other and plugin generators can be selected, but verify tool calls after changing it.");

        // Tool calls need structure; Chat has no tools, so it can stay strict.
        // Work keeps Lenient by default and opts into Loose only for Accuracy.
        string toolCallRecovery = target == TuneTarget.Work
            ? preset == TunePreset.Accuracy ? "Loose" : "Lenient"
            : "Strict";
        if (target == TuneTarget.Work)
            notes.Add($"Tool-call recovery: {toolCallRecovery} (Loose never guesses filesystem paths).");
        if (modelBytes > budget)
            notes.Add("Weights alone exceed the budget; Streaming keeps a resident window instead of loading everything.");
        if (context < wantedContext)
            notes.Add($"Context reduced from {wantedContext:N0} to {context:N0} to respect memory.");

        // Tool calls need the model to commit to exact names and JSON, so Work runs cold.
        float temperature = target == TuneTarget.Work
            ? preset switch { TunePreset.Performance => 0.2f, TunePreset.Balanced => 0.1f, _ => 0.05f }
            : preset switch { TunePreset.Performance => 0.7f, TunePreset.Balanced => 0.6f, _ => 0.2f };
        int maxNew = target == TuneTarget.Work
            ? preset switch { TunePreset.Performance => 256, TunePreset.Balanced => 512, _ => 768 }
            : preset switch { TunePreset.Performance => 512, TunePreset.Balanced => 1024, _ => 1536 };

        var overrides = new InferenceOverrides
        {
            Generator = "StandardGeneratorBuilder",
            Cache = cache,
            Formatter = "Auto",
            LoadMode = loadMode,
            ToolCallRecovery = toolCallRecovery,
            HardwareTier = "Auto",
            MaxParallelLoadDegree = 0,
            UseParallelKernels = true,
            Temperature = temperature,
            TopK = 0,
            TopP = preset == TunePreset.Accuracy ? 0.9f : 0.95f,
            MinP = 0f,
            MaxNewTokens = maxNew,
            MaxContextTokens = context,
            RepetitionPenalty = 1.1f,
            RepetitionWindow = 0,
            ShowThinking = true,
            EnableThinking = false,
            MtpDraft = true,
            MtpDraftTokens = preset switch
            {
                TunePreset.Performance => 6,
                TunePreset.Balanced => 4,
                _ => 2,
            },
            Compactor = "None",
        };

        return new TuneRecommendation(
            overrides,
            new TuneEstimate(totalMemory, budget, modelBytes, kvBytes, context, fitsFull, loadMode),
            notes);
    }

    public static TuneEstimate Estimate(
        ModelDescriptor model,
        string cache,
        int contextTokens,
        long? totalMemoryOverride = null)
    {
        long totalMemory = totalMemoryOverride ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalMemory <= 0) totalMemory = 16L * 1024 * 1024 * 1024;
        long budget = (long)(totalMemory * 0.90);
        long weights = Math.Max(0, model.SizeBytes);
        long kv = EstimateKvBytes(model, cache, contextTokens);
        bool fits = weights + kv <= budget;
        return new TuneEstimate(
            totalMemory, budget, weights, kv, Math.Max(0, contextTokens),
            fits, fits ? "Full" : "Streaming");
    }
    public static long EstimateKvBytes(ModelDescriptor model, string cache, int contextTokens)
    {
        int bytesPerElement = cache == "Int8KVCacherBuilder" ? 1 : 4;
        return (long)Math.Max(1, model.NumLayers) *
               Math.Max(1, model.NumKvHeads) *
               (Math.Max(1, model.KeyLength) + Math.Max(1, model.ValueLength)) *
               bytesPerElement *
               Math.Max(0, contextTokens);
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):N0} MB";
}



