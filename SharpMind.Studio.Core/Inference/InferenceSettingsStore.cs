using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Work;

namespace SharpMind.Studio.Core.Inference;

public sealed record InferenceSettingsDocument
{
    public const string CurrentSchema = "studio-inference/1";

    public string Schema { get; init; } = CurrentSchema;
    public InferenceOverrides System { get; set; } = InferenceOverrides.SystemDefaults();
    public Dictionary<string, InferenceOverrides> Families { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, InferenceOverrides> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, InferenceOverrides> WorkModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TuneProfileSet> TuneProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tool permissions that apply to every model unless a layer overrides them.</summary>
    public ToolPermissionSet SystemTools { get; set; } = new();

    /// <summary>Per-family tool overrides, keyed by lower-case family id.</summary>
    public Dictionary<string, ToolPermissionSet> FamilyTools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-model tool overrides, keyed by normalized model path.</summary>
    public Dictionary<string, ToolPermissionSet> ModelTools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Studio-level command access and allowlist.</summary>
    public CommandAccessLayer SystemCommands { get; set; } =
        new() { Mode = CommandAccessMode.Restricted };

    /// <summary>Per-family command additions, keyed by lower-case family id.</summary>
    public Dictionary<string, CommandAccessLayer> FamilyCommands { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-model command additions, keyed by normalized model path.</summary>
    public Dictionary<string, CommandAccessLayer> ModelCommands { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One-time upgrade of the old "Speculative + Int8" system defaults, which
    /// were slower than Standard + float KV on CPU. Recorded so a later explicit
    /// choice of that combination is not overwritten again.
    /// </summary>
    public bool InferenceOptimizationMigrated { get; set; }
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
        if (MigrateLegacyThroughputDefaults())
            SaveLocked();
    }

    private bool MigrateLegacyThroughputDefaults()
    {
        if (_document.InferenceOptimizationMigrated)
            return false;

        if (_document.System is
            { Generator: "SpeculativeGeneratorBuilder", Cache: "Int8KVCacherBuilder" })
        {
            _document.System = _document.System with
            {
                Generator = "StandardGeneratorBuilder",
                Cache = "KVCacherBuilder",
            };
        }

        _document.InferenceOptimizationMigrated = true;
        return true;
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

    public TuneProfileSet? TuneSet(string modelPath)
    {
        string key = NormalizeModel(modelPath);
        lock (_gate)
            return _document.TuneProfiles.GetValueOrDefault(key);
    }

    /// <summary>Tool permissions shared by every model.</summary>
    public ToolPermissionSet SystemToolPermissions
    {
        get { lock (_gate) return _document.SystemTools ?? new ToolPermissionSet(); }
    }

    public ToolPermissionSet? FamilyToolPermissions(string familyId)
    {
        if (string.IsNullOrWhiteSpace(familyId)) return null;
        lock (_gate)
            return _document.FamilyTools.GetValueOrDefault(familyId.Trim().ToLowerInvariant());
    }

    public ToolPermissionSet? ModelToolPermissions(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        string key = NormalizeModel(modelPath);
        lock (_gate)
            return _document.ModelTools.GetValueOrDefault(key);
    }

    /// <summary>An empty set is stored as absence, so the layer keeps inheriting.</summary>
    public void SetSystemTools(ToolPermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        lock (_gate)
        {
            _document.SystemTools = permissions;
            SaveLocked();
        }
    }

    public void SetFamilyTools(string familyId, ToolPermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (string.IsNullOrWhiteSpace(familyId)) return;
        string key = familyId.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (permissions.IsEmpty) _document.FamilyTools.Remove(key);
            else _document.FamilyTools[key] = permissions;
            SaveLocked();
        }
    }

    public void SetModelTools(string modelPath, ToolPermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        string key = NormalizeModel(modelPath);
        lock (_gate)
        {
            if (permissions.IsEmpty) _document.ModelTools.Remove(key);
            else _document.ModelTools[key] = permissions;
            SaveLocked();
        }
    }

    public void DeleteFamilyTools(string familyId)
    {
        if (string.IsNullOrWhiteSpace(familyId)) return;
        lock (_gate)
        {
            _document.FamilyTools.Remove(familyId.Trim().ToLowerInvariant());
            SaveLocked();
        }
    }

    /// <summary>Command access shared by every model.</summary>
    public CommandAccessLayer SystemCommands
    {
        get
        {
            lock (_gate)
            {
                return (_document.SystemCommands ?? new CommandAccessLayer
                {
                    Mode = CommandAccessMode.Restricted,
                }).Normalize();
            }
        }
    }

    public CommandAccessLayer? FamilyCommands(string familyId)
    {
        if (string.IsNullOrWhiteSpace(familyId)) return null;
        lock (_gate)
            return _document.FamilyCommands.GetValueOrDefault(
                familyId.Trim().ToLowerInvariant())?.Normalize();
    }

    public CommandAccessLayer? ModelCommands(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        string key = NormalizeModel(modelPath);
        lock (_gate)
            return _document.ModelCommands.GetValueOrDefault(key)?.Normalize();
    }

    public void SetSystemCommands(CommandAccessLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        lock (_gate)
        {
            _document.SystemCommands = layer.Normalize();
            SaveLocked();
        }
    }

    public void SetFamilyCommands(string familyId, CommandAccessLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (string.IsNullOrWhiteSpace(familyId)) return;
        string key = familyId.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (layer.IsEmpty) _document.FamilyCommands.Remove(key);
            else _document.FamilyCommands[key] = layer.Normalize();
            SaveLocked();
        }
    }

    public void SetModelCommands(string modelPath, CommandAccessLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        string key = NormalizeModel(modelPath);
        lock (_gate)
        {
            if (layer.IsEmpty) _document.ModelCommands.Remove(key);
            else _document.ModelCommands[key] = layer.Normalize();
            SaveLocked();
        }
    }

    public void DeleteModelTools(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        lock (_gate)
        {
            _document.ModelTools.Remove(NormalizeModel(modelPath));
            SaveLocked();
        }
    }

    public InferenceOverrides? WorkModelOverrides(string modelPath)
    {
        string key = NormalizeModel(modelPath);
        lock (_gate)
            return _document.WorkModels.GetValueOrDefault(key);
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

    public void SetTuneSet(string modelPath, TuneProfileSet profiles)
    {
        lock (_gate)
        {
            _document.TuneProfiles[NormalizeModel(modelPath)] = profiles;
            SaveLocked();
        }
    }

    public void DeleteTuneSet(string modelPath)
    {
        lock (_gate)
        {
            _document.TuneProfiles.Remove(NormalizeModel(modelPath));
            SaveLocked();
        }
    }

    public void SetWorkModel(string modelPath, InferenceOverrides profile)
    {
        lock (_gate)
        {
            _document.WorkModels[NormalizeModel(modelPath)] = profile;
            SaveLocked();
        }
    }

    public void DeleteWorkModel(string modelPath)
    {
        lock (_gate)
        {
            _document.WorkModels.Remove(NormalizeModel(modelPath));
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
            document.WorkModels = document.WorkModels is null
                ? new Dictionary<string, InferenceOverrides>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, InferenceOverrides>(
                    document.WorkModels, StringComparer.OrdinalIgnoreCase);
            document.TuneProfiles = document.TuneProfiles is null
                ? new Dictionary<string, TuneProfileSet>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, TuneProfileSet>(
                    document.TuneProfiles, StringComparer.OrdinalIgnoreCase);
            document.SystemTools ??= new ToolPermissionSet();
            document.FamilyTools = document.FamilyTools is null
                ? new Dictionary<string, ToolPermissionSet>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ToolPermissionSet>(
                    document.FamilyTools, StringComparer.OrdinalIgnoreCase);
            document.ModelTools = document.ModelTools is null
                ? new Dictionary<string, ToolPermissionSet>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ToolPermissionSet>(
                    document.ModelTools, StringComparer.OrdinalIgnoreCase);
            document.SystemCommands ??= new CommandAccessLayer
            {
                Mode = CommandAccessMode.Restricted,
            };
            document.FamilyCommands = document.FamilyCommands is null
                ? new Dictionary<string, CommandAccessLayer>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CommandAccessLayer>(
                    document.FamilyCommands, StringComparer.OrdinalIgnoreCase);
            document.ModelCommands = document.ModelCommands is null
                ? new Dictionary<string, CommandAccessLayer>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CommandAccessLayer>(
                    document.ModelCommands, StringComparer.OrdinalIgnoreCase);
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




