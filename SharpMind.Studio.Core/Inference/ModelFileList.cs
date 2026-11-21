namespace SharpMind.Studio.Core.Inference;

/// <summary>
/// The file list behind the model pickers.
///
/// The Models folder is scanned recursively: downloads, conversions and files a
/// user drops in can land in a subfolder, and a picker that only looks at the top
/// level cannot see them. The training folder is scanned at the top level only,
/// so a run's checkpoints and scratch files cannot bury the list.
///
/// Results are ordered by file name and are never truncated. A "most recent N"
/// cap is worse than a long list: it hides whichever model the user has not
/// touched lately, which is exactly the one they are looking for.
/// </summary>
public static class ModelFileList
{
    private static readonly string[] Extensions = [".smm", ".gguf"];

    public static IReadOnlyList<FileInfo> Find(
        string modelsFolder,
        string trainingFolder,
        string adapterExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(trainingFolder);

        string modelsRoot = Path.GetFullPath(modelsFolder);
        string trainingRoot = Path.GetFullPath(trainingFolder);

        var found = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Collect(modelsRoot, SearchOption.AllDirectories);
        if (!trainingRoot.Equals(modelsRoot, StringComparison.OrdinalIgnoreCase))
            Collect(trainingRoot, SearchOption.TopDirectoryOnly);

        return
        [
            .. found
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
        ];

        void Collect(string folder, SearchOption option)
        {
            if (!Directory.Exists(folder)) return;

            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*", option))
                {
                    string extension = Path.GetExtension(path);
                    bool wanted = Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(adapterExtension) &&
                         extension.Equals(adapterExtension, StringComparison.OrdinalIgnoreCase));
                    if (!wanted) continue;
                    if (!seen.Add(path)) continue;

                    found.Add(new FileInfo(path));
                }
            }
            catch (IOException)
            {
                // A folder that changes under the walk still yields what was read.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
