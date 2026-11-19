using System.Reflection;
using SharpMind.Core;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Training;

namespace SharpMind.Studio.Services;

/// <summary>
/// Loads plugin assemblies embedded in an <c>.smm</c> when the user has enabled
/// them for a session. Nothing here runs unless a session names the assembly.
/// </summary>
/// <remarks>
/// Once an assembly is loaded into the process it cannot be unloaded, so the
/// cache keyed by model and selection avoids loading the same bytes twice. The
/// session options say so before the box is ticked.
/// </remarks>
public sealed class EmbeddedPluginHost
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<object>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<object> Resolve(
        string? modelPath,
        IReadOnlyCollection<string> names,
        out IReadOnlyList<string> warnings)
    {
        warnings = [];
        if (string.IsNullOrWhiteSpace(modelPath) || names.Count == 0) return [];

        string key = modelPath + "|" + string.Join(
            "|", names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out IReadOnlyList<object>? cached)) return cached;
        }

        var tools = new List<object>();
        var problems = new List<string>();

        foreach (SmmPluginEntry entry in SmmContentsReader.ReadPlugins(modelPath))
        {
            if (!names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;

            try
            {
                Assembly assembly = Assembly.Load(entry.AssemblyBytes);
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
                    if (!type.IsClass || type.IsAbstract ||
                        type.GetConstructor(Type.EmptyTypes) is null)
                    {
                        continue;
                    }

                    bool hasTools = type
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                        .Any(method => method
                            .GetCustomAttributes(typeof(ToolDescAttribute), inherit: false).Length > 0);
                    if (!hasTools) continue;

                    tools.Add(Activator.CreateInstance(type)!);
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{entry.Name}: {ex.GetBaseException().Message}");
            }
        }

        lock (_gate) _cache[key] = tools;
        warnings = problems;
        return tools;
    }
}
