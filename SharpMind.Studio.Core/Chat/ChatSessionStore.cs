using SharpMind.Inference.Chat;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Chat;

public sealed class ChatSessionStore
{
    public const string ChatFileName = "chat.json";
    public const string KvFileName = "kv.json";

    public ChatSessionStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public IReadOnlyList<ChatSessionSummary> List()
    {
        var sessions = new List<ChatSessionSummary>();
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            string file = Path.Combine(directory, ChatFileName);
            if (!File.Exists(file)) continue;

            try
            {
                ChatSessionDocument document = JsonFile.Read<ChatSessionDocument>(file);
                sessions.Add(Summarize(document, preview: null));
            }
            catch
            {
            }
        }

        return [.. sessions.OrderByDescending(session => session.UpdatedUtc)];
    }

    /// <summary>
    /// Finds sessions whose name or message text contains <paramref name="query"/>.
    /// The returned preview is the matching line, so the list shows why a session
    /// matched instead of only the last reply.
    /// </summary>
    public IReadOnlyList<ChatSessionSummary> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return List();
        string needle = query.Trim();
        var sessions = new List<ChatSessionSummary>();
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            string file = Path.Combine(directory, ChatFileName);
            if (!File.Exists(file)) continue;

            try
            {
                ChatSessionDocument document = JsonFile.Read<ChatSessionDocument>(file);
                string? snippet = FindSnippet(document, needle);
                bool nameMatches = document.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
                if (!nameMatches && snippet is null) continue;
                sessions.Add(Summarize(document, snippet));
            }
            catch
            {
            }
        }

        return [.. sessions.OrderByDescending(session => session.UpdatedUtc)];
    }

    private static ChatSessionSummary Summarize(ChatSessionDocument document, string? preview)
    {
        string text = preview
            ?? document.Messages.LastOrDefault(message =>
                message.Role == ChatRole.Agent && !string.IsNullOrWhiteSpace(message.Content))?.Content
            ?? document.Messages.LastOrDefault()?.Content
            ?? "";
        if (text.Length > 120) text = text[..120] + "...";

        return new ChatSessionSummary
        {
            Id = document.Id,
            Name = document.Name,
            ModelPath = document.ModelPath,
            Provider = document.Provider ?? "local",
            RemoteModel = document.RemoteModel,
            Mode = document.Mode,
            TunePreset = document.TunePreset,
            UpdatedUtc = document.UpdatedUtc,
            MessageCount = document.Messages.Count,
            Preview = text.ReplaceLineEndings(" "),
        };
    }

    private static string? FindSnippet(ChatSessionDocument document, string needle)
    {
        foreach (ChatMessage message in document.Messages)
        {
            if (string.IsNullOrEmpty(message.Content)) continue;
            int index = message.Content.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            int start = Math.Max(0, index - 40);
            int length = Math.Min(message.Content.Length - start, needle.Length + 80);
            string snippet = message.Content.Substring(start, length);
            return (start > 0 ? "..." : "") + snippet + (start + length < message.Content.Length ? "..." : "");
        }
        return null;
    }

    public ChatSessionDocument Create(string modelPath, string? familyId, string? familyName, string? inferenceVersion, string? systemPrompt, string mode = "chat", string? tunePreset = null, string? tuneTarget = null)
    {
        string id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..8]}";
        string name = $"{Path.GetFileNameWithoutExtension(modelPath)} chat";
        var document = new ChatSessionDocument
        {
            Id = id,
            Name = name,
            ModelPath = Path.GetFullPath(modelPath),
            ModelFingerprint = Fingerprint(modelPath),
            FamilyId = familyId,
            FamilyName = familyName,
            InferenceVersion = inferenceVersion,
            Mode = mode,
            TunePreset = tunePreset,
            TuneTarget = tuneTarget,
            SystemPrompt = systemPrompt,
            Messages = [],
        };

        Save(document);
        return document;
    }

    /// <summary>
    /// Creates a chat backed by an OpenRouter model. Remote chats keep the same
    /// document/history shape as local ones, so the transcript, rename, delete
    /// and session list all work unchanged. <see cref="ModelPath"/> is only a
    /// stable label; <see cref="ChatSessionDocument.RemoteModel"/> is the model
    /// id sent to OpenRouter.
    /// </summary>
    public ChatSessionDocument CreateRemote(
        string modelId,
        string? systemPrompt,
        string mode = "chat")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        string id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..8]}";
        string cleanModel = modelId.Trim();
        var document = new ChatSessionDocument
        {
            Id = id,
            Name = $"{cleanModel} chat",
            ModelPath = "openrouter",
            Provider = "openrouter",
            RemoteModel = cleanModel,
            FamilyId = "openrouter",
            FamilyName = "OpenRouter",
            InferenceVersion = typeof(IChatSession).Assembly.GetName().Version?.ToString(),
            Mode = mode,
            SystemPrompt = systemPrompt,
            Messages = [],
        };

        Save(document);
        return document;
    }

    /// <summary>
    /// Starts a chat backed by a SharpMind Compute host. The provider id is kept
    /// so reopening the chat can resolve the same host and token.
    /// </summary>
    public ChatSessionDocument CreateCompute(
        string providerId,
        string providerName,
        string modelId,
        string? systemPrompt,
        string mode = "chat")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        string id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..8]}";
        string cleanModel = modelId.Trim();
        var document = new ChatSessionDocument
        {
            Id = id,
            Name = $"{cleanModel} via {providerName}",
            ModelPath = "compute",
            Provider = "compute",
            RemoteProvider = providerId,
            RemoteModel = cleanModel,
            FamilyId = "compute",
            FamilyName = providerName,
            InferenceVersion = typeof(IChatSession).Assembly.GetName().Version?.ToString(),
            Mode = mode,
            SystemPrompt = systemPrompt,
            Messages = [],
        };

        Save(document);
        return document;
    }

    public ChatSessionDocument Open(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string directory = DirectoryFor(id);
        string file = Path.Combine(directory, ChatFileName);
        if (!File.Exists(file)) throw new FileNotFoundException($"Chat '{id}' was not found.", file);

        ChatSessionDocument document = JsonFile.Read<ChatSessionDocument>(file);
        if (document.Schema != ChatSessionDocument.CurrentSchema)
            throw new InvalidDataException($"Chat schema '{document.Schema}' is not supported.");

        document.Overrides ??= new();
        document.Messages ??= [];
        return document;
    }

    public void Save(ChatSessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.UpdatedUtc = DateTimeOffset.UtcNow;
        JsonFile.Write(Path.Combine(DirectoryFor(document.Id), ChatFileName), document);
    }

    public void Rename(string id, string name)
    {
        ChatSessionDocument document = Open(id);
        string clean = name.Trim();
        if (clean.Length == 0) return;
        document.Name = clean.Length <= 120 ? clean : clean[..120];
        Save(document);
    }

    public void Clear()
    {
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    public void SaveSnapshot(ChatSessionDocument document, ChatSessionSnapshot? snapshot)
    {
        Save(document);
        string path = Path.Combine(DirectoryFor(document.Id), KvFileName);
        try
        {
            if (snapshot?.KVCache is { } cache)
                JsonFile.Write(path, cache);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public ChatSessionSnapshot BuildSnapshot(ChatSessionDocument document)
    {
        KVCacheSnapshot? cache = null;
        string path = Path.Combine(DirectoryFor(document.Id), KvFileName);
        if (File.Exists(path))
        {
            try { cache = JsonFile.Read<KVCacheSnapshot>(path); }
            catch { cache = null; }
        }

        return new ChatSessionSnapshot
        {
            History = [.. document.Messages],
            KVCache = cache,
        };
    }

    public long SnapshotSize(string id)
    {
        string path = Path.Combine(DirectoryFor(id), KvFileName);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public void Delete(string id)
    {
        string directory = DirectoryFor(id);
        string relative = Path.GetRelativePath(Root, directory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Refusing to delete a chat outside the chats folder.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    public static string Fingerprint(string modelPath)
    {
        var info = new FileInfo(Path.GetFullPath(modelPath));
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private string DirectoryFor(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string directory = Path.GetFullPath(Path.Combine(Root, id));
        string relative = Path.GetRelativePath(Root, directory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Chat id leaves the chats folder.");
        return directory;
    }
}



