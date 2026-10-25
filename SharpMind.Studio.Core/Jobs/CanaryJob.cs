using SharpMind.Studio.Core.Runs;

namespace SharpMind.Studio.Core.Jobs;

public sealed record CanaryRequest
{
    public required IReadOnlyList<string> Models { get; init; }
    public string Prompt { get; init; } = "The capital of France is";
    public int WarmupTokens { get; init; } = 16;
    public int MeasureTokens { get; init; } = 32;
    public bool FullLoad { get; init; } = true;
    public Type? GeneratorType { get; init; }
    public Type? CacheType { get; init; }
    public bool IncludeToolProbe { get; init; }
}

public sealed record CanaryResult
{
    public required string ModelPath { get; init; }
    public required string RunId { get; init; }
    public required bool Succeeded { get; init; }
    public double TokensPerSecond { get; init; }
    public double MeasureMs { get; init; }
    public long AllocatedBytesPerToken { get; init; }
    public string? Error { get; init; }
    public string ToolCallStatus { get; init; } = "N/A";
    public string? ToolRunId { get; init; }
    public string? ToolDetail { get; init; }
}

/// <summary>Runs a list of representative models as individual runs.</summary>
public static class CanaryJob
{
    public static async Task<IReadOnlyList<CanaryResult>> RunAsync(
        CanaryRequest request,
        RunStore store,
        IProgress<string>? progress = null,
        Action<string>? onModelStarted = null,
        Action<CanaryResult>? onResult = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);

        var results = new List<CanaryResult>(request.Models.Count);
        foreach (string model in request.Models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(model);
            onModelStarted?.Invoke(model);
            progress?.Report($"Running {name}");

            var run = store.Create(ModelBenchJob.Kind, Path.GetFileNameWithoutExtension(model));
            await using var writer = run.OpenWriter();
            try
            {
                var result = await ModelBenchJob.RunAsync(new ModelBenchRequest
                {
                    ModelPath = model,
                    Prompt = request.Prompt,
                    WarmupTokens = request.WarmupTokens,
                    MeasureTokens = request.MeasureTokens,
                    FullLoad = request.FullLoad,
                    GeneratorType = request.GeneratorType,
                    CacheType = request.CacheType,
                }, run, writer, cancellationToken).ConfigureAwait(false);

                string toolStatus = "N/A";
                string? toolRunId = null;
                string? toolDetail = null;
                if (request.IncludeToolProbe)
                {
                    progress?.Report($"{name}: probing tool call");
                    (toolStatus, toolRunId, toolDetail) = await RunToolProbeAsync(
                        model, request, store, cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync(new ToolProbeCompleted
                    {
                        Status = toolStatus,
                        Detail = toolDetail ?? "",
                        ToolRunId = toolRunId,
                    }, cancellationToken).ConfigureAwait(false);
                }

                var completed = new CanaryResult
                {
                    ModelPath = model,
                    RunId = run.RunId,
                    Succeeded = true,
                    TokensPerSecond = result.TokensPerSecond,
                    MeasureMs = result.MeasureMs,
                    AllocatedBytesPerToken = result.AllocatedBytesPerToken,
                    ToolCallStatus = toolStatus,
                    ToolRunId = toolRunId,
                    ToolDetail = toolDetail,
                };
                results.Add(completed);
                onResult?.Invoke(completed);
                progress?.Report($"{name}: {result.TokensPerSecond:F2} tok/s; tool {toolStatus}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failed = new CanaryResult
                {
                    ModelPath = model,
                    RunId = run.RunId,
                    Succeeded = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                    ToolCallStatus = "N/A",
                };
                results.Add(failed);
                onResult?.Invoke(failed);
                progress?.Report($"{name}: failed");
            }
        }

        return results;
    }

    private static async Task<(string Status, string RunId, string? Detail)> RunToolProbeAsync(
        string model,
        CanaryRequest request,
        RunStore store,
        CancellationToken cancellationToken)
    {
        var run = store.Create(ToolProbeJob.Kind, Path.GetFileNameWithoutExtension(model));
        await using var writer = run.OpenWriter();
        try
        {
            ToolProbeResult result = await ToolProbeJob.RunAsync(new ToolProbeRequest
            {
                ModelPath = model,
                GeneratorType = request.GeneratorType,
                CacheType = request.CacheType,
                FullLoad = request.FullLoad,
            }, run, writer, cancellationToken).ConfigureAwait(false);

            string detail = $"toolCalled={result.ToolCalled}; options=[{string.Join(", ", result.Options)}]; call={result.Response}";
            return (result.Passed ? "Yes" : "No", run.RunId, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ("N/A", run.RunId, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}





