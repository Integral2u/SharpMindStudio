using SharpMind.Inference.Chat;

namespace SharpMind.Studio.Core.Inference;

public sealed record InferenceOverrides
{
    public string? Generator { get; init; }
    public string? Cache { get; init; }
    public string? Formatter { get; init; }
    public string? LoadMode { get; init; }
    public string? ToolCallRecovery { get; init; }
    public string? HardwareTier { get; init; }
    public int? MaxParallelLoadDegree { get; init; }
    public bool? UseParallelKernels { get; init; }
    public string? InferenceAccelerator { get; init; }
    public float? Temperature { get; init; }
    public int? TopK { get; init; }
    public float? TopP { get; init; }
    public float? MinP { get; init; }
    public int? Seed { get; init; }
    public int? MaxNewTokens { get; init; }
    public int? MaxContextTokens { get; init; }
    public float? RepetitionPenalty { get; init; }
    public int? RepetitionWindow { get; init; }
    public int? PromptLookupDraft { get; init; }
    public bool? ShowThinking { get; init; }
    public bool? EnableThinking { get; init; }
    public string? Compactor { get; init; }

    /// <summary>LFM3-Audio TTS voice phrase: "female" or "male".</summary>
    public string? TtsVoice { get; init; }

    /// <summary>LFM3-Audio TTS language phrase, for example "American English".</summary>
    public string? TtsLanguage { get; init; }

    /// <summary>Upper bound on generated speech frames (one frame is 80 ms).</summary>
    public int? TtsMaxFrames { get; init; }

    /// <summary>Depth-decoder sampling temperature.</summary>
    public float? TtsTemperature { get; init; }

    /// <summary>Depth-decoder top-k.</summary>
    public int? TtsTopK { get; init; }

    /// <summary>Depth-decoder sampling seed; null picks a time-based seed.</summary>
    public int? TtsSeed { get; init; }

    /// <summary>Longest image side, in pixels, before the LFM3-VL encoder.</summary>
    public int? VisionMaxSide { get; init; }

    /// <summary>Upper bound on generated vision-answer tokens.</summary>
    public int? VisionMaxTokens { get; init; }

    public static InferenceOverrides SystemDefaults() => new()
    {
        Generator = "StandardGeneratorBuilder",
        Cache = "KVCacherBuilder",
        Formatter = "Auto",
        LoadMode = "Full",
        ToolCallRecovery = "Lenient",
        HardwareTier = "Auto",
        MaxParallelLoadDegree = 0,
        UseParallelKernels = true,
        Temperature = 0.6f,
        TopK = 0,
        TopP = 0.9f,
        MinP = 0f,
        MaxNewTokens = 512,
        RepetitionPenalty = 1.1f,
        RepetitionWindow = 0,
        PromptLookupDraft = 0,
        ShowThinking = true,
        EnableThinking = false,
        Compactor = "None",
    };
}

public sealed record ResolvedInference
{
    public required string Generator { get; init; }
    public required string Cache { get; init; }
    public required string Formatter { get; init; }
    public required string LoadMode { get; init; }
    public required ToolCallRecoveryMode ToolCallRecovery { get; init; }
    public required string HardwareTier { get; init; }
    public required int MaxParallelLoadDegree { get; init; }
    public required bool UseParallelKernels { get; init; }
    public string? InferenceAccelerator { get; init; }
    public required float Temperature { get; init; }
    public required int TopK { get; init; }
    public required float TopP { get; init; }
    public required float MinP { get; init; }
    public int? Seed { get; init; }
    public required int MaxNewTokens { get; init; }
    public int? MaxContextTokens { get; init; }
    public required float RepetitionPenalty { get; init; }
    public required int RepetitionWindow { get; init; }
    public required int PromptLookupDraft { get; init; }
    public required bool ShowThinking { get; init; }
    public required bool EnableThinking { get; init; }
    public required string Compactor { get; init; }
    public required IReadOnlyDictionary<string, string> Sources { get; init; }
}

public static class InferenceResolver
{
    public static ResolvedInference Resolve(
        InferenceOverrides system,
        InferenceOverrides? family,
        InferenceOverrides? model,
        InferenceOverrides? session)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        string String(string key, string? s, string? m, string? f, string? sys, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(s)) { sources[key] = "Session"; return s; }
            if (!string.IsNullOrWhiteSpace(m)) { sources[key] = "Model"; return m; }
            if (!string.IsNullOrWhiteSpace(f)) { sources[key] = "Family"; return f; }
            if (!string.IsNullOrWhiteSpace(sys)) { sources[key] = "System"; return sys; }
            sources[key] = "Default";
            return fallback;
        }

        T Value<T>(string key, T? s, T? m, T? f, T? sys, T fallback) where T : struct
        {
            if (s.HasValue) { sources[key] = "Session"; return s.Value; }
            if (m.HasValue) { sources[key] = "Model"; return m.Value; }
            if (f.HasValue) { sources[key] = "Family"; return f.Value; }
            if (sys.HasValue) { sources[key] = "System"; return sys.Value; }
            sources[key] = "Default";
            return fallback;
        }

        T? Nullable<T>(string key, T? s, T? m, T? f, T? sys) where T : struct
        {
            if (s.HasValue) { sources[key] = "Session"; return s; }
            if (m.HasValue) { sources[key] = "Model"; return m; }
            if (f.HasValue) { sources[key] = "Family"; return f; }
            if (sys.HasValue) { sources[key] = "System"; return sys; }
            sources[key] = "Default";
            return null;
        }

        string recoveryText = String(
            "ToolCallRecovery",
            session?.ToolCallRecovery,
            model?.ToolCallRecovery,
            family?.ToolCallRecovery,
            system.ToolCallRecovery,
            "Lenient");
        ToolCallRecoveryMode recovery = Enum.TryParse(
            recoveryText, ignoreCase: true, out ToolCallRecoveryMode parsedRecovery)
            ? parsedRecovery
            : ToolCallRecoveryMode.Lenient;

        return new ResolvedInference
        {
            Generator = String("Generator", session?.Generator, model?.Generator, family?.Generator, system.Generator, "StandardGeneratorBuilder"),
            Cache = String("Cache", session?.Cache, model?.Cache, family?.Cache, system.Cache, "KVCacherBuilder"),
            Formatter = String("Formatter", session?.Formatter, model?.Formatter, family?.Formatter, system.Formatter, "Auto"),
            LoadMode = String("LoadMode", session?.LoadMode, model?.LoadMode, family?.LoadMode, system.LoadMode, "Full"),
            ToolCallRecovery = recovery,
            HardwareTier = String("HardwareTier", session?.HardwareTier, model?.HardwareTier, family?.HardwareTier, system.HardwareTier, "Auto"),
            MaxParallelLoadDegree = Value("MaxParallelLoadDegree", session?.MaxParallelLoadDegree, model?.MaxParallelLoadDegree, family?.MaxParallelLoadDegree, system.MaxParallelLoadDegree, 0),
            UseParallelKernels = Value("UseParallelKernels", session?.UseParallelKernels, model?.UseParallelKernels, family?.UseParallelKernels, system.UseParallelKernels, true),
            InferenceAccelerator = String("InferenceAccelerator", session?.InferenceAccelerator, model?.InferenceAccelerator, family?.InferenceAccelerator, system.InferenceAccelerator, ""),
            Temperature = Value("Temperature", session?.Temperature, model?.Temperature, family?.Temperature, system.Temperature, 0.6f),
            TopK = Value("TopK", session?.TopK, model?.TopK, family?.TopK, system.TopK, 0),
            TopP = Value("TopP", session?.TopP, model?.TopP, family?.TopP, system.TopP, 0.9f),
            MinP = Value("MinP", session?.MinP, model?.MinP, family?.MinP, system.MinP, 0f),
            Seed = Nullable("Seed", session?.Seed, model?.Seed, family?.Seed, system.Seed),
            MaxNewTokens = Value("MaxNewTokens", session?.MaxNewTokens, model?.MaxNewTokens, family?.MaxNewTokens, system.MaxNewTokens, 512),
            MaxContextTokens = Nullable("MaxContextTokens", session?.MaxContextTokens, model?.MaxContextTokens, family?.MaxContextTokens, system.MaxContextTokens),
            RepetitionPenalty = Value("RepetitionPenalty", session?.RepetitionPenalty, model?.RepetitionPenalty, family?.RepetitionPenalty, system.RepetitionPenalty, 1.1f),
            RepetitionWindow = Value("RepetitionWindow", session?.RepetitionWindow, model?.RepetitionWindow, family?.RepetitionWindow, system.RepetitionWindow, 0),
            PromptLookupDraft = Value("PromptLookupDraft", session?.PromptLookupDraft, model?.PromptLookupDraft, family?.PromptLookupDraft, system.PromptLookupDraft, 0),
            ShowThinking = Value("ShowThinking", session?.ShowThinking, model?.ShowThinking, family?.ShowThinking, system.ShowThinking, true),
            EnableThinking = Value("EnableThinking", session?.EnableThinking, model?.EnableThinking, family?.EnableThinking, system.EnableThinking, false),
            Compactor = String("Compactor", session?.Compactor, model?.Compactor, family?.Compactor, system.Compactor, "None"),
            Sources = sources,
        };
    }
}

