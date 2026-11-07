using System.Text.Json.Nodes;
using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.Compute;

namespace SharpMind.Studio.Services.Chat;

/// <summary>
/// SharpMind Compute hosts expose plain OpenAI chat completions and are
/// inference-only, so there is no tool loop here.
/// </summary>
public sealed class ComputeChatBackend(ComputeProviderService compute) : IChatBackend
{
    public string Provider => "compute";

    public string? Validate(ChatSessionDocument document)
    {
        ComputeProvider? provider = compute.Resolve(document.RemoteProvider);
        if (provider is null || !provider.IsUsable)
            return "The Compute host is not configured. Add its URL and token in Settings > Providers.";

        return ResolveModel(document, provider) is { Length: > 0 }
            ? null
            : "The Compute host has no model. Upload one in Settings > Providers.";
    }

    public async Task RunTurnAsync(
        ChatBackendContext context,
        CancellationToken cancellationToken)
    {
        ChatSessionDocument document = context.Document;
        ComputeProvider provider = compute.Resolve(document.RemoteProvider)
            ?? throw new InvalidOperationException("The Compute host was not found.");
        string model = ResolveModel(document, provider)
            ?? throw new InvalidOperationException("The Compute host has no model.");

        var messages = new List<JsonObject>();
        if (!string.IsNullOrWhiteSpace(document.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = document.SystemPrompt,
            });
        }

        foreach (ChatMessage message in document.Messages.Take(document.Messages.Count - 1))
        {
            if (message.Role == ChatRole.System) continue;
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            string role = message.Role switch
            {
                ChatRole.Agent => "assistant",
                ChatRole.Tool => "assistant",
                _ => "user",
            };
            messages.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = message.Content,
            });
        }

        ChatMessage assistant = context.Assistant;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastNotify = TimeSpan.Zero;
        int? promptTokens = null;
        int? completionTokens = null;

        await foreach (ComputeStreamEvent entry in compute.Client.StreamChatAsync(
            provider,
            model,
            messages,
            document.Overrides.Temperature,
            document.Overrides.TopP,
            document.Overrides.MaxNewTokens,
            cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(entry.Error))
                throw new ComputeException(entry.Error);

            if (entry.PromptTokens is int prompt) promptTokens = prompt;
            if (entry.CompletionTokens is int completion) completionTokens = completion;

            if (!string.IsNullOrEmpty(entry.Delta))
            {
                assistant.Content += entry.Delta;
                context.SetStatus("Writing...");
                if (lastNotify == TimeSpan.Zero
                    || stopwatch.Elapsed - lastNotify > TimeSpan.FromMilliseconds(40))
                {
                    lastNotify = stopwatch.Elapsed;
                    context.Notify();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(assistant.Content) && assistant.Metadata is null)
            document.Messages.Remove(assistant);

        double? tps = null;
        if (completionTokens is int generated && stopwatch.Elapsed.TotalSeconds > 0.1)
            tps = generated / stopwatch.Elapsed.TotalSeconds;
        else if (assistant.Content.Length > 0 && stopwatch.Elapsed.TotalSeconds > 0.1)
            tps = (assistant.Content.Length / 4.0) / stopwatch.Elapsed.TotalSeconds;

        document.LastTokensPerSecond = tps;
        document.LastCost = null;
        document.LastPromptTokens = promptTokens;
        document.LastCompletionTokens = completionTokens;
        document.LastToolCalls = 0;
        context.SetStatus(tps is { } speed ? $"Ready. {speed:F1} tok/s." : "Ready.");
    }

    private static string? ResolveModel(ChatSessionDocument document, ComputeProvider provider) =>
        string.IsNullOrWhiteSpace(document.RemoteModel)
            ? provider.DefaultModel ?? provider.Models.FirstOrDefault()
            : document.RemoteModel;
}
