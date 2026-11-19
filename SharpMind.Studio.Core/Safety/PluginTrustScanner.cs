using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpMind.Studio.Core.Safety;

/// <summary>What a plugin assembly is able to do, judged without running it.</summary>
public sealed record PluginCapabilities(
    bool StartsProcesses,
    bool UsesNetwork,
    IReadOnlyList<string> Evidence)
{
    public static PluginCapabilities None { get; } = new(false, false, []);
}

/// <summary>
/// A static pre-warning for plugin assemblies. It reads referenced assemblies
/// and P/Invoke declarations, so it costs nothing at run time and happens before
/// any plugin code executes.
/// </summary>
/// <remarks>
/// This is a heuristic, not a proof. A plugin can reach the same APIs through
/// reflection, late binding, COM or another assembly. The point is to tell the
/// user "this plugin can start processes" up front, so the tool-call warning and
/// the kill button are not the first time they hear about it.
/// </remarks>
public static class PluginTrustScanner
{
    private static readonly string[] ProcessAssemblies =
    [
        "System.Diagnostics.Process",
        "System.Management",
    ];

    private static readonly string[] NetworkAssemblies =
    [
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.NetworkInformation",
    ];

    private static readonly string[] ProcessLibraries =
    [
        "kernel32", "kernel32.dll", "shell32", "shell32.dll",
        "ntdll", "ntdll.dll", "advapi32", "advapi32.dll", "user32", "user32.dll",
    ];

    public static PluginCapabilities Scan(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var evidence = new List<string>();
        bool processes = false;
        bool network = false;

        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            string name = reference.Name ?? "";
            if (ProcessAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                processes = true;
                evidence.Add($"references {name}");
            }
            if (NetworkAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                network = true;
                evidence.Add($"references {name}");
            }
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(type => type is not null).Cast<Type>()];
        }

        foreach (Type type in types)
        {
            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static))
            {
                DllImportAttribute? import = method
                    .GetCustomAttributes(typeof(DllImportAttribute), inherit: false)
                    .OfType<DllImportAttribute>()
                    .FirstOrDefault();
                if (import is null) continue;

                string library = import.Value ?? "";
                if (!ProcessLibraries.Any(candidate =>
                        library.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                processes = true;
                evidence.Add($"P/Invoke {library}!{import.EntryPoint ?? method.Name}");
            }
        }

        return new PluginCapabilities(processes, network, [.. evidence.Distinct()]);
    }
}
