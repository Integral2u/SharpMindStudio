using System.Reflection;
using SharpMind.Core;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed record WorkToolInfo(string Name, string Source, bool IsPlugin, string Group);

public sealed record WorkToolDefaultsDocument
{
    public const string CurrentSchema = "studio-work-tools/1";

    public string Schema { get; init; } = CurrentSchema;
    public List<string> DisabledByDefault { get; init; } = [];
}

/// <summary>
/// Built-in Work tools plus [ToolDesc] tool classes discovered in the plugins
/// folder. System defaults decide what new projects receive; projects override
/// individual tools.
/// </summary>
public sealed class WorkToolService
{
    private static readonly (string Name, string Group)[] BuiltInTools =
    [
        ("ListFiles", "Files · read"),
        ("ReadFile", "Files · read"),
        ("SearchFiles", "Files · read"),
        ("WriteFile", "Files · write"),
        ("AppendToFile", "Files · write"),
        ("CreateFile", "Files · write"),
        ("DeleteFile", "Files · delete"),
        ("FetchUrl", "Web"),
        ("UIShowOptionSelection", "Interface"),
        ("UIGetFreeMemory", "System"),
        ("UIGetSystemSpecs", "System"),
        ("GetCurrentDateTime", "System"),
        ("Echo", "Diagnostics"),
        ("delegate", "Agents"),
        ("spawn_agent", "Agents"),
    ];

    private readonly StudioWorkspaceService _workspace;
    private readonly Lock _gate = new();
    private List<WorkToolInfo> _tools = [];
    private List<object> _pluginTools = [];
    private HashSet<string> _disabledByDefault = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _warnings = [];

    public WorkToolService(StudioWorkspaceService workspace)
    {
        _workspace = workspace;
        LoadDefaults();
        Reload();
    }

    public event Action? Changed;

    public string Folder => _workspace.PluginsFolder;
    public IReadOnlyList<WorkToolInfo> Tools => _tools;
    public IReadOnlyList<object> PluginTools => _pluginTools;
    public IReadOnlyList<string> Warnings => _warnings;

    public void Reload()
    {
        var tools = BuiltInTools
            .Select(entry => new WorkToolInfo(entry.Name, "built-in", false, entry.Group))
            .ToList();
        var pluginTools = new List<object>();
        var warnings = new List<string>();
        var names = new HashSet<string>(BuiltInTools.Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase);

        string folder = _workspace.PluginsFolder;
        if (Directory.Exists(folder))
        {
            PluginAssemblyResolver.RegisterDirectory(folder);
            foreach (string dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
            {
                Assembly assembly;
                try
                {
                    PluginAssemblyResolver.RegisterDirectory(Path.GetDirectoryName(dll)!);
                    assembly = Assembly.LoadFrom(dll);
                }
                catch (BadImageFormatException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{Path.GetFileName(dll)}: {ex.GetBaseException().Message}");
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = [.. ex.Types.Where(type => type is not null).Cast<Type>()];
                    warnings.Add($"{assembly.GetName().Name}: some types could not be loaded.");
                }

                foreach (Type type in types)
                {
                    if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    string[] toolNames = [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                        .Where(method => method.GetCustomAttributes(typeof(ToolDescAttribute), inherit: false).Length != 0)
                        .Select(method => method.Name)
                        .Where(names.Add)];
                    if (toolNames.Length == 0) continue;

                    try
                    {
                        object? instance = Activator.CreateInstance(type);
                        if (instance is null) continue;
                        pluginTools.Add(instance);
                        string source = assembly.GetName().Name ?? Path.GetFileName(dll);
                        foreach (string name in toolNames.Order(StringComparer.OrdinalIgnoreCase))
                            tools.Add(new WorkToolInfo(name, source, true, $"Plugins · {source}"));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not instantiate tool '{type.FullName}': {ex.Message}");
                    }
                }
            }
        }

        lock (_gate)
        {
            _tools = tools;
            _pluginTools = pluginTools;
            _warnings = warnings;
        }
    }

    public bool IsDefaultEnabled(string name)
    {
        lock (_gate) return !_disabledByDefault.Contains(name);
    }

    public void SetDefault(string name, bool enabled) => SetDefaults([name], enabled);

    public void SetDefaults(IEnumerable<string> names, bool enabled)
    {
        lock (_gate)
        {
            foreach (string name in names)
            {
                if (enabled) _disabledByDefault.Remove(name);
                else _disabledByDefault.Add(name);
            }
        }
        SaveDefaults();
        Changed?.Invoke();
    }

    public void Refresh() => Reload();

    public string FilePath => StudioPaths.WorkToolsFile;

    private void LoadDefaults()
    {
        try
        {
            if (!File.Exists(StudioPaths.WorkToolsFile)) return;
            WorkToolDefaultsDocument document = JsonFile.Read<WorkToolDefaultsDocument>(StudioPaths.WorkToolsFile);
            if (document.Schema != WorkToolDefaultsDocument.CurrentSchema) return;
            _disabledByDefault = new HashSet<string>(
                document.DisabledByDefault ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _disabledByDefault = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveDefaults()
    {
        try
        {
            List<string> disabled;
            lock (_gate) disabled = [.. _disabledByDefault];
            JsonFile.Write(StudioPaths.WorkToolsFile, new WorkToolDefaultsDocument
            {
                DisabledByDefault = disabled,
            });
        }
        catch
        {
        }
    }
}

