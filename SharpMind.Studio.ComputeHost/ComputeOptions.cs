using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.ComputeHost;

/// <summary>Environment-driven settings for the SharpMind Compute host.</summary>
public sealed class ComputeOptions
{
    public string Token { get; init; } = "";
    public bool AllowAnonymous { get; init; }
    public string DataRoot { get; init; } = StudioPaths.Root;
    public string? ModelPath { get; init; }
    public string ModelsFolder { get; init; } = Path.Combine(StudioPaths.Root, "Models");
    public string PluginsFolder { get; init; } = Path.Combine(AppContext.BaseDirectory, "plugins");

    public static ComputeOptions FromEnvironment()
    {
        string dataRoot = Env("SHARPMIND_DATA")
            ?? StudioPaths.Root;

        string models = Env("SHARPMIND_MODELS_DIR")
            ?? Path.Combine(dataRoot, "Models");
        string plugins = Env("SHARPMIND_PLUGINS")
            ?? Path.Combine(AppContext.BaseDirectory, "plugins");

        string? model = Env("SHARPMIND_MODEL");
        if (string.IsNullOrWhiteSpace(model) && Directory.Exists(models))
        {
            model = Directory.EnumerateFiles(models, "*.smm", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? Directory.EnumerateFiles(models, "*.gguf", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
        }

        return new ComputeOptions
        {
            Token = Env("SHARPMIND_TOKEN") ?? "",
            AllowAnonymous = string.Equals(
                Env("SHARPMIND_ALLOW_ANONYMOUS"), "true", StringComparison.OrdinalIgnoreCase),
            DataRoot = Path.GetFullPath(dataRoot),
            ModelPath = string.IsNullOrWhiteSpace(model) ? null : Path.GetFullPath(model),
            ModelsFolder = Path.GetFullPath(models),
            PluginsFolder = Path.GetFullPath(plugins),
        };
    }

    public string ModelId =>
        string.IsNullOrWhiteSpace(ModelPath)
            ? "default"
            : Path.GetFileNameWithoutExtension(ModelPath);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) || AllowAnonymous;

    private static string? Env(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
