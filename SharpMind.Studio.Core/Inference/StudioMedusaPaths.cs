namespace SharpMind.Studio.Core.Inference;

/// <summary>Where Studio keeps calibrated Medusa heads.</summary>
public static class StudioMedusaPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpMind", "Studio", "medusa");

    public static string DefaultFor(string modelPath, int numHeads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        return Path.Combine(
            Root,
            $"{Path.GetFileNameWithoutExtension(modelPath)}-h{Math.Max(1, numHeads)}.mdh");
    }

    /// <summary>Cheap, stable identity for a model file; enough to reject stale heads.</summary>
    public static string FingerprintFor(string modelPath)
    {
        var info = new FileInfo(modelPath);
        return $"{info.Name}:{info.Length}";
    }
}
