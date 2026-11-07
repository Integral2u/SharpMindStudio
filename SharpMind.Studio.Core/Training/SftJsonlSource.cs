using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SharpMind.Data.Records;
using SharpMind.Data.Sources;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// The chat-record layouts recognised by <see cref="SftJsonlSource"/>. Auto
/// inspects every line in priority order, so a mixed file still works.
/// </summary>
public enum SftJsonlLayout
{
    Auto,
    Messages,
    Conversations,
    PromptResponse,
    InstructionOutput,
}

/// <summary>
/// Streams supervised fine-tuning examples from JSONL chat datasets. Each line
/// becomes a <see cref="DataRecord"/> with <see cref="DataRecord.Turns"/>
/// populated, which is what <c>ConversationTokenizer</c> needs to mask
/// assistant-only loss.
///
/// Supported shapes (auto-detected, or fixed through <c>format</c>):
///   * <c>{"messages":[{"role":"user","content":"..."},...]}</c> — OpenAI,
///     including content arrays with text parts.
///   * <c>{"conversations":[{"from":"human","value":"..."},...]}</c> — ShareGPT
///     and FuseChat-style data.
///   * <c>{"prompt":"...","response":"..."}</c> (also completion/output/answer).
///   * <c>{"instruction":"...","input":"...","output":"..."}</c>.
///
/// Blank or malformed lines, records without an assistant turn, and records
/// whose assistant content is empty are skipped and counted in
/// <see cref="SkippedRecords"/>.
/// </summary>
public sealed class SftJsonlSource : IRecordSource
{
    private readonly string[] _paths;
    private readonly SftJsonlLayout _layout;
    private long _skipped;
    private long _yielded;

    public SftJsonlSource(string path, string? format = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _layout = ParseLayout(format);
        _paths = GlobResolver.Resolve(path);
        if (_paths.Length == 0)
            throw new FileNotFoundException($"No files matched: {path}");
    }

    public SftJsonlSource(IEnumerable<string> paths, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _layout = ParseLayout(format);
        _paths = GlobResolver.ResolveMany(paths);
        if (_paths.Length == 0)
            throw new ArgumentException("Path list must not be empty.", nameof(paths));
    }

    public long? EstimatedCount => null;

    /// <summary>Number of files matched by the configured path or glob.</summary>
    public int FileCount => _paths.Length;

    /// <summary>The resolved file paths, in read order.</summary>
    public IReadOnlyList<string> Paths => _paths;

    public string Description =>
        _paths.Length == 1
            ? $"SftJsonl({Path.GetFileName(_paths[0])}, {_layout})"
            : $"SftJsonl({_paths.Length} files, {_layout})";

    /// <summary>Lines read but rejected as malformed or conversation-less.</summary>
    public long SkippedRecords => Volatile.Read(ref _skipped);

    /// <summary>Conversation records produced so far.</summary>
    public long YieldedRecords => Volatile.Read(ref _yielded);

    public async IAsyncEnumerable<DataRecord> ReadRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new JsonDocumentOptions { AllowTrailingCommas = true };

        foreach (string path in _paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65_536, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                DataRecord? record = ParseLine(line, path, options);
                if (record is null)
                {
                    Interlocked.Increment(ref _skipped);
                    continue;
                }

                Interlocked.Increment(ref _yielded);
                yield return record;
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private DataRecord? ParseLine(string line, string path, JsonDocumentOptions options)
    {
        try
        {
            using var document = JsonDocument.Parse(line, options);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            List<ConversationTurn>? turns;
            SftJsonlLayout usedLayout = _layout;
            if (_layout == SftJsonlLayout.Auto)
            {
                turns = ReadMessages(root);
                if (turns is not null)
                {
                    usedLayout = SftJsonlLayout.Messages;
                }
                else
                {
                    turns = ReadConversations(root);
                    if (turns is not null)
                    {
                        usedLayout = SftJsonlLayout.Conversations;
                    }
                    else
                    {
                        turns = ReadPromptResponse(root);
                        if (turns is not null)
                        {
                            usedLayout = SftJsonlLayout.PromptResponse;
                        }
                        else
                        {
                            turns = ReadInstructionOutput(root);
                            if (turns is not null) usedLayout = SftJsonlLayout.InstructionOutput;
                        }
                    }
                }
            }
            else
            {
                turns = _layout switch
                {
                    SftJsonlLayout.Messages => ReadMessages(root),
                    SftJsonlLayout.Conversations => ReadConversations(root),
                    SftJsonlLayout.PromptResponse => ReadPromptResponse(root),
                    SftJsonlLayout.InstructionOutput => ReadInstructionOutput(root),
                    _ => null,
                };
            }

            if (turns is null || turns.Count == 0) return null;
            if (!turns.Any(turn => IsAssistant(turn.Role) && !string.IsNullOrWhiteSpace(turn.Content)))
                return null;

            // Keep the transcript deterministic and useful to text-only stages.
            string text = string.Join('\n', turns.Select(turn => $"{turn.Role}: {turn.Content}"));
            return new DataRecord
            {
                Text = text,
                Turns = turns,
                Tags = [$"layout:{LayoutName(usedLayout)}"],
                Source = path,
                GroupId = ReadGroupId(root),
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A JSON value with the wrong kind (e.g. content is a number).
            return null;
        }
    }

    private static List<ConversationTurn>? ReadMessages(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out JsonElement messages)
            || messages.ValueKind != JsonValueKind.Array)
            return null;

        List<ConversationTurn> turns = [];
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) continue;
            if (!message.TryGetProperty("role", out JsonElement roleElement)
                || roleElement.ValueKind != JsonValueKind.String)
                continue;

            string role = NormalizeRole(roleElement.GetString() ?? "");
            string? content = ReadContent(message);
            if (string.IsNullOrWhiteSpace(content)) continue;
            turns.Add(new ConversationTurn(role, content));
        }

        return turns.Count > 0 ? turns : null;
    }

    private static List<ConversationTurn>? ReadConversations(JsonElement root)
    {
        JsonElement conversations;
        if (!root.TryGetProperty("conversations", out conversations)
            && !root.TryGetProperty("conversation", out conversations))
            return null;
        if (conversations.ValueKind != JsonValueKind.Array) return null;

        List<ConversationTurn> turns = [];
        foreach (JsonElement entry in conversations.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            string? role = ReadString(entry, "from") ?? ReadString(entry, "role");
            string? content = ReadString(entry, "value") ?? ReadString(entry, "content");
            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(content)) continue;
            turns.Add(new ConversationTurn(NormalizeRole(role), content));
        }

        return turns.Count > 0 ? turns : null;
    }

    private static List<ConversationTurn>? ReadPromptResponse(JsonElement root)
    {
        string? prompt = ReadString(root, "prompt") ?? ReadString(root, "question") ?? ReadString(root, "query");
        string? response = ReadString(root, "response") ?? ReadString(root, "completion")
            ?? ReadString(root, "output") ?? ReadString(root, "answer");
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(response)) return null;

        List<ConversationTurn> turns = [];
        if (ReadString(root, "system") is { Length: > 0 } system)
            turns.Add(new ConversationTurn("system", system));
        turns.Add(new ConversationTurn("user", prompt));
        turns.Add(new ConversationTurn("assistant", response));
        return turns;
    }

    private static List<ConversationTurn>? ReadInstructionOutput(JsonElement root)
    {
        string? instruction = ReadString(root, "instruction");
        string? output = ReadString(root, "output") ?? ReadString(root, "response")
            ?? ReadString(root, "completion") ?? ReadString(root, "answer");
        if (string.IsNullOrWhiteSpace(instruction) || string.IsNullOrWhiteSpace(output)) return null;

        string user = instruction;
        string? input = ReadString(root, "input") ?? ReadString(root, "context");
        if (!string.IsNullOrWhiteSpace(input)) user = $"{user}\n\n{input}";

        List<ConversationTurn> turns = [];
        if (ReadString(root, "system") is { Length: > 0 } system)
            turns.Add(new ConversationTurn("system", system));
        turns.Add(new ConversationTurn("user", user));
        turns.Add(new ConversationTurn("assistant", output));
        return turns;
    }

    private static string? ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content)) return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind != JsonValueKind.Array) return null;

        var builder = new StringBuilder();
        foreach (JsonElement part in content.EnumerateArray())
        {
            string? text = part.ValueKind switch
            {
                JsonValueKind.String => part.GetString(),
                JsonValueKind.Object => ReadString(part, "text") ?? ReadString(part, "content"),
                _ => null,
            };
            if (string.IsNullOrEmpty(text)) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(text);
        }
        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Data Factory (and similar exporters) write <c>source</c> plus a document
    /// <c>id</c>; every variant of one document keeps those two values. Group on
    /// them so a held-out split cannot put a rewrite of a training document on
    /// the exam side. Files without that provenance stay ungrouped.
    /// </summary>
    private static string? ReadGroupId(JsonElement root)
    {
        string? source = ReadString(root, "source");
        if (string.IsNullOrWhiteSpace(source)) return null;

        string? id = root.TryGetProperty("id", out JsonElement idElement)
            ? idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.GetRawText(),
                _ => null,
            }
            : null;

        return string.IsNullOrWhiteSpace(id) ? null : $"{source.Trim()}#{id.Trim()}";
    }

    private static string NormalizeRole(string role) => role.Trim().ToLowerInvariant() switch
    {
        "human" or "user" => "user",
        "gpt" or "assistant" or "model" or "agent" or "bot" => "assistant",
        "system" => "system",
        "tool" or "function" => "tool",
        var other => other,
    };

    private static bool IsAssistant(string role)
        => role.Equals("assistant", StringComparison.OrdinalIgnoreCase);

    private static SftJsonlLayout ParseLayout(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "messages" or "openai" => SftJsonlLayout.Messages,
        "conversations" or "sharegpt" or "fusechat" => SftJsonlLayout.Conversations,
        "prompt-response" or "promptresponse" or "prompt" => SftJsonlLayout.PromptResponse,
        "instruction-output" or "instructionoutput" or "instruction" or "alpaca" => SftJsonlLayout.InstructionOutput,
        _ => SftJsonlLayout.Auto,
    };

    private static string LayoutName(SftJsonlLayout layout) => layout switch
    {
        SftJsonlLayout.Messages => "messages",
        SftJsonlLayout.Conversations => "conversations",
        SftJsonlLayout.PromptResponse => "prompt/response",
        SftJsonlLayout.InstructionOutput => "instruction/output",
        _ => "auto",
    };
}

/// <summary>Filters a record source with a predicate (used for the deterministic splits).</summary>
public sealed class WhereRecordSource(
    IRecordSource inner,
    Func<DataRecord, bool> predicate,
    string? label = null) : IRecordSource
{
    private readonly IRecordSource _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<DataRecord, bool> _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

    public long? EstimatedCount => null;

    public string Description => label is { Length: > 0 }
        ? $"{_inner.Description} [{label}]"
        : _inner.Description;

    public async IAsyncEnumerable<DataRecord> ReadRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (DataRecord record in _inner.ReadRecordsAsync(cancellationToken))
        {
            if (_predicate(record)) yield return record;
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>Concatenates record sources in order (one per configured job source).</summary>
public sealed class CompositeRecordSource(IEnumerable<IRecordSource> sources) : IRecordSource
{
    private readonly IRecordSource[] _sources = [.. sources];

    public long? EstimatedCount => null;

    public string Description => $"CompositeRecords({string.Join(", ", _sources.Select(source => source.Description))})";

    public async IAsyncEnumerable<DataRecord> ReadRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (IRecordSource source in _sources)
        {
            await foreach (DataRecord record in source.ReadRecordsAsync(cancellationToken))
                yield return record;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IRecordSource source in _sources)
            await source.DisposeAsync();
    }
}

/// <summary>
/// The merged SFT record source plus the underlying JSONL readers, so a preflight
/// can report resolved files and skipped lines after reading the stream.
/// </summary>
public sealed record SftRecordSourceSet(
    IRecordSource Records,
    IReadOnlyList<SftJsonlSource> JsonlSources);

