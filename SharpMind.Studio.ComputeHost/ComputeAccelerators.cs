using SharpMind.Core.Plugins;

namespace SharpMind.Studio.ComputeHost;

/// <summary>
/// The accelerator plugins found in <see cref="ComputeOptions.PluginsFolder"/>.
/// Loaded once at startup so training resolution and inference share the same
/// plugin instances, and so load failures are visible in <c>/health</c>.
/// </summary>
public sealed class ComputeAccelerators
{
    public ComputeAccelerators(ComputeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Plugins = AcceleratorLoader.LoadFrom(options.PluginsFolder, out List<string> warnings);
        Warnings = warnings;
    }

    public IReadOnlyList<IAcceleratorPlugin> Plugins { get; }
    public IReadOnlyList<string> Warnings { get; }

    public string? DefaultName => Plugins.FirstOrDefault()?.Name;

    public bool HasInference =>
        Plugins.Any(plugin => plugin.Capabilities.Any(
            capability => capability is SharpMind.Inference.IInferenceEngineFactory));

    public IAcceleratorPlugin? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Plugins.FirstOrDefault(plugin =>
            plugin.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
