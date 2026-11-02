namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Resolves and checks Work context locations. The editor and the runtime use
/// the same resolution, so the folder a permission is shown against is the
/// folder the file tools actually jail to.
/// </summary>
public static class WorkContextValidation
{
    public static string Resolve(WorkContext context, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        string path = string.IsNullOrWhiteSpace(context.Path) ? "." : context.Path.Trim();
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
    }

    /// <summary>
    /// Returns an error when two contexts are the same folder; otherwise fills
    /// <paramref name="warnings"/> with missing folders and overlapping roots.
    /// Overlap is allowed (the most specific root wins) but worth surfacing.
    /// </summary>
    public static string? Validate(
        IReadOnlyList<WorkContext> contexts,
        string projectRoot,
        out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var list = new List<string>();
        var resolved = new List<(WorkContext Context, string Full)>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (WorkContext context in contexts)
        {
            string full;
            try
            {
                full = Resolve(context, projectRoot);
            }
            catch (Exception ex)
            {
                warnings = list;
                return $"Context '{context.Path}' is not a valid path: {ex.Message}";
            }

            if (seen.TryGetValue(full, out string? duplicate))
            {
                warnings = list;
                return $"Context '{context.Path}' points at the same folder as '{duplicate}'.";
            }

            seen[full] = context.Path;
            resolved.Add((context, full));
            if (!Directory.Exists(full))
                list.Add($"Context '{context.Path}' does not exist yet.");
        }

        for (int i = 0; i < resolved.Count; i++)
        {
            for (int j = i + 1; j < resolved.Count; j++)
            {
                if (!IsUnder(resolved[i].Full, resolved[j].Full)
                    && !IsUnder(resolved[j].Full, resolved[i].Full))
                    continue;

                list.Add($"Contexts '{resolved[i].Context.Path}' and '{resolved[j].Context.Path}' overlap; "
                         + "the most specific path wins.");
            }
        }

        warnings = list;
        return null;
    }

    private static bool IsUnder(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "."
               || (!relative.StartsWith("..", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative));
    }
}
