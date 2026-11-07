using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpMind.Studio.Core.OpenRouter;

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
    IReadOnlyList<string> Samples,
    bool Resumed = false);

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

        string recipeHash = DataFactoryRecipeHash.Compute(recipe);
        string partialPath = outputPath + ".partial";
        string checkpointPath = partialPath + ".json";

        DataFactoryCheckpoint? checkpoint = recipe.ResumePartial
            ? DataFactoryCheckpointStore.TryLoad(checkpointPath, recipeHash)
            : null;
        if (checkpoint is not null
            && (!File.Exists(partialPath)
                || new FileInfo(partialPath).Length < checkpoint.Bytes))
        {
            checkpoint = null;
        }

        if (checkpoint is null)
        {
            DataFactoryCheckpointStore.TryDelete(partialPath);
            DataFactoryCheckpointStore.TryDelete(checkpointPath);
        }

        bool resumed = checkpoint is not null;
        int processed = checkpoint?.Processed ?? 0;
        if (checkpoint is { LastId: < 0 }) processed = 0;
        int written = checkpoint?.Written ?? 0;
        int skipped = checkpoint?.Skipped ?? 0;
        int parseFailures = checkpoint?.ParseFailures ?? 0;
        int lastId = checkpoint?.LastId ?? -1;
        int lastVariant = checkpoint?.LastVariant ?? -1;
        var samples = new List<string>(SampleCount);

        await using (var stream = new FileStream(
            partialPath,
            resumed ? FileMode.Open : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 65_536,
            useAsync: true))
        await using (var writer = new StreamWriter(
            stream, new UTF8Encoding(false), bufferSize: 65_536, leaveOpen: true))
        {
            writer.NewLine = "\n";

            if (resumed)
            {
                stream.SetLength(Math.Min(stream.Length, checkpoint!.Bytes));
                stream.Seek(0, SeekOrigin.End);
            }

            try
            {
                await foreach (DataFactoryDocument document in DataFactoryInput
                    .ReadAsync(recipe, cancellationToken)
                    .WithCancellation(cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (written >= limit) break;
                    if (resumed && document.Index < checkpoint!.LastId) continue;

                    int startVariant = 0;
                    if (resumed && document.Index == checkpoint!.LastId)
                    {
                        startVariant = checkpoint.LastVariant + 1;
                    }
                    else
                    {
                        processed++;
                    }

                    string prompt = DataFactoryPrompt.Render(recipe.UserPromptTemplate, document);

                    for (int variant = startVariant; variant < variants && written < limit; variant++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string? response = await GenerateWithRetryAsync(
                            generator,
                            recipe.SystemPrompt,
                            prompt,
                            recipe,
                            cancellationToken).ConfigureAwait(false);

                        lastId = document.Index;
                        lastVariant = variant;

                        if (string.IsNullOrWhiteSpace(response))
                        {
                            skipped++;
                            await SaveProgressAsync(
                                writer, stream, checkpointPath, recipeHash,
                                processed, written, skipped, parseFailures,
                                lastId, lastVariant).ConfigureAwait(false);
                            continue;
                        }

                        string? line;
                        if (recipe.OutputFormat == DataFactoryOutputFormat.ChatJsonl)
                        {
                            if (!DataFactoryOutput.TryParseChat(
                                response, out string instruction, out string answer))
                            {
                                parseFailures++;
                                skipped++;
                                await SaveProgressAsync(
                                    writer, stream, checkpointPath, recipeHash,
                                    processed, written, skipped, parseFailures,
                                    lastId, lastVariant).ConfigureAwait(false);
                                continue;
                            }
                            line = DataFactoryOutput.BuildChatLine(
                                recipe, document, variant, instruction, answer);
                        }
                        else
                        {
                            line = DataFactoryOutput.BuildTextLine(document, variant, response);
                        }

                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                        written++;
                        if (samples.Count < SampleCount) samples.Add(line);
                        await SaveProgressAsync(
                            writer, stream, checkpointPath, recipeHash,
                            processed, written, skipped, parseFailures,
                            lastId, lastVariant).ConfigureAwait(false);
                    }

                    progress?.Report(new DataFactoryRunProgress(
                        processed, written, skipped, parseFailures, document.Source));
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    await SaveProgressAsync(
                        writer, stream, checkpointPath, recipeHash,
                        processed, written, skipped, parseFailures,
                        lastId, lastVariant).ConfigureAwait(false);
                }
                catch
                {
                    // Keep the original failure; the last good checkpoint stays.
                }
                throw;
            }
        }

        File.Move(partialPath, outputPath, overwrite: true);
        DataFactoryCheckpointStore.TryDelete(checkpointPath);

        return new DataFactoryRunResult(
            processed, written, skipped, parseFailures, outputPath, samples, resumed);
    }

    private static async Task<string> GenerateWithRetryAsync(
        IDataFactoryGenerator generator,
        string systemPrompt,
        string userPrompt,
        DataFactoryRecipe recipe,
        CancellationToken cancellationToken)
    {
        int attempts = Math.Max(1, recipe.RetryAttempts);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await generator
                    .GenerateAsync(systemPrompt, userPrompt, recipe, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < attempts && DataFactoryRetry.IsTransient(ex))
            {
                TimeSpan delay = DataFactoryRetry.Delay(ex, recipe.RetryDelayMs, attempt);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task SaveProgressAsync(
        StreamWriter writer,
        FileStream stream,
        string checkpointPath,
        string recipeHash,
        int processed,
        int written,
        int skipped,
        int parseFailures,
        int lastId,
        int lastVariant)
    {
        await writer.FlushAsync().ConfigureAwait(false);
        DataFactoryCheckpointStore.Save(
            checkpointPath,
            new DataFactoryCheckpoint(
                recipeHash,
                stream.Position,
                processed,
                written,
                skipped,
                parseFailures,
                lastId,
                lastVariant));
    }

    /// <summary>Progress marker for one output file; written atomically after every variant.</summary>
    internal sealed record DataFactoryCheckpoint(
        string RecipeHash,
        long Bytes,
        int Processed,
        int Written,
        int Skipped,
        int ParseFailures,
        int LastId,
        int LastVariant);

    internal static class DataFactoryCheckpointStore
    {
        public static DataFactoryCheckpoint? TryLoad(string path, string recipeHash)
        {
            try
            {
                if (!File.Exists(path)) return null;
                DataFactoryCheckpoint? checkpoint =
                    JsonSerializer.Deserialize<DataFactoryCheckpoint>(File.ReadAllText(path));
                if (checkpoint is null
                    || !string.Equals(checkpoint.RecipeHash, recipeHash, StringComparison.Ordinal)
                    || checkpoint.Bytes < 0
                    || checkpoint.LastId < -1
                    || checkpoint.LastVariant < -1)
                {
                    return null;
                }
                return checkpoint;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static void Save(string path, DataFactoryCheckpoint checkpoint)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint));
            File.Move(temp, path, overwrite: true);
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Identity of a run's inputs. Volatile fields (name, output path, retry
    /// settings) are excluded so a resumed run keeps its partial output, while a
    /// prompt, model, source or output-format change starts a fresh file.
    /// </summary>
    internal static class DataFactoryRecipeHash
    {
        public static string Compute(DataFactoryRecipe recipe)
        {
            var canonical = new
            {
                recipe.SourcePath,
                recipe.SourceFormat,
                recipe.TextField,
                recipe.StripHtml,
                recipe.NormalizeText,
                recipe.Deduplicate,
                recipe.MinChars,
                recipe.MaxChars,
                recipe.DropPattern,
                recipe.Provider,
                recipe.LocalModelPath,
                recipe.RemoteModel,
                recipe.SystemPrompt,
                recipe.UserPromptTemplate,
                recipe.OutputFormat,
                recipe.ChatSystemPrompt,
                recipe.Temperature,
                recipe.MaxTokens,
                recipe.Seed,
                recipe.Variants,
            };

            return Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
        }
    }

    internal static class DataFactoryRetry
    {
        public static bool IsTransient(Exception ex)
        {
            if (ex is OpenRouterException { StatusCode: int status })
                return status is 408 or 429 || status >= 500;

            return ex is HttpRequestException or TaskCanceledException or IOException;
        }

        public static TimeSpan Delay(Exception ex, int baseDelayMs, int attempt)
        {
            if (ex is OpenRouterException { RetryAfter: { } retryAfter }
                && retryAfter > TimeSpan.Zero)
            {
                return retryAfter > TimeSpan.FromMinutes(5)
                    ? TimeSpan.FromMinutes(5)
                    : retryAfter;
            }

            double milliseconds = Math.Min(
                30_000,
                Math.Max(0, baseDelayMs) * Math.Pow(2, Math.Max(0, attempt - 1)));
            return TimeSpan.FromMilliseconds(milliseconds);
        }
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
        int variant,
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
            ["variant"] = variant,
        };
        return line.ToJsonString();
    }

    public static string BuildTextLine(DataFactoryDocument document, int variant, string text)
    {
        var line = new JsonObject
        {
            ["text"] = text,
            ["source"] = document.Source,
            ["id"] = document.Index,
            ["variant"] = variant,
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
