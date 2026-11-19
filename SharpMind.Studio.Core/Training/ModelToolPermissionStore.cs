using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Work;

namespace SharpMind.Studio.Core.Training;

public sealed record ModelToolPermissionsDocument
{
    public const string CurrentSchema = "studio-model-tools/1";

    public string Schema { get; init; } = CurrentSchema;

    /// <summary>Tool permissions keyed by <see cref="ModelIdentity.Id"/>.</summary>
    public Dictionary<string, ToolPermissionSet> Models { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Permissions for tools that live inside a model file. They are keyed by the
/// model's identity so one model's embedded tools never inherit another's
/// settings: a different model simply has no entry, and its tools start off.
/// </summary>
public sealed class ModelToolPermissionStore
{
    public const string FileName = "model-tools.json";

    private readonly Lock _gate = new();
    private ModelToolPermissionsDocument _document;

    public ModelToolPermissionStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = System.IO.Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
        Path = System.IO.Path.Combine(Folder, FileName);
        _document = Load();
    }

    public string Folder { get; }
    public string Path { get; }

    public ToolPermissionSet For(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return new ToolPermissionSet();
        lock (_gate)
        {
            return _document.Models.TryGetValue(modelId, out ToolPermissionSet? value)
                ? new ToolPermissionSet
                {
                    Tools = new Dictionary<string, WorkToolPermission>(
                        value.Tools, StringComparer.OrdinalIgnoreCase),
                    Groups = new Dictionary<string, WorkToolPermission>(
                        value.Groups, StringComparer.OrdinalIgnoreCase),
                }
                : new ToolPermissionSet();
        }
    }

    public void Save(string modelId, ToolPermissionSet permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(permissions);

        lock (_gate)
        {
            if (permissions.IsEmpty) _document.Models.Remove(modelId);
            else _document.Models[modelId] = permissions;
            SaveLocked();
        }
    }

    public void Remove(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        lock (_gate)
        {
            _document.Models.Remove(modelId);
            SaveLocked();
        }
    }

    private ModelToolPermissionsDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return new ModelToolPermissionsDocument();
            ModelToolPermissionsDocument document =
                JsonFile.Read<ModelToolPermissionsDocument>(Path);
            if (document.Schema != ModelToolPermissionsDocument.CurrentSchema)
                return new ModelToolPermissionsDocument();

            document.Models = document.Models is null
                ? new Dictionary<string, ToolPermissionSet>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ToolPermissionSet>(
                    document.Models, StringComparer.OrdinalIgnoreCase);
            return document;
        }
        catch
        {
            return new ModelToolPermissionsDocument();
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
            // A read-only folder must not stop a chat.
        }
    }
}
