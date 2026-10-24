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
    public static string ChatSessions => Path.Combine(Root, "Chat Sessions");
    public static string Projects => Path.Combine(Root, "Studio");
    public static string SettingsFile => Path.Combine(Projects, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Corpus);
        Directory.CreateDirectory(Training);
        Directory.CreateDirectory(ChatSessions);
        Directory.CreateDirectory(Projects);
    }

    private static string GetBase()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : profile;
    }
}
