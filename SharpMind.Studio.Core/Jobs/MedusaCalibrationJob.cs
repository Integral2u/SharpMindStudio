using System.Diagnostics;
using System.Threading.Channels;
using SharpMind.Core.Quantization;
using SharpMind.Model;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Model.Layers;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.Runs;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Jobs;

public sealed record MedusaCalibrationRequest
{
    public required string ModelPath { get; init; }
    public required string HeadsPath { get; init; }
    public string Prompt { get; init; } = "The capital of France is";
    public int NumHeads { get; init; } = 3;
    public int Samples { get; init; } = 16;
    public int Steps { get; init; } = 10;
    public float LearningRate { get; init; } = 0.02f;
    public string? Fingerprint { get; init; }
}

public sealed record MedusaCalibrationOutcome(
    int Samples,
    int Steps,
    float FinalLoss,
    string HeadsPath);

/// <summary>
/// Trains Medusa draft heads against a model's own greedy continuation and
/// writes the sidecar that chat sessions load. Calibration can take minutes on
/// a large model, so it runs as its own cancellable run stream instead of
/// blocking session creation.
/// </summary>
public static class MedusaCalibrationJob
{
    public const string Kind = "medusa-calibrate";

    public static async Task<MedusaCalibrationOutcome> RunAsync(
        MedusaCalibrationRequest request,
        RunHandle run,
        RunEventWriter events,
        CancellationToken cancellationToken = default,
        IProgress<MedusaCalibrationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);
        if (request.Samples < 1) throw new ArgumentOutOfRangeException(nameof(request), "Samples must be positive.");
        if (request.Steps < 1) throw new ArgumentOutOfRangeException(nameof(request), "Steps must be positive.");
        if (request.NumHeads < 1) throw new ArgumentOutOfRangeException(nameof(request), "NumHeads must be positive.");
        if (string.IsNullOrWhiteSpace(request.HeadsPath)) throw new ArgumentException("A heads path is required.", nameof(request));

        var total = Stopwatch.StartNew();
        await events.WriteAsync(new RunStarted
        {
            RunId = run.RunId,
            Kind = Kind,
            Subject = Path.GetFileName(request.ModelPath),
            ModelPath = Path.GetFullPath(request.ModelPath),
            Prompt = request.Prompt,
            BaseModelHash = File.Exists(request.ModelPath)
                ? request.Fingerprint ?? ModelBenchJob.FingerprintFor(request.ModelPath)
                : request.Fingerprint,
            Generator = "MedusaGeneratorBuilder",
            Cache = "KVCacherBuilder",
            Device = ModelBenchJob.DeviceDescription(),
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
                throw new NotSupportedException("The model has no embedded tokenizer; Medusa calibration needs one.");

            var sharpConfig = config.ForModel();
            var mapping = sharpConfig.ToJigSawMapping(null, null);
            using var weights = ModelFactory.CreateWeights(
                config, sharpConfig, QuantizationFactory.Create(mapping), request.ModelPath,
                LoadMode.Full, quantizedResident: false, maxParallelLoadDegree: 1);
            weights.InitializeWeights(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            using var model = ModelFactory.CreateTransformer(weights, sharpConfig, mapping);
            using var heads = new MedusaHeads(
                request.NumHeads,
                config.HiddenDim,
                config.VocabSize,
                model.LmHead ?? model.EmbeddingWeight,
                model.RawEmbedding,
                model.RawEmbeddingDtype,
                model.QOps);

            int[] seed = tokenizer.Encode(request.Prompt, addBos: true, addEos: false);

            // SelfCalibrate reports from its calling thread; pump the reports to
            // the run stream without blocking the training loop.
            var channel = Channel.CreateUnbounded<MedusaCalibrationProgress>();
            Task pump = Task.Run(async () =>
            {
                await foreach (var progress in channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await events.WriteAsync(new CalibrationProgress
                    {
                        Phase = progress.Phase.ToString(),
                        Completed = progress.Completed,
                        Total = progress.Total,
                        Loss = progress.Loss,
                        AtUtc = DateTimeOffset.UtcNow,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            MedusaCalibrationResult calibration;
            try
            {
                calibration = heads.SelfCalibrate(
                    model,
                    numSamples: request.Samples,
                    trainingSteps: request.Steps,
                    learningRate: request.LearningRate,
                    seedTokens: seed,
                    cancellationToken: cancellationToken,
                    progress: new CallbackProgress<MedusaCalibrationProgress>(value =>
                    {
                        channel.Writer.TryWrite(value);
                        progress?.Report(value);
                    }));
            }
            finally
            {
                channel.Writer.Complete();
                await pump.ConfigureAwait(false);
            }

            string headsPath = Path.GetFullPath(request.HeadsPath);
            string? directory = Path.GetDirectoryName(headsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Chat loads with the cheap name:size fingerprint, so the sidecar
            // must carry the same value or every session would reject it.
            MedusaHeadFile.Save(
                headsPath, heads, calibration,
                request.Fingerprint ?? StudioMedusaPaths.FingerprintFor(request.ModelPath));

            await events.WriteAsync(new RunFinished
            {
                Status = "succeeded",
                DurationMs = total.Elapsed.TotalMilliseconds,
                AtUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

            return new MedusaCalibrationOutcome(
                calibration.Samples, calibration.Steps, calibration.FinalLoss, headsPath);
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

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
