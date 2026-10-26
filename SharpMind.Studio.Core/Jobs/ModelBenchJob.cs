using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpMind.Core.Quantization;
using SharpMind.Inference;
using SharpMind.Model;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Runs;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Jobs;

public sealed record ModelBenchRequest
{
    public required string ModelPath { get; init; }
    public string Prompt { get; init; } = "The capital of France is";
    public int WarmupTokens { get; init; } = 16;
    public int MeasureTokens { get; init; } = 64;
    public bool FullLoad { get; init; } = true;
    public Type? GeneratorType { get; init; }
    public Type? CacheType { get; init; }
}

public sealed record ModelBenchResult
{
    public required string RunId { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<int> GeneratedIds { get; init; }
    public required int Steps { get; init; }
    public required double MeasureMs { get; init; }
    public required long AllocatedBytes { get; init; }

    public double TokensPerSecond => MeasureMs > 0 ? Steps / (MeasureMs / 1000.0) : 0;
    public long AllocatedBytesPerToken => Steps > 0 ? AllocatedBytes / Steps : 0;
}

/// <summary>
/// The first real Studio harness: load a model, run a deterministic greedy
/// window, and write the measurement as a normal run stream. It deliberately
/// does not depend on the UI, so the same job can run from the CLI or a future
/// Blazor host.
/// </summary>
public static class ModelBenchJob
{
    public const string Kind = "model-bench";
    public const string GeneratorName = "StandardGeneratorBuilder";
    public const string CacheName = "KVCacherBuilder";

    public static async Task<ModelBenchResult> RunAsync(
        ModelBenchRequest request,
        RunHandle run,
        RunEventWriter events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);
        if (request.WarmupTokens < 1) throw new ArgumentOutOfRangeException(nameof(request), "WarmupTokens must be positive.");
        if (request.MeasureTokens < 1) throw new ArgumentOutOfRangeException(nameof(request), "MeasureTokens must be positive.");

        var total = Stopwatch.StartNew();
        await events.WriteAsync(new RunStarted
        {
            RunId = run.RunId,
            Kind = Kind,
            Subject = Path.GetFileName(request.ModelPath),
            ModelPath = Path.GetFullPath(request.ModelPath),
            Prompt = request.Prompt,
            WarmupTokens = request.WarmupTokens,
            MeasureTokens = request.MeasureTokens,
            InferenceVersion = typeof(SharpMind.Inference.Chat.IChatSession).Assembly.GetName().Version?.ToString(),
            BaseModelHash = File.Exists(request.ModelPath) ? Fingerprint(request.ModelPath) : null,
            Generator = request.GeneratorType is { } generatorType
                ? GeneratorFactory.DisplayName(generatorType)
                : GeneratorName,
            Cache = request.CacheType?.Name ?? CacheName,
            Device = DeviceDescription(),
            AtUtc = DateTimeOffset.UtcNow,
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(request.ModelPath))
                throw new FileNotFoundException("Model file was not found.", request.ModelPath);

            ModelFormat format = Path.GetExtension(request.ModelPath)
                .Equals(".smm", StringComparison.OrdinalIgnoreCase)
                ? ModelFormat.Smm
                : ModelFormat.Gguf;
            var metaHelper = ModelFormatHelpers.GetModelMetaHelperFor(format);
            metaHelper.Load(request.ModelPath, null, out _, out ModelConfig config, out Tokenizer? tokenizer);
            if (tokenizer is null)
                throw new NotSupportedException("The model has no embedded tokenizer; Studio bench needs one.");

            var sharpConfig = config.ForModel();
            var mapping = sharpConfig.ToJigSawMapping(null, null);
            using var weights = ModelFactory.CreateWeights(
                config, sharpConfig, QuantizationFactory.Create(mapping), request.ModelPath,
                request.FullLoad ? LoadMode.Full : LoadMode.Streaming,
                quantizedResident: request.FullLoad,
                maxParallelLoadDegree: 1);
            weights.InitializeWeights(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            using var model = ModelFactory.CreateTransformer(weights, sharpConfig, mapping);
            using IDisposable generator = GeneratorFactory.Create(
                request.GeneratorType ?? typeof(StandardGeneratorBuilder<>),
                request.CacheType ?? typeof(KVCacherBuilder),
                model, tokenizer);

            var sampling = new SamplingConfig { Temperature = 0f, TopK = 1 };
            var generation = new GenerationConfig
            {
                MaxNewTokens = request.WarmupTokens + request.MeasureTokens,
                Stream = true,
                RepetitionPenalty = 1.0f,
            };

            int consumed = 0;
            long measureStart = 0;
            long previousStamp = 0;
            long allocStart = 0;
            var text = new System.Text.StringBuilder();

            await foreach (var fragment in GeneratorFactory
                .GenerateAsync(generator, request.Prompt, sampling, generation, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                consumed++;
                if (consumed == request.WarmupTokens)
                {
                    measureStart = Stopwatch.GetTimestamp();
                    previousStamp = measureStart;
                    allocStart = GC.GetTotalAllocatedBytes();
                }
                else if (consumed > request.WarmupTokens)
                {
                    long now = Stopwatch.GetTimestamp();
                    double ms = (now - previousStamp) * 1000.0 / Stopwatch.Frequency;
                    previousStamp = now;
                    int step = consumed - request.WarmupTokens;
                    text.Append(fragment);
                    await events.WriteAsync(new StepLogged
                    {
                        Step = step,
                        TokensThisStep = 1,
                        TokensSeen = consumed,
                        TokensPerSecond = ms > 0 ? (float)(1000.0 / ms) : 0f,
                        MemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
                        AtUtc = DateTimeOffset.UtcNow,
                    }, cancellationToken).ConfigureAwait(false);
                }

                if (consumed == request.WarmupTokens + request.MeasureTokens) break;
            }

            long end = Stopwatch.GetTimestamp();
            if (measureStart == 0 || consumed <= request.WarmupTokens)
                throw new InvalidOperationException(
                    $"Generation ended before the {request.WarmupTokens}-token warm-up completed.");

            int steps = consumed - request.WarmupTokens;
            double measureMs = (end - measureStart) * 1000.0 / Stopwatch.Frequency;
            long allocated = GC.GetTotalAllocatedBytes() - allocStart;
            int[] ids = GeneratorFactory.CurrentGeneratedIds(generator) is { } generated
                ? [.. generated.Skip(request.WarmupTokens).Take(steps)]
                : [];

            await events.WriteAsync(new ProbeAnswered
            {
                Step = steps,
                ProbeId = "greedy-0",
                Text = text.ToString(),
                AtUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

            await events.WriteAsync(new RunFinished
            {
                Status = "succeeded",
                DurationMs = total.Elapsed.TotalMilliseconds,
                AtUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

            return new ModelBenchResult
            {
                RunId = run.RunId,
                Text = text.ToString(),
                GeneratedIds = ids,
                Steps = steps,
                MeasureMs = measureMs,
                AllocatedBytes = allocated,
            };
        }
        catch (OperationCanceledException)
        {
            await events.WriteAsync(new RunFinished
            {
                Status = "cancelled",
                DurationMs = total.Elapsed.TotalMilliseconds,
                AtUtc = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await events.WriteAsync(new RunFinished
            {
                Status = "failed",
                DurationMs = total.Elapsed.TotalMilliseconds,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                AtUtc = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static string DeviceDescription()
    {
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string os = RuntimeInformation.OSDescription.Trim();
        return $"{os} / {arch} / {Environment.ProcessorCount} logical CPU";
    }

    public static string FingerprintFor(string path) => Fingerprint(path);

    private static string Fingerprint(string path)
    {
        const long hashLimit = 1L << 30;
        var info = new FileInfo(path);
        if (info.Length > hashLimit)
            return $"size:{info.Length}";

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}








