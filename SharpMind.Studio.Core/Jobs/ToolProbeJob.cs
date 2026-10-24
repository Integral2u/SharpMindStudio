using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using SharpMind.Core;
using SharpMind.Core.Quantization;
using SharpMind.Inference;
using SharpMind.Inference.Agent;
using SharpMind.Inference.Chat;
using SharpMind.Model;
using SharpMind.Model.Config;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Runs;
using SharpMind.Tokenization;

namespace SharpMind.Studio.Core.Jobs;

public sealed record ToolProbeRequest
{
    public required string ModelPath { get; init; }
    public string Prompt { get; init; } =
        "can you show me three options to choose from. Red, Green or Blue";
    public int MaxNewTokens { get; init; } = 96;
    public bool FullLoad { get; init; } = true;
    public Type? GeneratorType { get; init; }
    public Type? CacheType { get; init; }
}

public sealed record ToolProbeResult
{
    public required string RunId { get; init; }
    public required bool Passed { get; init; }
    public required bool ToolCalled { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public required string Response { get; init; }
}

/// <summary>
/// Tool-call canary: advertises the same option-selection tool the CUI uses and
/// checks that the model invokes it with the three requested choices.
/// </summary>
public static class ToolProbeJob
{
    public const string Kind = "tool-probe";

    public static async Task<ToolProbeResult> RunAsync(
        ToolProbeRequest request,
        RunHandle run,
        RunEventWriter events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);

        var total = Stopwatch.StartNew();
        await events.WriteAsync(new RunStarted
        {
            RunId = run.RunId,
            Kind = Kind,
            Subject = Path.GetFileName(request.ModelPath),
            ModelPath = Path.GetFullPath(request.ModelPath),
            Prompt = request.Prompt,
            InferenceVersion = typeof(IChatSession).Assembly.GetName().Version?.ToString(),
            BaseModelHash = File.Exists(request.ModelPath) ? ModelBenchJob.FingerprintFor(request.ModelPath) : null,
            Generator = request.GeneratorType is { } generatorType
                ? GeneratorFactory.DisplayName(generatorType)
                : ModelBenchJob.GeneratorName,
            Cache = request.CacheType?.Name ?? ModelBenchJob.CacheName,
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
            metaHelper.Load(request.ModelPath, null, out ModelMetaData meta, out ModelConfig config, out Tokenizer? tokenizer);
            if (tokenizer is null)
                throw new NotSupportedException("The model has no embedded tokenizer; tool probe needs one.");

            var sharpConfig = config.ForModel();
            var mapping = sharpConfig.ToJigSawMapping(null, null);
            using var weights = ModelFactory.CreateWeights(
                config, sharpConfig, QuantizationFactory.Create(mapping), request.ModelPath,
                request.FullLoad ? LoadMode.Full : LoadMode.Streaming,
                quantizedResident: request.FullLoad,
                maxParallelLoadDegree: 1);
            weights.InitializeWeights();
            using var model = ModelFactory.CreateTransformer(weights, sharpConfig, mapping);

            var probe = new OptionSelectionProbe();
            var agent = new AgentBuilder("StudioToolProbe", new SamplingConfig { Temperature = 0f, TopK = 1 });
            agent.WithTools(probe);

            await using IChatSession session = ChatSessionFactory.CreateChatSession(
                request.GeneratorType ?? typeof(StandardGeneratorBuilder<>),
                request.CacheType ?? typeof(KVCacherBuilder),
                model, tokenizer, meta, agentBuilder: agent, seed: 1234);
            session.MaxNewTokens = Math.Max(1, request.MaxNewTokens);
            session.Temperature = 0f;
            session.TopK = 1;
            session.RepetitionPenalty = 1.0f;
            session.MaxToolCallsPerTurn = 2;
            session.ProcessToolRequest = (_, _, _) =>
                Task.FromResult(ToolRequestResult.ReturnToCaller());
            session.InitializeChat();

            var response = new System.Text.StringBuilder();
            JsonObject? toolCall = null;
            string? error = null;
            await foreach (var entry in session.GetResponseStreamAsync(request.Prompt, ct: cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(entry.Error)) error = entry.Error;
                if (entry.Status == ChatStatus.Responding && entry.Token is not null)
                    response.Append(entry.Token);
                if (entry.Status == ChatStatus.ToolCall && entry.ToolCall is not null)
                    toolCall = entry.ToolCall;
            }

            string? toolName = toolCall?["tool"]?.GetValue<string>();
            List<string> options = [];
            if (toolCall?["arguments"] is JsonObject arguments &&
                arguments["options"] is JsonArray values)
            {
                options = [.. values
                    .Select(value => value?.GetValue<string>() ?? "")
                    .Where(value => value.Length > 0)];
            }

            bool toolCalled = string.Equals(
                toolName, nameof(OptionSelectionProbe.UIShowOptionSelection), StringComparison.Ordinal);
            bool expected = toolCalled &&
                options.Count == 3 &&
                options.Any(value => value.Contains("red", StringComparison.OrdinalIgnoreCase)) &&
                options.Any(value => value.Contains("green", StringComparison.OrdinalIgnoreCase)) &&
                options.Any(value => value.Contains("blue", StringComparison.OrdinalIgnoreCase));

            string callJson = toolCall?.ToJsonString() ?? response.ToString();
            string summary = $"tool={toolName ?? "none"}; options=[{string.Join(", ", options)}]; call={callJson}";
            await events.WriteAsync(new ProbeAnswered
            {
                Step = 1,
                ProbeId = "tool-call-option-selection",
                Text = summary,
                AtUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

            await events.WriteAsync(new RunFinished
            {
                Status = expected ? "succeeded" : "failed",
                DurationMs = total.Elapsed.TotalMilliseconds,
                Error = expected ? null : error ?? "The model did not emit a valid UIShowOptionSelection call with Red, Green and Blue.",
                AtUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

            return new ToolProbeResult
            {
                RunId = run.RunId,
                Passed = expected,
                ToolCalled = toolCalled,
                Options = options,
                Response = callJson,
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

    private sealed class OptionSelectionProbe
    {
        public bool Called { get; private set; }
        public List<string> Options { get; private set; } = [];

        [ToolDesc("Shows the person a list of options to choose from, optionally letting them type their own answer instead. Use this whenever you want the person to pick between a small number of concrete choices.")]
        public Task<string> UIShowOptionSelection(
            [ToolDesc("A short question or instruction shown above the option list.")] string prompt,
            [ToolDesc("The list of choices to present, each shown as a selectable option.")] List<string> options,
            [ToolDesc("Whether to also let the person type their own answer instead of picking a listed option.")] bool allowFreeText = false)
        {
            Called = true;
            Options = options ?? [];
            string selected = Options.FirstOrDefault(
                value => value.Contains("blue", StringComparison.OrdinalIgnoreCase))
                ?? Options.FirstOrDefault()
                ?? "Blue";
            return Task.FromResult(selected);
        }
    }
}







