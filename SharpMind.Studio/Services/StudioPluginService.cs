using System.Reflection;
using SharpMind.Core;
using SharpMind.Core.Plugins;
using SharpMind.Inference;
using SharpMind.Inference.Chat;
using SharpMind.Model;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

public sealed record StudioPluginComponent(
    string Name,
    string Kind,
    string Source,
    string Detail);

/// <summary>
/// Discovers the plugin component types Studio can host, not just tool classes:
/// context compactors, prompt pre/post-processors, generator/cache builders and
/// accelerator plugins. Tools stay in <see cref="WorkToolService"/> because only
/// Work/agent sessions register them.
/// </summary>
public sealed class StudioPluginService
{
    private readonly StudioWorkspaceService _workspace;
    private readonly Lock _gate = new();
    private readonly List<IContextCompactor> _compactors = [];
    private readonly List<IPromptPreProcessor> _preProcessors = [];
    private readonly List<IPromptPostProcessor> _postProcessors = [];
    private readonly List<StudioPluginComponent> _components = [];
    private readonly List<string> _warnings = [];
    private IPromptPreProcessor? _preProcessor;
    private IPromptPostProcessor _postProcessor = new StudioArtifactPostProcessor();

    public StudioPluginService(StudioWorkspaceService workspace)
    {
        _workspace = workspace;
        Reload();
    }

    public event Action? Changed;

    public string Folder => _workspace.PluginsFolder;
    public IReadOnlyList<IContextCompactor> Compactors { get { lock (_gate) return [.. _compactors]; } }
    public IReadOnlyList<IPromptPreProcessor> PreProcessors { get { lock (_gate) return [.. _preProcessors]; } }
    public IReadOnlyList<IPromptPostProcessor> PostProcessors { get { lock (_gate) return [.. _postProcessors]; } }
    public IReadOnlyList<StudioPluginComponent> Components { get { lock (_gate) return [.. _components]; } }
    public IReadOnlyList<string> Warnings { get { lock (_gate) return [.. _warnings]; } }

    /// <summary>The composed pre-processor passed to a chat session.</summary>
    public IPromptPreProcessor? PreProcessor { get { lock (_gate) return _preProcessor; } }

    /// <summary>
    /// The composed post-processor: Studio's artifact extraction first, then
    /// every plugin post-processor in discovery order.
    /// </summary>
    public IPromptPostProcessor PostProcessor { get { lock (_gate) return _postProcessor; } }

    public IContextCompactor? FindCompactor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
        {
            return _compactors.FirstOrDefault(compactor =>
                string.Equals(compactor.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Reload()
    {
        var compactors = new List<IContextCompactor>();
        var preProcessors = new List<IPromptPreProcessor>();
        var postProcessors = new List<IPromptPostProcessor>();
        var components = new List<StudioPluginComponent>();
        var warnings = new List<string>();
        var compactorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var postNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    PluginAssemblyResolver.RegisterEmbeddedResources(assembly);
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

                string source = assembly.GetName().Name ?? Path.GetFileName(dll);
                foreach (Type type in types)
                {
                    if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    if (typeof(IContextCompactor).IsAssignableFrom(type))
                    {
                        try
                        {
                            var instance = (IContextCompactor)Activator.CreateInstance(type)!;
                            if (compactorNames.Add(instance.Name))
                            {
                                compactors.Add(instance);
                                components.Add(new StudioPluginComponent(
                                    instance.Name, "Compactor", source, type.FullName ?? type.Name));
                            }
                            else
                            {
                                warnings.Add($"Compactor '{instance.Name}' from '{source}' was already registered.");
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not instantiate compactor '{type.FullName}': {ex.GetBaseException().Message}");
                        }
                    }

                    if (typeof(IPromptPreProcessor).IsAssignableFrom(type))
                    {
                        try
                        {
                            var instance = (IPromptPreProcessor)Activator.CreateInstance(type)!;
                            if (preNames.Add(instance.Name))
                            {
                                preProcessors.Add(instance);
                                components.Add(new StudioPluginComponent(
                                    instance.Name, "Pre-processor", source, type.FullName ?? type.Name));
                            }
                            else
                            {
                                warnings.Add($"Pre-processor '{instance.Name}' from '{source}' was already registered.");
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not instantiate pre-processor '{type.FullName}': {ex.GetBaseException().Message}");
                        }
                    }

                    if (typeof(IPromptPostProcessor).IsAssignableFrom(type))
                    {
                        try
                        {
                            var instance = (IPromptPostProcessor)Activator.CreateInstance(type)!;
                            if (postNames.Add(instance.Name))
                            {
                                postProcessors.Add(instance);
                                components.Add(new StudioPluginComponent(
                                    instance.Name, "Post-processor", source, type.FullName ?? type.Name));
                            }
                            else
                            {
                                warnings.Add($"Post-processor '{instance.Name}' from '{source}' was already registered.");
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not instantiate post-processor '{type.FullName}': {ex.GetBaseException().Message}");
                        }
                    }

                    if (typeof(IAcceleratorPlugin).IsAssignableFrom(type))
                    {
                        components.Add(new StudioPluginComponent(
                            type.Name, "Accelerator", source, type.FullName ?? type.Name));
                    }

                    if (typeof(IKVCacheBuilder).IsAssignableFrom(type))
                    {
                        components.Add(new StudioPluginComponent(
                            type.Name, "Cache builder", source, type.FullName ?? type.Name));
                    }

                    foreach (Type iface in type.GetInterfaces())
                    {
                        if (!iface.IsGenericType ||
                            iface.GetGenericTypeDefinition() != typeof(IGeneratorBuilder<>))
                        {
                            continue;
                        }

                        components.Add(new StudioPluginComponent(
                            type.Name, "Generator builder", source, type.FullName ?? type.Name));
                        break;
                    }
                }
            }
        }

        lock (_gate)
        {
            _compactors.Clear();
            _compactors.AddRange(compactors);
            _preProcessors.Clear();
            _preProcessors.AddRange(preProcessors);
            _postProcessors.Clear();
            _postProcessors.AddRange(postProcessors);
            _components.Clear();
            _components.AddRange(components);
            _warnings.Clear();
            _warnings.AddRange(warnings);
            _preProcessor = preProcessors.Count == 0
                ? null
                : new CompositePromptPreProcessor(preProcessors);
            _postProcessor = new CompositePromptPostProcessor(
                [new StudioArtifactPostProcessor(), .. postProcessors]);
        }

        Changed?.Invoke();
    }
}

internal sealed class CompositePromptPreProcessor(
    IReadOnlyList<IPromptPreProcessor> processors) : IPromptPreProcessor
{
    public string Name => "Studio pre-processors";
    public string Description => string.Join(", ", processors.Select(processor => processor.Name));

    public async Task ProcessAsync(
        ChatMessage userInput,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct)
    {
        foreach (IPromptPreProcessor processor in processors)
            await processor.ProcessAsync(userInput, history, ct).ConfigureAwait(false);
    }
}

internal sealed class CompositePromptPostProcessor(
    IReadOnlyList<IPromptPostProcessor> processors) : IPromptPostProcessor
{
    public string Name => "Studio post-processors";
    public string Description => string.Join(", ", processors.Select(processor => processor.Name));

    public async Task ProcessAsync(
        ChatMessage agentMessage,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct)
    {
        foreach (IPromptPostProcessor processor in processors)
            await processor.ProcessAsync(agentMessage, history, ct).ConfigureAwait(false);
    }
}
