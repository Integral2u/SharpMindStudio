namespace SharpMind.Studio.ComputeHost;

/// <summary>Maps untrusted relative paths onto a fixed folder without allowing escapes.</summary>
internal static class HostFiles
{
    public static bool TryResolve(string root, string? relative, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relative)) return false;

        string cleaned = relative.Replace('\\', '/').Trim('/');
        if (cleaned.Length == 0) return false;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, cleaned));
        }
        catch
        {
            return false;
        }

        string normalizedRoot = Path.GetFullPath(root);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            normalizedRoot += Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>Streams a request body to disk through a temp file, so a failed upload never looks complete.</summary>
    public static async Task<long> SaveAsync(
        Stream source,
        string destination,
        CancellationToken cancellationToken)
    {
        string? folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        string temp = destination + ".upload";
        long bytes = 0;
        try
        {
            await using (var file = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true))
            {
                byte[] buffer = new byte[81_920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytes += read;
                }
            }

            File.Move(temp, destination, overwrite: true);
            return bytes;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
