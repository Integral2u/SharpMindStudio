using System.Reflection;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Core.Training;

/// <summary>
/// Loads the planner rule set: <c>Training/rules/studio-training.rules.json</c>
/// when present, otherwise the copy embedded in Studio.Core. An invalid user
/// file falls back to the embedded default and reports a warning rather than
/// taking the lab down.
/// </summary>
public static class TrainingRuleSetStore
{
    private const string ResourceName =
        "SharpMind.Studio.Core.Training.Rules.studio-training.rules.json";

    public static string Folder => Path.Combine(StudioPaths.Training, "rules");
    public static string DefaultFile => Path.Combine(Folder, "studio-training.rules.json");

    public static TrainingRuleSet Load(out string source, out string? warning)
    {
        warning = null;

        if (File.Exists(DefaultFile))
        {
            try
            {
                source = DefaultFile;
                return TrainingRuleSet.Load(File.ReadAllText(DefaultFile));
            }
            catch (Exception ex)
            {
                warning = $"Rule file ignored: {ex.Message}";
            }
        }

        try
        {
            if (!File.Exists(DefaultFile))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(DefaultFile, ReadEmbedded());
            }
        }
        catch
        {
            // A read-only install still plans from the embedded copy.
        }

        source = "embedded";
        return TrainingRuleSet.Load(ReadEmbedded());
    }

    /// <summary>Writes the embedded rule file for editing when none exists.</summary>
    public static void EnsureDefaultFile()
    {
        try
        {
            if (File.Exists(DefaultFile)) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(DefaultFile, ReadEmbedded());
        }
        catch
        {
            // Read-only installs keep using the embedded copy.
        }
    }

    public static string ReadEmbedded()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded rule set '{ResourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
