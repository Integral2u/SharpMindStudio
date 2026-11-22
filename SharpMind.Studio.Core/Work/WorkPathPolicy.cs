namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Resolves a shell's working directory and redirection targets through the same
/// project/context jail as the file tools. It is a guardrail for a cooperative
/// model, not an OS sandbox; symlinks are not resolved.
/// </summary>
public sealed class WorkPathPolicy
{
    private readonly IReadOnlyList<string> _roots;

    public WorkPathPolicy(string projectRoot, IReadOnlyList<string> allowedRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        Root = NormalizeRoot(projectRoot);
        _roots = [.. allowedRoots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (_roots.Count == 0) _roots = [Root];
    }

    public string Root { get; }

    public string ResolveDirectory(string path, string? baseDirectory = null)
    {
        string full = Resolve(path, baseDirectory);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        return full;
    }

    public string ResolveFile(string path, string? baseDirectory = null) =>
        Resolve(path, baseDirectory);

    private string Resolve(string path, string? baseDirectory)
    {
        string value = string.IsNullOrWhiteSpace(path) ? "." : path.Trim();
        string basePath = string.IsNullOrWhiteSpace(baseDirectory) ? Root : baseDirectory;
        string full = Path.GetFullPath(value, basePath);

        if (!_roots.Any(root => IsUnderRoot(full, root)))
            throw new UnauthorizedAccessException(
                $"Path '{path}' is outside the allowed project locations.");

        return full;
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return true;

        string prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
