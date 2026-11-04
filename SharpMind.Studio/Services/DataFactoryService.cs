using SharpMind.Studio.Core.Chat;
using SharpMind.Studio.Core.DataFactory;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.OpenRouter;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

/// <summary>
/// UI state and execution for the Data Factory: the current recipe, saved
/// recipes, input preview, and one run at a time. Provider construction lives
/// here so the runner stays UI-free.
/// </summary>
public sealed class DataFactoryService : IDisposable
{
    private readonly ModelCatalogService _catalog;
    private readonly InferenceSettingsService _inference;
    private readonly StudioWorkspaceService _workspace;
    private readonly StudioModelHost _host;
    private readonly OpenRouterSettingsService _openRouter;
    private readonly OpenRouterClient _openRouterClient;
    private readonly DataFactoryRecipeStore _store;

    private CancellationTokenSource? _cts;

    public DataFactoryService(
        ModelCatalogService catalog,
        InferenceSettingsService inference,
        StudioWorkspaceService workspace,
        StudioModelHost host,
        OpenRouterSettingsService openRouter,
        OpenRouterClient openRouterClient)
    {
        _catalog = catalog;
        _inference = inference;
        _workspace = workspace;
        _host = host;
        _openRouter = openRouter;
        _openRouterClient = openRouterClient;
        _store = new DataFactoryRecipeStore(StudioPaths.DataFactory);
        Recipe = DataFactoryRecipeStore.NewRecipe();
        _openRouter.Changed += OnOpenRouterChanged;
    }

    public event Action? Changed;

    public DataFactoryRecipe Recipe { get; private set; }
    public IReadOnlyList<string> SavedRecipes => _store.List();
    public IReadOnlyList<ModelDescriptor> LocalModels { get; private set; } = [];
    public IReadOnlyList<string> RemoteModels => _openRouter.ActiveModels;
    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Ready.";
    public string? Error { get; private set; }
    public string? InputPreview { get; private set; }
    public DataFactoryRunResult? Result { get; private set; }
    public string? CurrentRecipePath { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            LocalModels = await _catalog.RefreshAsync(cancellationToken: cancellationToken);
        }
        catch
        {
            LocalModels = _catalog.Models;
        }

        EnsureDefaults();
        Notify();
    }

    public void NewRecipe()
    {
        Recipe = DataFactoryRecipeStore.NewRecipe();
        CurrentRecipePath = null;
        InputPreview = null;
        Result = null;
        Error = null;
        Status = "New recipe.";
        EnsureDefaults();
        Notify();
    }

    public void LoadRecipe(string path)
    {
        try
        {
            Recipe = _store.Load(path);
            CurrentRecipePath = path;
            InputPreview = null;
            Result = null;
            Error = null;
            Status = $"Loaded {Path.GetFileName(path)}.";
            EnsureDefaults();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Could not load recipe.";
        }
        Notify();
    }

    public void SaveRecipe()
    {
        try
        {
            CurrentRecipePath = _store.Save(Recipe, CurrentRecipePath);
            Status = $"Saved {Path.GetFileName(CurrentRecipePath)}.";
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Could not save recipe.";
        }
        Notify();
    }

    public void DeleteRecipe(string path)
    {
        try
        {
            _store.Delete(path);
            if (string.Equals(CurrentRecipePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Recipe = DataFactoryRecipeStore.NewRecipe();
                CurrentRecipePath = null;
            }
            Status = $"Deleted {Path.GetFileName(path)}.";
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Could not delete recipe.";
        }
        Notify();
    }

    public void UpdateRecipe(Action<DataFactoryRecipe> update)
    {
        update(Recipe);
        Notify();
    }

    public void EnsureRecipeDefaults()
    {
        EnsureDefaults();
        Notify();
    }

    public async Task PreviewAsync(int maxDocuments = 5, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        Error = null;
        InputPreview = null;
        Status = "Reading source…";
        Notify();

        try
        {
            var builder = new System.Text.StringBuilder();
            int count = 0;
            await foreach (DataFactoryDocument document in DataFactoryInput.ReadAsync(Recipe, cancellationToken))
            {
                builder.AppendLine($"[{document.Index}] {Path.GetFileName(document.Source)} · {document.Text.Length:N0} chars");
                string preview = document.Text.Length <= 600
                    ? document.Text
                    : document.Text[..600] + "…";
                builder.AppendLine(preview);
                builder.AppendLine();
                count++;
                if (count >= Math.Max(1, maxDocuments)) break;
            }

            InputPreview = count == 0
                ? "No documents passed the source and filter rules."
                : builder.ToString();
            Status = $"Preview: {count} document(s).";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Preview failed.";
        }
        Notify();
    }

    public Task RunAsync(int? maxRecords = null, CancellationToken cancellationToken = default)
        => RunCoreAsync(Recipe, maxRecords, sample: false, cancellationToken);

    public async Task SampleAsync(int count = 3, CancellationToken cancellationToken = default)
    {
        string temp = Path.Combine(
            Path.GetTempPath(),
            "sharpmind-factory-sample-" + Guid.NewGuid().ToString("N") + ".jsonl");
        DataFactoryRecipe sampleRecipe = Recipe with
        {
            OutputPath = temp,
            MaxRecords = Math.Max(1, count),
        };

        await RunCoreAsync(sampleRecipe, Math.Max(1, count), sample: true, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch
        {
        }

        if (Result is not null)
            Result = Result with { OutputPath = Recipe.OutputPath };
    }

    private async Task RunCoreAsync(
        DataFactoryRecipe recipe,
        int? maxRecords,
        bool sample,
        CancellationToken cancellationToken)
    {
        if (IsRunning) return;

        Error = null;
        Result = null;
        IsRunning = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Status = sample ? "Generating a sample…" : "Starting…";
        Notify();

        IDataFactoryGenerator? generator = null;
        try
        {
            DataFactoryRecipe runRecipe = recipe with { };
            generator = await CreateGeneratorAsync(runRecipe, _cts.Token);
            var progress = new Progress<DataFactoryRunProgress>(report =>
            {
                Status = $"Processed {report.Processed:N0} · wrote {report.Written:N0} · skipped {report.Skipped:N0}";
                Notify();
            });

            Result = await DataFactoryRunner.RunAsync(
                runRecipe, generator, progress, maxRecords, _cts.Token);
            Status = Result.Written == 0
                ? $"Finished with no records written ({Result.ParseFailures:N0} parse failure(s))."
                : sample
                    ? $"Sample: {Result.Written:N0} record(s)."
                    : $"Done: {Result.Written:N0} record(s) written to {Path.GetFileName(Result.OutputPath)}.";
            if (runRecipe.OutputFormat == DataFactoryOutputFormat.ChatJsonl && Result.ParseFailures > 0)
                Status += $" {Result.ParseFailures:N0} response(s) were not JSON/Q&A and were skipped.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Run failed.";
        }
        finally
        {
            if (generator is not null)
            {
                try { await generator.DisposeAsync(); } catch { }
            }
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            Notify();
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    private async Task<IDataFactoryGenerator> CreateGeneratorAsync(
        DataFactoryRecipe recipe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipe.SourcePath))
            throw new InvalidOperationException("Choose a source path first.");
        if (string.IsNullOrWhiteSpace(recipe.OutputPath))
            throw new InvalidOperationException("Choose an output file first.");

        if (recipe.Provider == DataFactoryProviderKind.OpenRouter)
        {
            if (!_openRouter.Connection.IsUsable)
                throw new InvalidOperationException("OpenRouter is not configured. Add an API key in Settings.");
            if (string.IsNullOrWhiteSpace(recipe.RemoteModel))
                throw new InvalidOperationException("Choose an OpenRouter model for this recipe.");
            return new OpenRouterDataGenerator(_openRouterClient, _openRouter.Connection);
        }

        LocalModels = await _catalog.RefreshAsync(cancellationToken: cancellationToken);
        ModelDescriptor model = _catalog.Find(recipe.LocalModelPath)
            ?? throw new InvalidOperationException("Choose a local model for this recipe.");
        ResolvedInference profile = _inference.Resolve(model.Family.Id, model.Path, null);
        return await LocalDataFactoryGenerator.CreateAsync(
            _host, model, profile, _workspace.Builders, cancellationToken);
    }

    private void EnsureDefaults()
    {
        if (Recipe.Provider == DataFactoryProviderKind.OpenRouter
            && string.IsNullOrWhiteSpace(Recipe.RemoteModel))
        {
            Recipe.RemoteModel = PickFactoryModel();
        }

        if (Recipe.Provider == DataFactoryProviderKind.Local
            && string.IsNullOrWhiteSpace(Recipe.LocalModelPath))
        {
            Recipe.LocalModelPath = LocalModels.FirstOrDefault()?.Path;
        }

        if (string.IsNullOrWhiteSpace(Recipe.OutputPath))
        {
            string name = string.IsNullOrWhiteSpace(Recipe.Name) ? "factory" : Recipe.Name;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            Recipe.OutputPath = Path.Combine(StudioPaths.Corpus, name + ".jsonl");
        }
    }

    private string? PickFactoryModel()
    {
        return _openRouter.ActiveModels.FirstOrDefault(model =>
                   model.Equals("openrouter/free", StringComparison.OrdinalIgnoreCase))
               ?? _openRouter.ActiveModels.FirstOrDefault(model =>
                   model.Contains(":free", StringComparison.OrdinalIgnoreCase))
               ?? _openRouter.DefaultModel
               ?? _openRouter.ActiveModels.FirstOrDefault();
    }

    private void OnOpenRouterChanged()
    {
        if (Recipe.Provider == DataFactoryProviderKind.OpenRouter)
            EnsureDefaults();
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _openRouter.Changed -= OnOpenRouterChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
