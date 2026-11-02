using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Jobs;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Services;

/// <summary>
/// Builds a canary request that re-runs existing runs. Each model uses its tuned
/// Performance profile when one exists, and system defaults otherwise.
/// </summary>
public static class CanaryRerunPlanner
{
    public sealed record Plan(CanaryRequest? Request, int Skipped);

    public static Plan Build(
        IEnumerable<RunSummary> runs,
        StudioWorkspaceService workspace,
        InferenceSettingsService inference,
        bool includeToolProbe = true)
    {
        var models = new List<string>();
        var settings = new Dictionary<string, CanaryModelSettings>(StringComparer.OrdinalIgnoreCase);
        RunSummary? first = null;
        int skipped = 0;

        foreach (RunSummary run in runs)
        {
            string? path = workspace.ResolveModelPath(run.ModelPath) ??
                           workspace.ResolveModelPath(run.Subject);
            if (path is null)
            {
                skipped++;
                continue;
            }

            first ??= run;
            if (settings.ContainsKey(path)) continue;

            models.Add(path);
            settings[path] = SettingsFor(path, workspace, inference);
        }

        if (models.Count == 0 || first is null) return new Plan(null, skipped);

        var fallback = SettingsFor(null, workspace, inference);
        return new Plan(new CanaryRequest
        {
            Models = models,
            Prompt = string.IsNullOrWhiteSpace(first.Prompt) ? "The capital of France is" : first.Prompt,
            WarmupTokens = Math.Max(1, first.WarmupTokens ?? 16),
            MeasureTokens = Math.Max(1, first.MeasureTokens ?? 32),
            GeneratorType = fallback.GeneratorType,
            CacheType = fallback.CacheType,
            IncludeToolProbe = includeToolProbe,
            ModelSettings = settings,
        }, skipped);
    }

    private static CanaryModelSettings SettingsFor(
        string? modelPath,
        StudioWorkspaceService workspace,
        InferenceSettingsService inference)
    {
        TuneProfile? tuned = modelPath is null
            ? null
            : inference.TuneProfile(modelPath, TuneTarget.Chat, TunePreset.Performance);

        string? generatorName = tuned?.Overrides.Generator ?? inference.System.Generator;
        string? cacheName = tuned?.Overrides.Cache ?? inference.System.Cache;

        Type? generator = workspace.Builders.Generators.FirstOrDefault(value =>
            value.Name.Equals(generatorName, StringComparison.OrdinalIgnoreCase))?.BuilderType;
        Type? cache = workspace.Builders.Caches.FirstOrDefault(value =>
            value.Name.Equals(cacheName, StringComparison.OrdinalIgnoreCase))?.Type;

        return new CanaryModelSettings(generator, cache);
    }
}
