namespace SharpMind.Studio.Core.Chat;

/// <summary>One command the chat composer understands.</summary>
public sealed record ChatCommandDefinition(
    string Name,
    string Usage,
    string Description,
    string? ArgumentHint = null,
    bool AvailableWhileStreaming = false,
    IReadOnlyList<string>? Aliases = null)
{
    public string Invocation => "/" + Name;
}

/// <summary>A parsed slash-command line.</summary>
public sealed record ChatCommandLine(string Name, string Argument);

/// <summary>The built-in chat commands and the parser that recognises them.</summary>
public static class ChatCommands
{
    public static IReadOnlyList<ChatCommandDefinition> All { get; } =
    [
        new("help", "/help", "List the commands available in this chat."),
        new("new", "/new", "Start a new chat with the same model."),
        new("model", "/model <name>", "Switch model by name; /model shows the current one.",
            ArgumentHint: "<name>"),
        new("system", "/system [text|clear]", "Show, set or clear the system prompt.",
            ArgumentHint: "[text|clear]"),
        new("rename", "/rename [name]", "Rename this chat.",
            ArgumentHint: "[name]"),
        new("delete", "/delete", "Delete this chat."),
        new("options", "/options", "Open inference and tool options."),
        new("stop", "/stop", "Stop the current reply.", AvailableWhileStreaming: true),
    ];

    public static ChatCommandDefinition? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string needle = name.TrimStart('/');
        return All.FirstOrDefault(command =>
            string.Equals(command.Name, needle, StringComparison.OrdinalIgnoreCase) ||
            command.Aliases?.Any(alias =>
                string.Equals(alias, needle, StringComparison.OrdinalIgnoreCase)) == true);
    }

    public static IReadOnlyList<ChatCommandDefinition> Match(string query)
    {
        string needle = query.Trim();
        if (needle.Length == 0) return All;
        return
        [
            .. All
                .Where(command =>
                    command.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    command.Description.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    command.Usage.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    command.Aliases?.Any(alias =>
                        alias.Contains(needle, StringComparison.OrdinalIgnoreCase)) == true)
                .OrderByDescending(command =>
                    command.Name.Equals(needle, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(command =>
                    command.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                .ThenBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }
}

public static class ChatCommandParser
{
    /// <summary>
    /// True when the text looks like a command line. A leading <c>//</c> is a
    /// path, not a command, so it is deliberately excluded.
    /// </summary>
    public static bool IsCommandAttempt(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string trimmed = text.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '/' &&
            (trimmed.Length == 1 || trimmed[1] != '/');
    }

    /// <summary>
    /// Splits <c>/model qwen 2.5</c> into the command name and the rest. Returns
    /// null for ordinary chat text, a bare slash and path-like input.
    /// </summary>
    public static ChatCommandLine? Parse(string? text)
    {
        if (!IsCommandAttempt(text)) return null;
        string trimmed = text!.Trim();
        string body = trimmed[1..];
        int split = body.IndexOfAny([' ', '\t', '\r', '\n']);
        string name = split < 0 ? body : body[..split];
        string argument = split < 0 ? "" : body[(split + 1)..].Trim();
        return name.Length == 0 ? null : new ChatCommandLine(name, argument);
    }
}
