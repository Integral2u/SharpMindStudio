using System.Diagnostics;
using SharpMind.Core.Quantization;
using SharpMind.Model.Analysis;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Inference;

namespace SharpMind.Studio.Services;

/// <summary>
/// Runs the quantization lab: a tensor-level weight comparison that only opens the two GGUF
/// files, an optional forward-loss comparison that loads the two models sequentially, and a
/// real GGUF re-quantization that runs without loading a model.
/// </summary>
public sealed class StudioQuantizationLabService : IAsyncDisposable
{
    private readonly InferenceSettingsService _inference;
    private readonly StudioModelHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private LoadedStudioModel? _loaded;
    private bool _disposed;

    public StudioQuantizationLabService(InferenceSettingsService inference, StudioModelHost host)
    {
        _inference = inference;
        _host = host;
    }

    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Pick a reference and a candidate quantization.";
    public string? Error { get; private set; }
    public QuantizationComparisonReport? Report { get; private set; }
    public double ElapsedSeconds { get; private set; }

    public double? ReferenceLoss { get; private set; }
    public double? CandidateLoss { get; private set; }
    public int LossTokens { get; private set; }
    public double? LossDelta =>
        ReferenceLoss is { } reference && CandidateLoss is { } candidate ? candidate - reference : null;

    public bool IsQuantizing { get; private set; }
    public string? QuantizeStatus { get; private set; }
    public string? QuantizeError { get; private set; }
    public GgufQuantizeResult? QuantizeResult { get; private set; }

    public async Task RunAsync(
        string referencePath,
        string candidatePath,
        string? nameFilter,
        int maxTensors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            IsRunning = true;
            Error = null;
            Report = null;
            ElapsedSeconds = 0;
            _cts = new CancellationTokenSource();
            Status = "Comparing tensors...";
            Notify();

            CancellationToken token = _cts.Token;
            var watch = Stopwatch.StartNew();
            Report = await Task.Run(
                () => QuantizationAnalyzer.Compare(
                    referencePath, candidatePath, nameFilter, maxTensors,
                    progress: (done, total) =>
                    {
                        Status = $"Tensor {done}/{total}...";
                        Notify();
                    },
                    cancellationToken: token),
                token).ConfigureAwait(false);
            watch.Stop();
            ElapsedSeconds = watch.Elapsed.TotalSeconds;
            Status = $"Done: {Report.Compared} tensors in {ElapsedSeconds:F0}s.";
        }
        catch (OperationCanceledException)
        {
            Status = "Quantization comparison cancelled.";
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Status = "Quantization comparison failed.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            _gate.Release();
            Notify();
        }
    }

    /// <summary>
    /// Loads each model in turn, measures the next-token cross-entropy on the same
    /// calibration ids and releases it before loading the next one.
    /// </summary>
    public async Task RunLossAsync(
        ModelDescriptor reference,
        ModelDescriptor candidate,
        string prompt,
        int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            IsRunning = true;
            Error = null;
            ReferenceLoss = null;
            CandidateLoss = null;
            LossTokens = 0;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            QuantizationCompatibility compatibility =
                QuantizationAnalyzer.CheckModelCompatibility(reference.Path, candidate.Path);
            if (!compatibility.SameModel)
            {
                Error = compatibility.Note ??
                    "The reference and candidate are not the same model.";
                Status = "Pick the same model at two different quantizations.";
                return;
            }

            Status = $"Loading {reference.FileName}...";
            Notify();
            int[] ids = await MeasureAsync(reference, prompt, maxTokens, reference: true, null, token)
                .ConfigureAwait(false);
            Status = $"Loading {candidate.FileName}...";
            Notify();
            await MeasureAsync(candidate, prompt, maxTokens, reference: false, ids, token).ConfigureAwait(false);

            LossTokens = Math.Max(0, Math.Min(ids.Length, maxTokens) - 1);
            Status = $"Loss {ReferenceLoss:F4} -> {CandidateLoss:F4} (delta {LossDelta:+0.0000;-0.0000;0}).";
        }
        catch (OperationCanceledException)
        {
            Status = "Loss comparison cancelled.";
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Status = "Loss comparison failed.";
        }
        finally
        {
            if (_loaded is not null)
            {
                _host.Release(_loaded);
                _loaded = null;
            }
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            _gate.Release();
            Notify();
        }
    }

    /// <summary>
    /// Re-quantizes a GGUF to a fixed target, a per-role blend, or a byte budget.
    /// This only reads and writes the file, so it runs without loading a model.
    /// </summary>
    public async Task QuantizeAsync(
        ModelDescriptor source,
        string targetText,
        int budgetMegabytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            QuantizeError = "Actual quantization only supports GGUF models.";
            Notify();
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning || IsQuantizing) return;
            IsQuantizing = true;
            QuantizeError = null;
            QuantizeResult = null;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            (string label, GgufQuantOptions options) = BuildQuantizeOptions(
                targetText, budgetMegabytes);
            string destination = DefaultQuantizePath(source.Path, label, budgetMegabytes);
            QuantizeStatus = $"Quantizing to {label}...";
            Notify();

            GgufQuantizeResult result = await Task.Run(
                () => GgufQuantizer.Quantize(
                    source.Path, destination, options,
                    progress: (done, total) =>
                    {
                        QuantizeStatus = $"Tensor {done}/{total}...";
                        Notify();
                    },
                    cancellationToken: token),
                token).ConfigureAwait(false);

            QuantizeResult = result;
            QuantizeStatus =
                $"Wrote {result.Tensors} tensors ({result.Requantized} requantized, " +
                $"{result.Copied} copied) · {result.Bytes / (1024.0 * 1024.0):F1} MB.";
        }
        catch (OperationCanceledException)
        {
            QuantizeStatus = "Quantization cancelled.";
        }
        catch (Exception ex)
        {
            QuantizeError = $"{ex.GetType().Name}: {ex.Message}";
            QuantizeStatus = "Quantization failed.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsQuantizing = false;
            _gate.Release();
            Notify();
        }
    }

    private static (string Label, GgufQuantOptions Options) BuildQuantizeOptions(
        string targetText, int budgetMegabytes)
    {
        string target = targetText.Trim().ToLowerInvariant();
        long? budget = budgetMegabytes > 0
            ? budgetMegabytes * 1024L * 1024L
            : null;

        if (target == "blend")
        {
            return ("blend", new GgufQuantOptions
            {
                DefaultLevel = QuantDType.Q4_K,
                RoleLevels = new Dictionary<SmmTensorRole, QuantDType>
                {
                    [SmmTensorRole.Embedding] = QuantDType.Q8_K,
                    [SmmTensorRole.LmHead] = QuantDType.Q8_K,
                },
                TargetBytes = budget,
                Floor = QuantDType.Q4_K,
            });
        }

        QuantDType dtype = target switch
        {
            "q2k" => QuantDType.Q2_K,
            "q3k" => QuantDType.Q3_K,
            "q4k" => QuantDType.Q4_K,
            "q5k" => QuantDType.Q5_K,
            "q6k" => QuantDType.Q6_K,
            "q8k" => QuantDType.Q8_K,
            "q8_0" => QuantDType.Q8_0,
            "q4_0" => QuantDType.Q4_0,
            _ => QuantDType.F16,
        };
        QuantDType floor = dtype is QuantDType.Q2_K or QuantDType.Q3_K or QuantDType.Q4_K
            or QuantDType.Q5_K or QuantDType.Q6_K or QuantDType.Q8_K
            ? dtype
            : QuantDType.Q4_K;
        bool budgetable = dtype is QuantDType.Q2_K or QuantDType.Q3_K or QuantDType.Q4_K
            or QuantDType.Q5_K or QuantDType.Q6_K or QuantDType.Q8_K;
        return (target, new GgufQuantOptions
        {
            DefaultLevel = dtype,
            TargetBytes = budgetable ? budget : null,
            Floor = floor,
        });
    }

    private static string DefaultQuantizePath(
        string sourcePath, string label, int budgetMegabytes)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string suffix = budgetMegabytes > 0 ? $"{label}-{budgetMegabytes}mb" : label;
        return Path.Combine(directory, $"{name}-{suffix}.gguf");
    }

    private async Task<int[]> MeasureAsync(
        ModelDescriptor descriptor,
        string prompt,
        int maxTokens,
        bool reference,
        int[]? ids,
        CancellationToken token)
    {
        ResolvedInference resolved = _inference.Resolve(
            descriptor.Family.Id, descriptor.Path, session: null);
        _loaded = await _host.LoadAsync(descriptor, resolved, progress: null, token)
            .ConfigureAwait(false);
        ids ??= _loaded.Tokenizer.Encode(prompt, addBos: true, addEos: false);
        var analyzer = new LayerImportanceAnalyzer(_loaded.Model);
        double loss = await Task.Run(
            () => analyzer.MeasureLoss(ids, maxTokens, token), token).ConfigureAwait(false);
        if (reference) ReferenceLoss = loss;
        else CandidateLoss = loss;
        _host.Release(_loaded);
        _loaded = null;
        return ids;
    }

    public void Cancel() => _cts?.Cancel();

    private void Notify() => Changed?.Invoke();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        if (_loaded is not null)
        {
            _host.Release(_loaded);
            _loaded = null;
        }
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
