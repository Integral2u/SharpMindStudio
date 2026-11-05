namespace SharpMind.Studio.Core.Storage;

/// <summary>
/// The same user-visible tree the CUI uses, so models, corpora and training
/// output are where an existing SharpMind user already keeps them:
///
///   %USERPROFILE%\SharpMind\Models
///   %USERPROFILE%\SharpMind\Corpus
///   %USERPROFILE%\SharpMind\Training
///   %USERPROFILE%\SharpMind\Chat Sessions
///
/// Studio's own project/settings live under the root, not inside the CUI's
/// terminal-only folders.
/// </summary>
public static class StudioPaths
{
    private static string? _rootOverride;

    public static void UseRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _rootOverride = Path.GetFullPath(root);
    }

    public static string Root => _rootOverride ?? Path.Combine(GetBase(), "SharpMind");
    public static string Models => Path.Combine(Root, "Models");
    public static string Corpus => Path.Combine(Root, "Corpus");
    public static string Training => Path.Combine(Root, "Training");
    public static string DataFactory => Path.Combine(Training, "DataFactory");
    public static string ChatSessions => Path.Combine(Root, "Chat Sessions");
    public static string Projects => Path.Combine(Root, "Studio");
    public static string Workspaces => Path.Combine(Root, "Workspaces");
    public static string Skills => Path.Combine(Root, "Skills");
    public static string WorkspaceFile => Path.Combine(Projects, "workspace.json");
    public static string SettingsFile => Path.Combine(Projects, "settings.json");
    public static string InferenceFile => Path.Combine(Projects, "inference.json");
    public static string WorkSkillsFile => Path.Combine(Projects, "work-skills.json");
    public static string WorkToolsFile => Path.Combine(Projects, "work-tools.json");
    public static string OpenRouterFile => Path.Combine(Projects, "openrouter.json");
    public static string OpenRouterKeyFile => Path.Combine(Projects, "openrouter.key");
    public static string HuggingFaceFile => Path.Combine(Projects, "huggingface.json");
    public static string HuggingFaceKeyFile => Path.Combine(Projects, "huggingface.key");
    public static string ComputeFile => Path.Combine(Projects, "compute.json");
    public static string ComputeKeyFile => Path.Combine(Projects, "compute.key");
    public static string Compute => Path.Combine(Root, "Compute");
    public static string Gateway => Path.Combine(Projects, "Gateway");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Corpus);
        Directory.CreateDirectory(Training);
        Directory.CreateDirectory(ChatSessions);
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Gateway);
        Directory.CreateDirectory(DataFactory);
        Directory.CreateDirectory(Workspaces);
        Directory.CreateDirectory(Skills);
        Directory.CreateDirectory(Compute);
    }

    private static string GetBase()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : profile;
    }
}






