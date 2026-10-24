using System.Reflection;
using SharpMind.Core;
using SharpMind.Inference;
using SharpMind.Model;

namespace SharpMind.Studio.Core.Inference;

public sealed record GeneratorOption(string Name, Type BuilderType, Type? CacheType, bool IsPlugin);

public sealed record CacheOption(string Name, Type Type, bool IsPlugin);

public sealed record BuilderCatalogResult(
    IReadOnlyList<GeneratorOption> Generators,
    IReadOnlyList<CacheOption> Caches,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Built-ins plus anything discoverable in the plugins folder. The CUI uses the
/// same reflection contract (IGeneratorBuilder&lt;T&gt; / IKVCacheBuilder); Studio
/// keeps its own copy so it never references the CUI assembly.
/// </summary>
public static class BuilderCatalog
{
    public static BuilderCatalogResult Discover(string? pluginsFolder)
    {
        var caches = new List<CacheOption>
        {
            new("KVCacherBuilder", typeof(KVCacherBuilder), false),
            new("Int8KVCacherBuilder", typeof(Int8KVCacherBuilder), false),
            new("PagedKVCacherBuilder", typeof(PagedKVCacherBuilder), false),
            new("QuantizedKVCacherBuilder", typeof(QuantizedKVCacherBuilder), false),
        };
        var generators = new List<GeneratorOption>
        {
            new("StandardGeneratorBuilder", typeof(StandardGeneratorBuilder<>), null, false),
            new("SpeculativeGeneratorBuilder", typeof(SpeculativeGeneratorBuilder<>), null, false),
            new("MedusaGeneratorBuilder", typeof(MedusaGeneratorBuilder<>), null, false),
        };
        var warnings = new List<string>();
        var cacheNames = new HashSet<string>(caches.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var generatorNames = new HashSet<string>(generators.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(pluginsFolder) || !Directory.Exists(pluginsFolder))
            return new(generators, caches, warnings);

        PluginAssemblyResolver.RegisterDirectory(pluginsFolder);
        foreach (string dll in Directory.EnumerateFiles(pluginsFolder, "*.dll", SearchOption.AllDirectories))
        {
            Assembly assembly;
            try
            {
                PluginAssemblyResolver.RegisterDirectory(Path.GetDirectoryName(dll));
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
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null).Cast<Type>()];
                warnings.Add($"{assembly.GetName().Name}: some types could not be loaded.");
            }

            foreach (Type type in types)
            {
                if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                    continue;

                if (typeof(IKVCacheBuilder).IsAssignableFrom(type) && cacheNames.Add(type.Name))
                {
                    caches.Add(new(type.Name, type, true));
                    continue;
                }

                foreach (Type iface in type.GetInterfaces())
                {
                    if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IGeneratorBuilder<>))
                        continue;
                    Type cacheType = iface.GetGenericArguments()[0];
                    if (generatorNames.Add(type.Name))
                        generators.Add(new(type.Name, type, cacheType, true));
                    break;
                }
            }
        }

        return new(generators, caches, warnings);
    }
}
