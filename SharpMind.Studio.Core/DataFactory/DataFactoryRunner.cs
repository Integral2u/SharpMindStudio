using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.DataFactory;

public sealed record DataFactoryRunProgress(
    int Processed,
    int Written,
    int Skipped,
    int ParseFailures,
    string? CurrentSource);

public sealed record DataFactoryRunResult(
    int Processed,
    int Written,
    int Skipped,
    int ParseFailures,
    string OutputPath,
    IReadOnlyList<string> Samples);

/// <summary>One provider-backed text generator for a Data Factory run.</summary>
public interface IDataFactoryGenerator : IAsyncDisposable
{
    string Description { get; }

    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        DataFactoryRecipe recipe,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a saved Data Factory recipe: read and clean the source, ask the provider
/// for one response per document (or variant), then write chat/text JSONL.
/// </summary>
public static class DataFactoryRunner
{
    public const int SampleCount = 5;

    public static async Task<DataFactoryRunResult> RunAsync(
        DataFactoryRecipe recipe,
        IDataFactoryGenerator generator,
        IProgress<DataFactoryRunProgress>? progress = null,
        int? maxRecords = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(generator);

        string outputPath = string.IsNullOrWhiteSpace(recipe.OutputPath)
            ? Path.Combine(SharpMind.Studio.Core.Storage.StudioPaths.Corpus, "factory-output.jsonl")
            : Path.GetFullPath(recipe.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        int limit = maxRecords ?? recipe.MaxRecords;
        if (limit <= 0) limit = int.MaxValue;
        int variants = Math.Clamp(recipe.Variants, 1, 10);

        int processed = 0;
        int written = 0;
        int skipped = 0;
        int parseFailures = 0;
        var samples = new List<string>(SampleCount);

        await using var stream = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 65_536, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.NewLine = "\n";

        await foreach (DataFactoryDocument document in DataFactoryInput
            .ReadAsync(recipe, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (written >= limit) break;
            processed++;

            string prompt = DataFactoryPrompt.Render(recipe.UserPromptTemplate, document);

            for (int variant = 0; variant < variants && written < limit; variant++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? response = await generator
                    .GenerateAsync(recipe.SystemPrompt, prompt, recipe, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(response))
                {
                    skipped++;
                    continue;
                }

                string? line;
                if (recipe.OutputFormat == DataFactoryOutputFormat.ChatJsonl)
                {
                    if (!DataFactoryOutput.TryParseChat(response, out string instruction, out string answer))
                    {
                        parseFailures++;
                        skipped++;
                        continue;
                    }
                    line = DataFactoryOutput.BuildChatLine(recipe, document, instruction, answer);
                }
                else
                {
                    line = DataFactoryOutput.BuildTextLine(document, response);
                }

                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                written++;
                if (samples.Count < SampleCount) samples.Add(line);
            }

            progress?.Report(new DataFactoryRunProgress(
                processed, written, skipped, parseFailures, document.Source));
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new DataFactoryRunResult(
            processed, written, skipped, parseFailures, outputPath, samples);
    }
}

/// <summary>Turns provider responses into the two supported JSONL shapes.</summary>
public static class DataFactoryOutput
{
    public static bool TryParseChat(string response, out string instruction, out string answer)
    {
        instruction = "";
        answer = "";

        string text = StripCodeFence(response.Trim());
        if (text.Length == 0) return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                string? parsedInstruction = ReadString(
                    document.RootElement, "instruction", "question", "prompt", "user");
                string? parsedAnswer = ReadString(
                    document.RootElement, "answer", "response", "output", "assistant");
                if (!string.IsNullOrWhiteSpace(parsedInstruction)
                    && !string.IsNullOrWhiteSpace(parsedAnswer))
                {
                    instruction = parsedInstruction.Trim();
                    answer = parsedAnswer.Trim();
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        Match question = Regex.Match(
            text,
            @"(?im)^\s*Q(?:uestion)?\s*[:.)-]\s*(?<value>.+?)\s*$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        Match answerMatch = Regex.Match(
            text,
            @"(?im)^\s*A(?:nswer)?\s*[:.)-]\s*(?<value>[\s\S]+)$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        if (question.Success && answerMatch.Success)
        {
            instruction = question.Groups["value"].Value.Trim();
            answer = answerMatch.Groups["value"].Value.Trim();
            return instruction.Length > 0 && answer.Length > 0;
        }

        return false;
    }

    public static string BuildChatLine(
        DataFactoryRecipe recipe,
        DataFactoryDocument document,
        string instruction,
        string answer)
    {
        var line = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = recipe.ChatSystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = instruction },
                new JsonObject { ["role"] = "assistant", ["content"] = answer }),
            ["source"] = document.Source,
            ["id"] = document.Index,
        };
        return line.ToJsonString();
    }

    public static string BuildTextLine(DataFactoryDocument document, string text)
    {
        var line = new JsonObject
        {
            ["text"] = text,
            ["source"] = document.Source,
            ["id"] = document.Index,
        };
        return line.ToJsonString();
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return text;
        string body = text[(firstNewline + 1)..];
        int closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing].Trim() : body.Trim();
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;
            string? text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }
}
