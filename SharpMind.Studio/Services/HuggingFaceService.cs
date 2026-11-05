using SharpMind.Studio.Core.HuggingFace;
using SharpMind.Studio.Core.Storage;

namespace SharpMind.Studio.Services;

/// <summary>
/// Models-tab state for HuggingFace: search, file selection and resumable
/// downloads into the local Models folder.
/// </summary>
public sealed class HuggingFaceService : IDisposable
{
    private readonly HuggingFaceClient _client;
    private readonly HuggingFaceSettingsStore _store;
    private CancellationTokenSource? _cts;

    public HuggingFaceService(HuggingFaceClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        StudioPaths.EnsureCreated();
        _store = new HuggingFaceSettingsStore(
            StudioPaths.HuggingFaceFile,
            StudioPaths.HuggingFaceKeyFile);
    }

    public event Action? Changed;

    public string Query { get; set; } = "";
    public IReadOnlyList<HuggingFaceRepo> Results { get; private set; } = [];
    public HuggingFaceRepo? SelectedRepo { get; private set; }
    public IReadOnlyList<HuggingFaceFile> Files { get; private set; } = [];
    public bool IsSearching { get; private set; }
    public bool IsLoadingFiles { get; private set; }
    public bool IsDownloading { get; private set; }
    public string Status { get; private set; } = "Search HuggingFace for a GGUF model.";
    public string? Error { get; private set; }
    public string? DownloadPath { get; private set; }
    public long DownloadedBytes { get; private set; }
    public long? TotalBytes { get; private set; }
    public double? DownloadPercent { get; private set; }

    public bool HasToken => _store.HasToken;
    public string? Token => _store.ApiToken;

    public void SaveToken(string? token)
    {
        try
        {
            _store.SetToken(token);
            Error = null;
            Status = string.IsNullOrWhiteSpace(token)
                ? "HuggingFace token cleared."
                : "HuggingFace token saved.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Could not save the HuggingFace token.";
        }
        Notify();
    }

    public void ClearToken()
    {
        _store.ClearToken();
        Status = "HuggingFace token cleared.";
        Error = null;
        Notify();
    }

    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        if (IsSearching) return;
        IsSearching = true;
        Error = null;
        SelectedRepo = null;
        Files = [];
        Status = string.IsNullOrWhiteSpace(Query)
            ? "Loading popular GGUF models…"
            : $"Searching for “{Query.Trim()}”…";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts.CancelAfter(TimeSpan.FromSeconds(30));
        Notify();

        try
        {
            Results = await _client.SearchAsync(
                Query, limit: 30, token: Token, cancellationToken: _cts.Token);
            Status = Results.Count == 0
                ? "No GGUF repositories matched that search."
                : $"{Results.Count} GGUF repositor{(Results.Count == 1 ? "y" : "ies")}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Search failed.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsSearching = false;
            Notify();
        }
    }

    public async Task SelectRepoAsync(
        HuggingFaceRepo repo,
        CancellationToken cancellationToken = default)
    {
        if (IsLoadingFiles || IsDownloading) return;
        SelectedRepo = repo;
        Files = repo.Files;
        IsLoadingFiles = true;
        Error = null;
        Status = $"Reading files in {repo.Id}…";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts.CancelAfter(TimeSpan.FromSeconds(60));
        Notify();

        try
        {
            IReadOnlyList<HuggingFaceFile> files = await _client.GetFilesAsync(
                repo.Id, Token, _cts.Token);
            Files = files;
            Status = files.Count == 0
                ? "This repository has no GGUF files."
                : $"{files.Count} GGUF file{(files.Count == 1 ? "" : "s")} in {repo.Id}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Loading files cancelled.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Could not read the repository files.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsLoadingFiles = false;
            Notify();
        }
    }

    public async Task<string?> DownloadAsync(
        HuggingFaceFile file,
        CancellationToken cancellationToken = default)
    {
        if (IsDownloading) return null;
        if (file.Size is <= 0)
        {
            Error = "The file size is invalid; refresh the repository before downloading.";
            Notify();
            return null;
        }

        string destination = UniqueDestination(StudioPaths.Models, Path.GetFileName(file.FileName));
        IsDownloading = true;
        Error = null;
        DownloadPath = null;
        DownloadedBytes = 0;
        TotalBytes = file.Size;
        DownloadPercent = 0;
        Status = $"Downloading {Path.GetFileName(file.FileName)}…";
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Notify();

        try
        {
            var progress = new Progress<HuggingFaceDownloadProgress>(report =>
            {
                DownloadedBytes = report.BytesReceived;
                TotalBytes = report.TotalBytes;
                DownloadPercent = report.Percent;
                Status = $"Downloading {Path.GetFileName(file.FileName)}…";
                Notify();
            });

            string path = await _client.DownloadAsync(
                file, destination, Token, progress, _cts.Token);
            DownloadPath = path;
            Status = $"Downloaded {Path.GetFileName(path)} to Models.";
            return path;
        }
        catch (OperationCanceledException)
        {
            Status = "Download cancelled; the .part file is kept for resume.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "Download failed.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsDownloading = false;
            Notify();
        }
        return null;
    }

    public void Cancel() => _cts?.Cancel();

    private static string UniqueDestination(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        if (!File.Exists(path) && !File.Exists(path + ".part")) return path;

        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; ; index++)
        {
            path = Path.Combine(folder, $"{name} ({index}){extension}");
            if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
        }
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
