using System.Reflection;
using SharpMind.Core;
using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Core.Safety;
using SharpMind.Studio.Core.Serialization;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed record WorkToolInfo(string Name, string Source, bool IsPlugin, string Group)
{
    /// <summary>Static warning from the assembly scan, before any plugin code runs.</summary>
    public bool StartsProcesses { get; init; }

    public bool UsesNetwork { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>A tool discovered on an MCP server, not a local plugin.</summary>
    public bool IsMcp { get; init; }
}

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
        ("EditFile", "Files · write"),
        ("AppendToFile", "Files · write"),
        ("CreateFile", "Files · write"),
        ("DeleteFile", "Files · delete"),
        ("RunProcess", "Execution"),
        ("RunShell", "Execution"),
        ("git_status", "CLI · git"),
        ("git_diff", "CLI · git"),
        ("git_log", "CLI · git"),
        ("dotnet_build", "CLI · .NET"),
        ("dotnet_test", "CLI · .NET"),
        ("rg_search", "CLI · search"),
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
    private List<WorkToolInfo> _mcpTools = [];
    private List<object> _pluginTools = [];
    private HashSet<string> _disabledByDefault = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _warnings = [];

    public WorkToolService(StudioWorkspaceService workspace, McpServerHost? mcp = null)
    {
        _workspace = workspace;
        LoadDefaults();
        Reload();
        if (mcp is not null) mcp.Stopped += ClearMcpTools;
    }

    public event Action? Changed;

    public string Folder => _workspace.PluginsFolder;
    public IReadOnlyList<WorkToolInfo> Tools
    {
        get { lock (_gate) return [.. _tools, .. _mcpTools]; }
    }
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
                        PluginCapabilities capabilities = PluginTrustScanner.Scan(assembly);
                        foreach (string name in toolNames.Order(StringComparer.OrdinalIgnoreCase))
                        {
                            tools.Add(new WorkToolInfo(name, source, true, $"Plugins · {source}")
                            {
                                StartsProcesses = capabilities.StartsProcesses,
                                UsesNetwork = capabilities.UsesNetwork,
                                Evidence = capabilities.Evidence,
                            });
                        }
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

    /// <summary>
    /// Adds the tools a connected MCP server advertised. Names are namespaced so
    /// two servers can expose the same tool without colliding.
    /// </summary>
    public void RegisterMcpTools(string serverId, string serverName, IReadOnlyList<McpToolInfo> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        lock (_gate)
        {
            _mcpTools.RemoveAll(tool =>
                tool.Source.Equals($"mcp:{serverId}", StringComparison.OrdinalIgnoreCase));

            foreach (McpToolInfo tool in tools)
            {
                _mcpTools.Add(new WorkToolInfo(
                    $"mcp__{serverId}__{tool.Name}",
                    $"MCP · {serverName}",
                    true,
                    $"MCP · {serverName}")
                {
                    IsMcp = true,
                    UsesNetwork = true,
                });
            }
        }

        Changed?.Invoke();
    }

    public void ClearMcpTools(string serverId)
    {
        lock (_gate)
        {
            _mcpTools.RemoveAll(tool =>
                tool.Source.Equals($"mcp:{serverId}", StringComparison.OrdinalIgnoreCase));
        }
        Changed?.Invoke();
    }

    /// <summary>The group a tool belongs to, or null when it is not registered.</summary>
    public string? GroupFor(string name)
    {
        lock (_gate)
        {
            foreach (WorkToolInfo tool in _tools)
            {
                if (string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase))
                    return tool.Group;
            }
            return null;
        }
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

