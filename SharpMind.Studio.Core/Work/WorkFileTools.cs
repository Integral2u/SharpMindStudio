using System.Text;
using SharpMind.Core;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// File tools for Work mode. Every operation resolves through one path jail and
/// asks the project permission profile before touching disk. Path jailing is a
/// guardrail for a cooperative model, not an OS sandbox.
/// </summary>
public sealed class WorkFileTools(
    string projectRoot,
    IReadOnlyList<string> allowedRoots,
    Func<WorkToolAccess, Task<bool>>? permission = null)
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".bin", ".bmp", ".class", ".dll", ".dylib", ".exe", ".flac",
        ".gif", ".gz", ".ico", ".jpeg", ".jpg", ".mkv", ".mov", ".mp3", ".mp4",
        ".nupkg", ".ogg", ".pdf", ".png", ".so", ".tar", ".ttf", ".wav", ".webm",
        ".webp", ".woff", ".woff2", ".zip",
    };

    private readonly string _root = NormalizeRoot(projectRoot);
    private readonly IReadOnlyList<string> _roots = [.. allowedRoots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase)];

    public WorkFileTools(string projectRoot, Func<WorkToolAccess, Task<bool>>? permission = null)
        : this(projectRoot, [projectRoot], permission)
    {
    }

    [ToolDesc("Lists files and folders inside the project. Use relative paths, with '.' for the project root.")]
    public async Task<string> ListFiles(
        [ToolDesc("Relative directory path. Use '.' for the project root.")] string relativePath = ".")
    {
        try
        {
            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(ListFiles), WorkToolCategory.Read, relativePath))
                return Denied(nameof(ListFiles), relativePath);
            if (!Directory.Exists(path)) return $"Directory not found: {relativePath}";

            string[] entries = Directory.GetFileSystemEntries(path)
                .OrderByDescending(entry => Directory.Exists(entry))
                .ThenBy(entry => Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (entries.Length == 0) return "Directory is empty.";

            var output = new StringBuilder();
            foreach (string entry in entries)
                output.AppendLine($"{(Directory.Exists(entry) ? "[DIR]" : "[FILE]")} {Path.GetFileName(entry)}");
            return output.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error listing files: {ex.Message}";
        }
    }

    [ToolDesc("Reads a UTF-8 text file inside the project. Use a relative path.")]
    public async Task<string> ReadFile(
        [ToolDesc("Relative path to the file.")] string relativePath)
    {
        try
        {
            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(ReadFile), WorkToolCategory.Read, relativePath))
                return Denied(nameof(ReadFile), relativePath);
            if (!File.Exists(path)) return $"File not found: {relativePath}";
            if (new FileInfo(path).Length > 2 * 1024 * 1024)
                return $"File is larger than 2 MB: {relativePath}";

            return await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    [ToolDesc("Searches text files inside the project for a literal string and returns matching lines with line numbers.")]
    public async Task<string> SearchFiles(
        [ToolDesc("Text to search for, case-insensitive.")] string query,
        [ToolDesc("Relative directory to search from. Use '.' for the project root.")] string relativePath = ".",
        [ToolDesc("Maximum number of matching lines to return.")] int maxResults = 50)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Search text is required.";

            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(SearchFiles), WorkToolCategory.Read, relativePath))
                return Denied(nameof(SearchFiles), relativePath);
            if (!Directory.Exists(path)) return $"Directory not found: {relativePath}";

            maxResults = Math.Clamp(maxResults, 1, 200);
            var matches = new List<string>();
            foreach (string file in EnumerateSearchFiles(path))
            {
                if (matches.Count >= maxResults) break;
                if (BinaryExtensions.Contains(Path.GetExtension(file))) continue;
                if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;

                string[] lines;
                try { lines = await File.ReadAllLinesAsync(file); }
                catch { continue; }

                for (int i = 0; i < lines.Length && matches.Count < maxResults; i++)
                {
                    if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
                    string text = lines[i].Trim();
                    if (text.Length > 220) text = text[..220] + "...";
                    matches.Add($"{relative}:{i + 1}: {text}");
                }
            }

            return matches.Count == 0
                ? $"No matches for \"{query}\"."
                : string.Join(Environment.NewLine, matches);
        }
        catch (Exception ex)
        {
            return $"Error searching files: {ex.Message}";
        }
    }

    [ToolDesc("Writes UTF-8 text to a file inside the project, creating parents and overwriting the file.")]
    public async Task<string> WriteFile(
        [ToolDesc("Relative path to the file.")] string relativePath,
        [ToolDesc("Text to write.")] string content)
    {
        try
        {
            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(WriteFile), WorkToolCategory.Write, relativePath))
                return Denied(nameof(WriteFile), relativePath);

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, content ?? "");
            return $"Wrote {relativePath}.";
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    [ToolDesc("Appends UTF-8 text to a file inside the project, creating it when missing.")]
    public async Task<string> AppendToFile(
        [ToolDesc("Relative path to the file.")] string relativePath,
        [ToolDesc("Text to append.")] string content)
    {
        try
        {
            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(AppendToFile), WorkToolCategory.Write, relativePath))
                return Denied(nameof(AppendToFile), relativePath);

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(path, content ?? "");
            return $"Appended to {relativePath}.";
        }
        catch (Exception ex)
        {
            return $"Error appending to file: {ex.Message}";
        }
    }

    [ToolDesc("Replaces an exact piece of text in a file. Use it to insert, replace or remove a section without rewriting the whole file. The old text must match exactly and appear once unless replaceAll is true.")]
    public async Task<string> EditFile(
        [ToolDesc("Relative path to the file.")] string relativePath,
        [ToolDesc("The exact text to replace. Include surrounding context so it appears once.")] string oldText,
        [ToolDesc("The replacement text. Use an empty string to remove the section.")] string newText,
        [ToolDesc("Replace every occurrence instead of failing when the text appears more than once.")] bool replaceAll = false)
    {
        try
        {
            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(EditFile), WorkToolCategory.Write, relativePath))
                return Denied(nameof(EditFile), relativePath);
            if (!File.Exists(path)) return $"File not found: {relativePath}";
            if (string.IsNullOrEmpty(oldText)) return "The old text is required.";

            if (new FileInfo(path).Length > 2 * 1024 * 1024)
                return $"File is larger than 2 MB: {relativePath}";

            string content = await File.ReadAllTextAsync(path);

            // A Windows file usually has CRLF while a model emits LF. Matching the
            // snippet as written would fail on every multi-line edit, so align the
            // snippet and the replacement with the file once.
            bool crlf = content.Contains("\r\n", StringComparison.Ordinal);
            string match = crlf ? ToCrlf(oldText) : oldText;
            string replacement = crlf ? ToCrlf(newText ?? "") : newText ?? "";

            int count = CountOccurrences(content, match);
            if (count == 0)
            {
                return $"The old text was not found in {relativePath}. Read the file and copy the exact "
                       + "text, including indentation and line breaks.";
            }
            if (count > 1 && !replaceAll)
            {
                return $"The old text appears {count} times in {relativePath}. Include more surrounding "
                       + "context, or set replaceAll to change every occurrence.";
            }

            string updated = replaceAll
                ? content.Replace(match, replacement, StringComparison.Ordinal)
                : ReplaceFirst(content, match, replacement);

            await File.WriteAllTextAsync(path, updated);
            return count == 1
                ? $"Edited {relativePath}."
                : $"Edited {relativePath} ({count} replacements).";
        }
        catch (Exception ex)
        {
            return $"Error editing file: {ex.Message}";
        }
    }

    private static string ToCrlf(string text) =>
        text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) return text;
        return string.Concat(
            text.AsSpan(0, index),
            newValue,
            text.AsSpan(index + oldValue.Length));
    }

    [ToolDesc("Creates an empty file inside the project, creating parent directories when needed.")]
    public async Task<string> CreateFile(
        [ToolDesc("Relative path to the file.")] string relativePath)
    {
        try
        {
            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(CreateFile), WorkToolCategory.Write, relativePath))
                return Denied(nameof(CreateFile), relativePath);

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "");
            return $"Created {relativePath}.";
        }
        catch (Exception ex)
        {
            return $"Error creating file: {ex.Message}";
        }
    }

    [ToolDesc("Deletes a file inside the project. This cannot be undone.")]
    public async Task<string> DeleteFile(
        [ToolDesc("Relative path to the file.")] string relativePath)
    {
        try
        {
            string path = ResolvePath(relativePath);
            if (!await AllowedAsync(nameof(DeleteFile), WorkToolCategory.Delete, relativePath))
                return Denied(nameof(DeleteFile), relativePath);
            if (!File.Exists(path)) return $"File not found: {relativePath}";

            File.Delete(path);
            return $"Deleted {relativePath}.";
        }
        catch (Exception ex)
        {
            return $"Error deleting file: {ex.Message}";
        }
    }

    private async Task<bool> AllowedAsync(string tool, WorkToolCategory category, string path)
    {
        if (permission is null) return false;
        string fullPath = Path.IsPathRooted(path) ? Path.GetFullPath(path) : ResolvePath(path);
        return await permission(new WorkToolAccess(tool, category, fullPath));
    }

    private static string Denied(string tool, string path) =>
        $"Permission denied: {tool} may not access '{path}' with the current project permission.";

    private string ResolvePath(string relativePath)
    {
        string candidate = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath.Trim();
        string fullPath = Path.GetFullPath(Path.Combine(_root, candidate));

        bool inside = _roots.Any(root => IsUnderRoot(fullPath, root));
        if (!inside)
            throw new UnauthorizedAccessException($"Path '{relativePath}' is outside the allowed project locations.");
        return fullPath;
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (char separator in Separators)
        {
            if (fullPath.StartsWith(root + separator, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    private static IEnumerable<string> EnumerateSearchFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (string file in files) yield return file;
            foreach (string child in directories)
            {
                if (SkippedDirectories.Contains(Path.GetFileName(child))) continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string full = Path.GetFullPath(root);
        string pathRoot = Path.GetPathRoot(full) ?? "";
        return string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}




