using System.Diagnostics;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Jobs;
using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Services;

public sealed class TuneRunnerService
{
    private readonly StudioWorkspaceService _workspace;

    public TuneRunnerService(StudioWorkspaceService workspace) => _workspace = workspace;

    public async Task<TuneMeasurement> MeasureAsync(
        ModelDescriptor model,
        TuneProfile profile,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Type? generator = TypeFor(profile.Overrides.Generator);
            Type? cache = TypeFor(profile.Overrides.Cache);
            var request = new ModelBenchRequest
            {
                ModelPath = model.Path,
                Prompt = "The capital of France is",
                WarmupTokens = 8,
                MeasureTokens = 24,
                FullLoad = !string.Equals(profile.Overrides.LoadMode, "Streaming", StringComparison.OrdinalIgnoreCase),
                GeneratorType = generator,
                CacheType = cache,
            };

            RunHandle run = _workspace.Runs.Create(ModelBenchJob.Kind, Path.GetFileNameWithoutExtension(model.Path));
            double tokensPerSecond;
            await using (RunEventWriter events = run.OpenWriter())
            {
                ModelBenchResult bench = await ModelBenchJob.RunAsync(request, run, events, cancellationToken);
                tokensPerSecond = bench.TokensPerSecond;
            }

            bool toolPassed = true;
            if (profile.Target == TuneTarget.Work)
            {
                ToolCallRecoveryMode recovery = Enum.TryParse(
                    profile.Overrides.ToolCallRecovery, ignoreCase: true, out ToolCallRecoveryMode parsedRecovery)
                    ? parsedRecovery
                    : ToolCallRecoveryMode.Lenient;

                RunHandle probeRun = _workspace.Runs.Create(ToolProbeJob.Kind, Path.GetFileNameWithoutExtension(model.Path));
                await using RunEventWriter probeEvents = probeRun.OpenWriter();
                ToolProbeResult probe = await ToolProbeJob.RunAsync(new ToolProbeRequest
                {
                    ModelPath = model.Path,
                    FullLoad = request.FullLoad,
                    GeneratorType = generator,
                    CacheType = cache,
                    ToolCallRecovery = recovery,
                }, probeRun, probeEvents, cancellationToken);
                toolPassed = probe.Passed;
            }

            return new TuneMeasurement
            {
                Succeeded = true,
                ToolProbePassed = toolPassed,
                LoadMs = 0,
                TtftMs = 0,
                TokensPerSecond = tokensPerSecond,
                PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TuneMeasurement
            {
                Succeeded = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            };
        }
    }

    private Type? TypeFor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var generator = _workspace.Builders.Generators.FirstOrDefault(option =>
            option.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (generator is not null) return generator.BuilderType;
        return _workspace.Builders.Caches.FirstOrDefault(option =>
            option.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Type;
    }
}


