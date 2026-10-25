using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Inference;

public sealed record InferenceSettingsDocument
{
    public const string CurrentSchema = "studio-inference/1";

    public string Schema { get; init; } = CurrentSchema;
    public InferenceOverrides System { get; set; } = InferenceOverrides.SystemDefaults();
    public Dictionary<string, InferenceOverrides> Families { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, InferenceOverrides> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InferenceSettingsStore
{
    private readonly object _gate = new();
    private InferenceSettingsDocument _document;

    public InferenceSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _document = Load();
    }

    public string Path { get; }
    public InferenceSettingsDocument Document
    {
        get { lock (_gate) return _document; }
    }

    public InferenceOverrides? FamilyOverrides(string familyId)
    {
        lock (_gate)
            return _document.Families.GetValueOrDefault(familyId);
    }

    public InferenceOverrides? ModelOverrides(string modelPath)
    {
        string key = NormalizeModel(modelPath);
        lock (_gate)
            return _document.Models.GetValueOrDefault(key);
    }

    public void SetSystem(InferenceOverrides profile)
    {
        lock (_gate)
        {
            _document.System = profile;
            SaveLocked();
        }
    }

    public void SetFamily(string familyId, InferenceOverrides profile)
    {
        if (string.IsNullOrWhiteSpace(familyId)) return;
        lock (_gate)
        {
            _document.Families[familyId.Trim().ToLowerInvariant()] = profile;
            SaveLocked();
        }
    }

    public void DeleteFamily(string familyId)
    {
        lock (_gate)
        {
            _document.Families.Remove(familyId.Trim().ToLowerInvariant());
            SaveLocked();
        }
    }

    public void SetModel(string modelPath, InferenceOverrides profile)
    {
        lock (_gate)
        {
            _document.Models[NormalizeModel(modelPath)] = profile;
            SaveLocked();
        }
    }

    public void DeleteModel(string modelPath)
    {
        lock (_gate)
        {
            _document.Models.Remove(NormalizeModel(modelPath));
            SaveLocked();
        }
    }

    private InferenceSettingsDocument Load()
    {
        try
        {
            if (!File.Exists(Path))
                return new InferenceSettingsDocument();

            InferenceSettingsDocument document = JsonFile.Read<InferenceSettingsDocument>(Path);
            if (document.Schema != InferenceSettingsDocument.CurrentSchema)
                return new InferenceSettingsDocument();

            document.System ??= InferenceOverrides.SystemDefaults();
            document.Families = new Dictionary<string, InferenceOverrides>(
                document.Families, StringComparer.OrdinalIgnoreCase);
            document.Models = new Dictionary<string, InferenceOverrides>(
                document.Models, StringComparer.OrdinalIgnoreCase);
            return document;
        }
        catch
        {
            return new InferenceSettingsDocument();
        }
    }

    private void SaveLocked()
    {
        try
        {
            JsonFile.Write(Path, _document);
        }
        catch
        {
        }
    }

    private static string NormalizeModel(string modelPath) =>
        System.IO.Path.GetFullPath(modelPath.Trim());
}
