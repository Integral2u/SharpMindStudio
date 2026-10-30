namespace SharpMind.Studio.Core.Storage;

/// <summary>
/// Older Studio builds treated the settings folder as a project. Migration moved
/// real runs and chats into the default project, leaving empty folders behind.
/// These are safe to remove because only folders with no files anywhere inside
/// are touched.
/// </summary>
public static class LegacyStudioCleanup
{
    public static readonly string[] LegacyProjectFolders =
        ["runs", "chats", "model-lists", "models", "data", "evals", "workspaces"];

    public static IReadOnlyList<string> RemoveEmptyProjectFolders(string projectsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);
        if (!Directory.Exists(projectsRoot)) return [];

        var removed = new List<string>();
        foreach (string name in LegacyProjectFolders)
        {
            string path = Path.Combine(projectsRoot, name);
            if (!Directory.Exists(path) || !HasNoFiles(path)) continue;

            try
            {
                Directory.Delete(path, recursive: true);
                removed.Add(path);
            }
            catch (IOException)
            {
                // Another Studio process may still be using it; try again next start.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static bool HasNoFiles(string path)
    {
        foreach (string _ in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            return false;
        return true;
    }
}
