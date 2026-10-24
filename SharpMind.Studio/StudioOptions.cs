using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio;

public sealed class StudioOptions
{
    public string ProjectRoot { get; set; } = "Studio";
    public string Url { get; set; } = "http://127.0.0.1:5117";
    public string[] CanaryModels { get; set; } = [];
    public string? ModelsFolder { get; set; }
    public string? CorpusFolder { get; set; }
    public string? TrainingFolder { get; set; }
    public string? PluginsFolder { get; set; }

    public string ResolvedModelsFolder =>
        string.IsNullOrWhiteSpace(ModelsFolder) ? StudioPaths.Models : ModelsFolder;

    public string ResolvedCorpusFolder =>
        string.IsNullOrWhiteSpace(CorpusFolder) ? StudioPaths.Corpus : CorpusFolder;

    public string ResolvedTrainingFolder =>
        string.IsNullOrWhiteSpace(TrainingFolder) ? StudioPaths.Training : TrainingFolder;

    public string ResolvedPluginsFolder =>
        string.IsNullOrWhiteSpace(PluginsFolder)
            ? Path.Combine(AppContext.BaseDirectory, "plugins")
            : PluginsFolder;
}

