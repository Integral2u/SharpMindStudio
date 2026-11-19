using System.Reflection;
using System.Runtime.InteropServices;
using SharpMind.Model.Format;

namespace SharpMind.Studio.Core.Training;

/// <summary>A tool found inside an <c>.smm</c> container, before anything runs.</summary>
public sealed record EmbeddedToolInfo(string Tool, string Assembly, bool StartsProcesses);

/// <summary>
/// Lists the tools an <c>.smm</c> carries without executing the assembly. It
/// reads metadata through a <see cref="MetadataLoadContext"/>, so no module
/// initialiser or static constructor runs just to fill in a permission grid.
/// </summary>
public static class EmbeddedToolCatalog
{
    public static IReadOnlyList<EmbeddedToolInfo> Read(string? modelPath)
    {
        var tools = new List<EmbeddedToolInfo>();

        foreach (SmmPluginEntry entry in SmmContentsReader.ReadPlugins(modelPath))
        {
            try
            {
                foreach (string name in ToolNames(entry.AssemblyBytes, out bool processes))
                    tools.Add(new EmbeddedToolInfo(name, entry.Name, processes));
            }
            catch
            {
                // A malformed assembly is reported by the container summary, not here.
            }
        }

        return [.. tools
            .GroupBy(tool => tool.Tool, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static IReadOnlyList<string> ToolNames(byte[] bytes, out bool startsProcesses)
    {
        startsProcesses = false;

        var resolver = new PathAssemblyResolver(
            Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
        using var context = new MetadataLoadContext(resolver);
        using var stream = new MemoryStream(bytes);

        Assembly assembly = context.LoadFromStream(stream);
        startsProcesses = assembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is "System.Diagnostics.Process" or "System.Management");

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(type => type is not null).Cast<Type>()];
        }

        var names = new List<string>();
        foreach (Type type in types)
        {
            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                bool isTool = method.GetCustomAttributesData().Any(attribute =>
                    attribute.AttributeType.Name == "ToolDescAttribute");
                if (isTool) names.Add(method.Name);
            }
        }

        return names;
    }
}
