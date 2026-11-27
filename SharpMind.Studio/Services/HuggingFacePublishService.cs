using SharpMind.Core.Quantization;
using SharpMind.Model.Format;
using SharpMind.Model.Format.Conversion;
using SharpMind.Studio.Core.HuggingFace;
using SharpMind.Studio.Core.Training;

namespace SharpMind.Studio.Services;

/// <summary>User choices for publishing one model to HuggingFace.</summary>
public sealed record PublishModelRequest(
    string SourcePath,
    string RepoId,
    bool Private,
    string License,
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string ArtifactFileName,
    string Quantization,
    bool IncludeSmm,
    bool ConfirmEmbedded,
    string? BaseModel,
    ModelPublishRunInfo? Run);

public sealed record PublishModelResult(
    string RepoUrl,
    string CommitUrl,
    string ArtifactFileName,
    string? SmmFileName,
    long ArtifactBytes,
    long SmmBytes,
    ModelPublishRecord Record);

/// <summary>Result of an explicit repository check.</summary>
public sealed record ModelRepositoryCheck(
    string RepoId,
    bool Exists,
    bool Private,
    bool Gated,
    DateTimeOffset? LastModified)
{
    public string RepoUrl => $"https://huggingface.co/{RepoId}";
    public string SettingsUrl => $"{RepoUrl}/settings";
}

/// <summary>
/// Owns the one publish operation Studio can run at a time. The source model
/// is converted and quantized in a temporary folder, then uploaded as a GGUF
/// with a generated model card and metadata sidecar.
/// </summary>
public sealed class HuggingFacePublishService : IDisposable
{
    public static readonly IReadOnlyList<string> Quantizations =
        ["F16", "Q8_0", "Q6_K", "Q5_K", "Q4_K", "Q4_0", "Q2_K"];

    private readonly HuggingFaceClient _client;
    private readonly HuggingFaceService _huggingFace;
    private readonly ModelPublishHistoryStore _historyStore = new();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private string _stage = "Idle";
    private float _progress;
    private string _status = "Pick a model to publish.";
    private string? _error;
    private PublishModelResult? _result;
    private IReadOnlyList<ModelPublishRecord> _history;

    public HuggingFacePublishService(
        HuggingFaceClient client,
        HuggingFaceService huggingFace)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _huggingFace = huggingFace ?? throw new ArgumentNullException(nameof(huggingFace));
        _history = _historyStore.Load();
    }

    public event Action? Changed;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public string Stage { get { lock (_gate) return _stage; } }
    public float Progress { get { lock (_gate) return _progress; } }
    public string Status { get { lock (_gate) return _status; } }
    public string? Error { get { lock (_gate) return _error; } }
    public PublishModelResult? Result { get { lock (_gate) return _result; } }
    public IReadOnlyList<ModelPublishRecord> History { get { lock (_gate) return _history; } }

    public bool HasToken => _huggingFace.HasToken;

    public void ClearHistory()
    {
        _historyStore.Clear();
        lock (_gate) _history = [];
        Changed?.Invoke();
    }

    public async Task<ModelRepositoryCheck?> CheckRepositoryAsync(
        string repoId,
        CancellationToken cancellationToken = default)
    {
        string? token = _huggingFace.Token;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repoId))
            return null;

        HuggingFaceRepoInfo? info = await _client
            .GetModelInfoAsync(repoId.Trim(), token, cancellationToken)
            .ConfigureAwait(false);
        return info is null
            ? new ModelRepositoryCheck(repoId.Trim(), false, false, false, null)
            : new ModelRepositoryCheck(
                info.Id, true, info.Private, info.Gated, info.LastModified);
    }

    public async Task<HuggingFaceUser?> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        string? token = _huggingFace.Token;
        if (string.IsNullOrWhiteSpace(token)) return null;
        return await _client.WhoAmIAsync(token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a publish. Returns false when one is already running.</summary>
    public bool Start(PublishModelRequest request)
    {
        lock (_gate)
        {
            if (_running) return false;
            _running = true;
            _stage = "Preparing";
            _progress = 0;
            _status = "Preparing the model...";
            _error = null;
            _result = null;
            _cts = new CancellationTokenSource();
        }

        Changed?.Invoke();
        _ = Task.Run(() => RunAsync(request, _cts.Token));
        return true;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_running || _cts is null) return;
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                _status = "Cancelling...";
            }
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_running) return;
            _error = null;
            _result = null;
            _progress = 0;
            _stage = "Idle";
            _status = "Pick a model to publish.";
        }
        Changed?.Invoke();
    }

    private async Task RunAsync(PublishModelRequest request, CancellationToken cancellationToken)
    {
        string temp = Path.Combine(Path.GetTempPath(), "SharpMindPublish", Guid.NewGuid().ToString("N"));
        try
        {
            string? token = _huggingFace.Token;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "Add a HuggingFace write token in Settings before publishing.");

            string source = Path.GetFullPath(request.SourcePath);
            if (!File.Exists(source))
                throw new FileNotFoundException("The model to publish was not found.", source);
            bool sourceIsSmm = source.EndsWith(".smm", StringComparison.OrdinalIgnoreCase);
            bool sourceIsGguf = source.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
            if (!sourceIsSmm && !sourceIsGguf)
                throw new NotSupportedException("Publish accepts .smm and .gguf models.");

            if (request.IncludeSmm && !sourceIsSmm)
                throw new InvalidOperationException(
                    "Only an .smm source can be uploaded alongside the GGUF.");

            string artifactName = Path.GetFileName(request.ArtifactFileName);
            if (string.IsNullOrWhiteSpace(artifactName) ||
                !artifactName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(artifactName, request.ArtifactFileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The published GGUF name must be a file name ending in .gguf.");
            }

            ModelPublishMetadata sourceInfo = ModelPublishMetadata.Read(source);
            if (request.IncludeSmm && sourceInfo.HasEmbeddedExtras && !request.ConfirmEmbedded)
                throw new InvalidOperationException(
                    "This .smm contains embedded plugins, skills or a system prompt. " +
                    "Confirm that they may be uploaded.");

            // Check the repository before spending minutes converting and quantizing.
            // An existing repository is also how a second quantization is added.
            Report("Creating repository", 0.02f, $"Checking {request.RepoId}...");
            await _client.CreateModelRepoAsync(
                request.RepoId.Trim(), token, request.Private, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<string> otherArtifacts = [];
            try
            {
                IReadOnlyList<HuggingFaceRepoFile> committed =
                    await _client.ListModelFilesAsync(
                        request.RepoId.Trim(), token, cancellationToken)
                        .ConfigureAwait(false);
                otherArtifacts = [.. committed
                    .Where(file =>
                        (file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                         file.Path.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)) &&
                        !string.Equals(file.Path, artifactName, StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Path)];
            }
            catch
            {
                // A write-only token may not be able to list the repo; publishing still works.
            }

            Directory.CreateDirectory(temp);
            string converted = Path.Combine(temp, "source.gguf");
            string artifact = Path.Combine(temp, artifactName);

            Report("Converting", 0.05f, "Converting the model to GGUF...");
            if (sourceIsSmm)
            {
                await Task.Run(
                    () => SmmToGufConverter.Convert(
                        source, converted,
                        new Progress<float>(value => Report(
                            "Converting", 0.05f + value * 0.30f,
                            $"Converting to GGUF... {(value * 100):0}%")),
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Copy(source, converted, overwrite: true);
            }

            QuantDType target = ParseQuantization(request.Quantization);
            Report("Quantizing", 0.38f, $"Quantizing to {request.Quantization}...");
            await Task.Run(
                () => GgufQuantizer.Quantize(
                    converted, artifact, target,
                    progress: (done, total) => Report(
                        "Quantizing", 0.38f + (total > 0 ? (float)done / total : 1) * 0.27f,
                        $"Quantizing to {request.Quantization}... {done}/{total} tensors"),
                    cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);

            ModelPublishMetadata artifactInfo = ModelPublishMetadata.Read(artifact);
            ModelPublishMetadata cardInfo = artifactInfo with
            {
                Checksum = sourceInfo.Checksum ?? artifactInfo.Checksum,
                Lineage = sourceInfo.Lineage,
                Skills = sourceInfo.Skills,
                HasSystemPrompt = sourceInfo.HasSystemPrompt,
                Plugins = sourceInfo.Plugins,
            };

            var card = new ModelPublishCard(
                request.RepoId.Trim(),
                string.IsNullOrWhiteSpace(request.Title)
                    ? Path.GetFileNameWithoutExtension(artifactName)
                    : request.Title.Trim(),
                request.Description,
                request.License,
                request.Tags,
                request.BaseModel,
                artifactName,
                request.Quantization,
                request.IncludeSmm,
                request.IncludeSmm ? Path.GetFileName(source) : null,
                request.Run,
                otherArtifacts);

            string readme = ModelPublishCardBuilder.BuildReadme(cardInfo, card);
            string metadataJson = ModelPublishCardBuilder.BuildMetadataJson(cardInfo, card);
            string gitAttributes = ModelPublishCardBuilder.BuildGitAttributes();

            var files = new List<HuggingFaceUploadFile>
            {
                HuggingFaceUploadFile.FromPath(artifactName, artifact),
                HuggingFaceUploadFile.FromText("README.md", readme),
                HuggingFaceUploadFile.FromText("sharp-mind.json", metadataJson),
                HuggingFaceUploadFile.FromText(".gitattributes", gitAttributes),
            };
            string? smmName = null;
            if (request.IncludeSmm)
            {
                smmName = Path.GetFileName(source);
                files.Add(HuggingFaceUploadFile.FromPath(smmName, source));
            }

            Report("Uploading", 0.70f, "Uploading files...");
            var progress = new Progress<HuggingFaceUploadProgress>(report =>
            {
                float mapped = 0.70f + (float)(report.Percent ?? 0) * 0.27f;
                Report(report.Stage, mapped, StatusFor(report));
            });
            HuggingFaceCommitResult commit = await _client.UploadFilesAsync(
                request.RepoId.Trim(),
                files,
                token,
                commitMessage: $"Publish {artifactName} from SharpMind Studio",
                progress: progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            HuggingFaceRepoInfo? repoInfo = null;
            try
            {
                repoInfo = await _client.GetModelInfoAsync(
                    request.RepoId.Trim(), token, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The upload already succeeded; visibility is best-effort here.
            }

            long artifactBytes = new FileInfo(artifact).Length;
            long smmBytes = smmName is null ? 0 : new FileInfo(source).Length;
            var record = new ModelPublishRecord
            {
                RepoId = request.RepoId.Trim(),
                RepoUrl = commit.RepoUrl,
                CommitUrl = commit.CommitUrl,
                CommitOid = commit.CommitOid,
                ArtifactFileName = artifactName,
                SmmFileName = smmName,
                Quantization = request.Quantization,
                Private = repoInfo?.Private ?? request.Private,
                PublishedUtc = DateTimeOffset.UtcNow,
                SourceFileName = Path.GetFileName(source),
                SourceChecksum = sourceInfo.Checksum,
                ArtifactBytes = artifactBytes,
                SmmBytes = smmBytes,
            };
            _historyStore.Add(record);
            var result = new PublishModelResult(
                commit.RepoUrl,
                commit.CommitUrl,
                artifactName,
                smmName,
                artifactBytes,
                smmBytes,
                record);

            lock (_gate)
            {
                _history = _historyStore.Load();
                _result = result;
                _error = null;
                _progress = 1;
                _stage = "Done";
                _status = $"Published {artifactName}.";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _stage = "Cancelled";
                _status = "Publish cancelled.";
                _error = null;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _stage = "Failed";
                _error = $"{ex.GetType().Name}: {ex.Message}";
                _status = "Publish failed.";
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            lock (_gate)
            {
                _running = false;
                _cts?.Dispose();
                _cts = null;
            }
            Changed?.Invoke();
        }
    }

    private void Report(string stage, float progress, string status)
    {
        lock (_gate)
        {
            _stage = stage;
            _progress = Math.Clamp(progress, 0, 1);
            _status = status;
        }
        Changed?.Invoke();
    }

    private static string StatusFor(HuggingFaceUploadProgress progress) =>
        progress.FileName is { Length: > 0 } file
            ? $"{progress.Stage} {file}..."
            : $"{progress.Stage}...";

    public static QuantDType ParseQuantization(string value) => value.Trim().ToUpperInvariant() switch
    {
        "F16" => QuantDType.F16,
        "Q8_0" => QuantDType.Q8_0,
        "Q6_K" => QuantDType.Q6_K,
        "Q5_K" => QuantDType.Q5_K,
        "Q4_K" => QuantDType.Q4_K,
        "Q4_0" => QuantDType.Q4_0,
        "Q2_K" => QuantDType.Q2_K,
        _ => throw new InvalidOperationException($"Unsupported quantization '{value}'."),
    };

    public static string DefaultArtifactName(string sourcePath, string quantization)
    {
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (string suffix in new[]
        {
            "_F16", "_Q8_0", "_Q6_K", "_Q5_K", "_Q4_K", "_Q4_0", "_Q2_K",
            "-F16", "-Q8_0", "-Q6_K", "-Q5_K", "-Q4_K", "-Q4_0", "-Q2_K",
        })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        return $"{name}_{quantization}.gguf";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
