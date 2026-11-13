using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

/// <summary>Progress for a tune run: the current step and how many are done.</summary>
public sealed record ModelTuneProgress(string Message, int Completed, int Total);

/// <summary>
/// Runs the measured tune flow for one model and saves the presets. The Tests
/// list, a run detail and Settings all share this so every entry point produces
/// the same Performance/Balanced/Accuracy set. Tuning Work also tunes the Chat
/// baseline first, because Work copies generator, cache and context from it.
/// </summary>
public sealed class ModelTuneService
{
    private readonly StudioWorkspaceService _workspace;
    private readonly InferenceSettingsService _inference;
    private readonly TuneRunnerService _runner;
    private readonly StudioChatService _chat;

    public ModelTuneService(
        StudioWorkspaceService workspace,
        InferenceSettingsService inference,
        TuneRunnerService runner,
        StudioChatService chat)
    {
        _workspace = workspace;
        _inference = inference;
        _runner = runner;
        _chat = chat;
    }

    public async Task<TuneProfileSet> TuneAsync(
        ModelDescriptor model,
        TuneTarget target,
        IProgress<ModelTuneProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Free the loaded chat model before the benchmarks claim memory.
        await _chat.SuspendAsync().ConfigureAwait(false);

        string fingerprint = TuneFingerprint.ForModel(model.Path);
        TuneProfileSet? previous = _inference.TuneSet(model.Path);
        bool sameModel = previous is not null && previous.ModelFingerprint == fingerprint;
        List<TuneProfile> chatProfiles = sameModel ? [.. previous!.For(TuneTarget.Chat)] : [];
        string[] caches = [.. _workspace.Builders.Caches.Select(cache => cache.Name)];

        int completed = 0;
        int total = target == TuneTarget.Work && chatProfiles.Count < 3 ? 6 : 3;

        if (target == TuneTarget.Work && chatProfiles.Count < 3)
        {
            foreach (TunePreset preset in Enum.GetValues<TunePreset>())
            {
                progress?.Report(new($"Chat baseline: {preset}...", completed, total));
                TuneProfile profile = Recommend(model, TuneTarget.Chat, preset, caches);
                TuneMeasurement measurement = await _runner
                    .MeasureAsync(model, profile, cancellationToken)
                    .ConfigureAwait(false);
                chatProfiles.Add(profile with { Measurement = measurement });
                completed++;
            }

            Save(model.Path, fingerprint, previous, TuneTarget.Chat, chatProfiles);
            previous = _inference.TuneSet(model.Path);
            sameModel = previous?.ModelFingerprint == fingerprint;
        }

        List<TuneProfile> profiles = [];
        if (target == TuneTarget.Work)
        {
            IReadOnlyList<TuneProfile> baselines = sameModel
                ? previous!.For(TuneTarget.Chat)
                : chatProfiles;
            foreach (TunePreset preset in Enum.GetValues<TunePreset>())
            {
                TuneProfile profile = WorkBaseline(model, preset, baselines, caches);
                progress?.Report(new($"Work tool test: {preset}...", completed, total));
                TuneMeasurement measurement = await _runner
                    .MeasureAsync(model, profile, cancellationToken)
                    .ConfigureAwait(false);

                if (measurement.Succeeded && !measurement.ToolProbePassed)
                {
                    progress?.Report(new(
                        $"Tool call failed for {preset}; retrying with Standard + low temperature...",
                        completed, total));
                    TuneProfile tweak = profile with
                    {
                        Overrides = profile.Overrides with
                        {
                            Generator = "StandardGeneratorBuilder",
                            Temperature = 0.1f,
                            TopK = 1,
                            TopP = 1f,
                            MaxNewTokens = Math.Max(256, profile.Overrides.MaxNewTokens ?? 256),
                        },
                        Notes =
                        [
                            .. profile.Notes,
                            "Tool probe failed; retried StandardGeneratorBuilder at low temperature.",
                        ],
                    };
                    TuneMeasurement retry = await _runner
                        .MeasureAsync(model, tweak, cancellationToken)
                        .ConfigureAwait(false);
                    if (retry.ToolProbePassed)
                    {
                        profile = tweak;
                        measurement = retry;
                    }
                }

                profiles.Add(profile with { Measurement = measurement });
                completed++;
            }
        }
        else
        {
            foreach (TunePreset preset in Enum.GetValues<TunePreset>())
            {
                TuneProfile profile = (sameModel ? previous!.Find(TuneTarget.Chat, preset) : null)
                    ?? Recommend(model, TuneTarget.Chat, preset, caches);
                progress?.Report(new($"Testing {preset}...", completed, total));
                TuneMeasurement measurement = await _runner
                    .MeasureAsync(model, profile, cancellationToken)
                    .ConfigureAwait(false);
                profiles.Add(profile with { Measurement = measurement });
                completed++;
            }
        }

        Save(model.Path, fingerprint, previous, target, profiles);
        progress?.Report(new(
            $"Tuned {target}: Performance, Balanced, Accuracy.", completed, total));
        return _inference.TuneSet(model.Path)!;
    }

    private TuneProfile Recommend(
        ModelDescriptor model,
        TuneTarget target,
        TunePreset preset,
        IReadOnlyList<string> caches)
    {
        TuneRecommendation recommendation = TunePlanner.Recommend(model, target, preset, caches);
        return new TuneProfile
        {
            Target = target,
            Preset = preset,
            Overrides = recommendation.Overrides,
            Estimate = recommendation.Estimate,
            Notes = recommendation.Notes,
        };
    }

    private TuneProfile WorkBaseline(
        ModelDescriptor model,
        TunePreset preset,
        IReadOnlyList<TuneProfile> chatProfiles,
        IReadOnlyList<string> caches)
    {
        TuneProfile baseline = chatProfiles.FirstOrDefault(profile => profile.Preset == preset)
            ?? Recommend(model, TuneTarget.Chat, preset, caches);
        TuneProfile work = Recommend(model, TuneTarget.Work, preset, caches);
        InferenceOverrides overrides = baseline.Overrides with
        {
            Temperature = work.Overrides.Temperature,
            MaxNewTokens = work.Overrides.MaxNewTokens,
            RepetitionPenalty = work.Overrides.RepetitionPenalty,
            ToolCallRecovery = work.Overrides.ToolCallRecovery,
        };
        return work with
        {
            Overrides = overrides,
            Estimate = TunePlanner.Estimate(
                model,
                overrides.Cache ?? "KVCacherBuilder",
                overrides.MaxContextTokens ?? model.ContextLength),
            Notes =
            [
                .. work.Notes,
                "Work baseline copied generator, cache and context from Chat; then applied Work sampling.",
            ],
        };
    }

    private void Save(
        string modelPath,
        string fingerprint,
        TuneProfileSet? previous,
        TuneTarget target,
        IEnumerable<TuneProfile> profiles)
    {
        List<TuneProfile> merged = previous is not null && previous.ModelFingerprint == fingerprint
            ? [.. previous.Profiles.Where(profile => profile.Target != target), .. profiles]
            : [.. profiles];
        _inference.SetTuneSet(modelPath, new TuneProfileSet
        {
            ModelFingerprint = fingerprint,
            Profiles = merged,
        });
    }
}
