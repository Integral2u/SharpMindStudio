using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SharpMind.Data.Sources;
using SharpMind.Model.Format;
using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Core.Training;
using SharpMind.Studio.Services;
using SharpMind.Training;

namespace SharpMind.Studio.Components.Pages;

public partial class Training
{
    [SupplyParameterFromQuery(Name = "tab")]
    public string? TabQuery { get; set; }

    private string _tab = "new";
    private TrainingJobSettings _job = null!;
    private string _selectedJobPath = "";
    private string? _error;
    private bool _starting;
    private string? _computeProviderId;
    private IReadOnlyList<ComputeAcceleratorInfo> _remoteAccelerators = [];
    private string? _remoteAcceleratorError;

    private string _convertSource = "";
    private string _convertDestination = "";
    private bool _allowOverwrite;
    private string? _convertError;

    private string _tuneBase = "";
    private string _tuneCorpusPath = "";
    private bool _tuneSft;
    private string _tuneSftFormatter = "auto";
    private int _tuneSftSeqLen = 512;
    private bool _sftPreviewing;
    private CancellationTokenSource? _sftPreviewCancel;
    private SftDatasetPreview? _sftPreview;
    private string? _sftPreviewError;
    private string _sftPreviewStatus = "";
    private string _tuneName = "";
    private string _tuneExport = "";
    private int _tuneRank = 8;
    private float _tuneAlpha = 16f;
    private int _tuneSteps = 100;
    private double _tuneValidationPercent = 10;
    private double _tuneTestPercent = 10;
    private float _tuneLr = 2e-4f;
    private string _tuneOutput = "merged";
    private string _tuneAdapterPath = "";
    private string _tuneResume = "";
    private bool _tuneStartFresh = true;
    private string? _tuneError;
    private bool _tuning;
    private string _compareA = "";
    private string _compareB = "";
    private string? _promotionSummary;
    private string? _promotionPath;
    private string? _runNotice;
    private string? _planId;
    private string? _ruleSetVersion;
    private string _playbookStatus = "";
    private string _mergeSource = "";
    private string _mergeOutput = "";
    private string _mergeFormat = "jsonl";
    private string _mergeLayout = "auto";
    private string _mergeSeparator = "";
    private string _mergeDropPattern = "";
    private bool _mergeStripHtml = true;
    private bool _mergeNormalizeText = true;
    private bool _mergeDeduplicate = true;
    private string _mergeStatus = "";
    private string? _mergeError;
    private bool _merging;
    private IReadOnlyList<string>? _resumeOptions;
    private readonly HashSet<string> _tuneTargets =
        ["q_proj", "k_proj", "v_proj", "o_proj", "up_proj", "down_proj"];

    private static readonly (string Id, string Label)[] LoraTargets =
    [
        ("q_proj", "Attention Q"),
        ("k_proj", "Attention K"),
        ("v_proj", "Attention V"),
        ("o_proj", "Attention O"),
        ("gate_proj", "FFN gate"),
        ("up_proj", "FFN up"),
        ("down_proj", "FFN down"),
    ];

    private IReadOnlyList<FileInfo> Models
    {
        get
        {
            try
            {
                var found = new List<FileInfo>();
                foreach (string folder in new[] { Lab.Folder, StudioPaths.Models }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(folder)) continue;
                    var directory = new DirectoryInfo(folder);
                    found.AddRange(directory.GetFiles("*.smm", SearchOption.TopDirectoryOnly));
                    found.AddRange(directory.GetFiles("*.gguf", SearchOption.TopDirectoryOnly));
                    found.AddRange(directory.GetFiles("*" + TrainingJobSettings.AdapterExtension, SearchOption.TopDirectoryOnly));
                }

                return [.. found
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(50)];
            }
            catch
            {
                return [];
            }
        }
    }

    private IReadOnlyList<FileInfo> SmmModels =>
        [.. Models.Where(file => file.Extension.Equals(".smm", StringComparison.OrdinalIgnoreCase))];

    private IReadOnlyList<TrainingPlaybook> AllPlaybooks =>
        [.. TrainingPlaybooks.All, .. Lab.Playbooks.List().Select(TrainingPlaybookStore.ToPlaybook)];

    private TrainingCorpusEstimate CorpusEstimate => TrainingCorpusFacts.Estimate(_job);

    private static bool IsCustomPlaybook(TrainingPlaybook playbook) =>
        playbook.Id.StartsWith("custom:", StringComparison.Ordinal);

    /// <summary>Model files the converter understands; adapters are not convertible.</summary>
    private IReadOnlyList<FileInfo> ConvertibleModels =>
        [.. Models.Where(file => !file.Extension.Equals(TrainingJobSettings.AdapterExtension, StringComparison.OrdinalIgnoreCase))];

    private ModelConversionDirection? Direction => ModelConverter.Detect(_convertSource);

    private string DirectionLabel => Direction switch
    {
        ModelConversionDirection.SmmToGguf => "SMM to GGUF",
        ModelConversionDirection.GgufToSmm => "GGUF to SMM",
        _ => "Pick a .smm or .gguf source",
    };

    private bool CanConvert => Direction is not null && !string.IsNullOrWhiteSpace(_convertSource);

    private bool DestinationExists
    {
        get
        {
            string destination = _convertDestination.Trim();
            return destination.Length > 0 && File.Exists(destination);
        }
    }

    private string ConvertPercent => (Convert.Progress * 100).ToString("0") + "%";

    protected override void OnInitialized()
    {
        _job = Lab.Draft ?? CreateDefaultJob();
        Lab.Draft = _job;
        _mergeOutput = Path.Combine(StudioPaths.Corpus, "merged.jsonl");
        _mergeSource = _job.Sources
            .Select(source => source.Component.Args.GetValueOrDefault("path"))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? "";

        Lab.Changed += OnTrainingChanged;
        Convert.Changed += OnTrainingChanged;
        Exam.Changed += OnExamChanged;
    }

    protected override void OnParametersSet()
    {
        _tab = TabQuery is "new" or "improve" or "playbooks" or "data" or "runs" or "models"
            ? TabQuery
            : "new";
        if (_tab == "runs") EnsureCompareSelection();
    }

    public void Dispose()
    {
        Lab.Changed -= OnTrainingChanged;
        Convert.Changed -= OnTrainingChanged;
        Exam.Changed -= OnExamChanged;
        _sftPreviewCancel?.Cancel();
        _sftPreviewCancel?.Dispose();
    }

    private void OnTrainingChanged() => _ = InvokeAsync(() =>
    {
        if (_tab == "runs") EnsureCompareSelection();
        // A finished run may have written a new adapter checkpoint; the resume
        // picker should see it next time the Improve tab renders.
        if (!Lab.IsRunning) _resumeOptions = null;
        StateHasChanged();
    });

    private void OnExamChanged() => _ = InvokeAsync(StateHasChanged);

    private void OnModelDownloaded(string path) => _ = InvokeAsync(() =>
    {
        _convertSource = path;
        SuggestDestination();
        StateHasChanged();
    });

    private void PickSource(ChangeEventArgs args)
    {
        _convertSource = args.Value as string ?? "";
        SuggestDestination();
    }

    private void SuggestDestination()
    {
        _convertError = null;
        if (ModelConverter.Detect(_convertSource) is null)
        {
            _convertDestination = "";
            return;
        }

        _convertDestination = ModelConverter.SuggestDestination(_convertSource.Trim());
        _allowOverwrite = false;
    }

    private void StartConversion()
    {
        _convertError = null;
        if (Convert.IsRunning) return;

        string source = _convertSource.Trim();
        if (ModelConverter.Detect(source) is null)
        {
            _convertError = "Pick a .smm or .gguf source file.";
            return;
        }
        if (!File.Exists(source))
        {
            _convertError = $"Source model not found: {source}";
            return;
        }

        string destination = string.IsNullOrWhiteSpace(_convertDestination)
            ? ModelConverter.SuggestDestination(source)
            : _convertDestination.Trim();

        if (!Convert.Start(source, destination, _allowOverwrite))
            _convertError = "A conversion is already running.";
    }

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.0");

    private void PickBase(ChangeEventArgs args)
    {
        _tuneBase = args.Value as string ?? "";
        _tuneError = null;
        if (string.IsNullOrWhiteSpace(_tuneName) && File.Exists(_tuneBase))
            _tuneName = Path.GetFileNameWithoutExtension(_tuneBase) + "-lora";
    }

    private async Task BrowseTuneCorpus()
    {
        string pattern = _tuneSft ? "*.jsonl" : "*.txt";
        string? folder = null;
        try
        {
            string typed = _tuneCorpusPath.Trim();
            if (typed.Length > 0) folder = Path.GetDirectoryName(typed);
        }
        catch
        {
            folder = null;
        }

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            folder = Directory.Exists(StudioPaths.Corpus) ? StudioPaths.Corpus : null;

        PathPickerSelection? selection = await Picker.PickAsync(
            PathPickerMode.File,
            folder,
            _tuneSft ? "Choose a chat JSONL corpus" : "Choose a text corpus",
            pattern);
        if (selection?.Path is { Length: > 0 } path)
        {
            _tuneCorpusPath = path;
            _tuneError = null;
        }
    }

    private void ToggleTarget(string id, ChangeEventArgs args)
    {
        if (args.Value is not bool wanted) return;
        if (wanted) _tuneTargets.Add(id);
        else _tuneTargets.Remove(id);
    }

    private void PickResume(ChangeEventArgs args)
        => _tuneResume = args.Value as string ?? "";

    private IReadOnlyList<string> AdapterResumeOptions => _resumeOptions ??= LoadAdapterResumeOptions();

    private IReadOnlyList<string> LoadAdapterResumeOptions()
    {
        try
        {
            var found = new List<string>();
            foreach (string folder in new[] { StudioPaths.Training, StudioPaths.Models }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(folder)) continue;
                found.AddRange(Directory.GetFiles(
                    folder, "*" + TrainingJobSettings.AdapterExtension, SearchOption.TopDirectoryOnly));
                foreach (string checkpoints in Directory.GetDirectories(folder, "checkpoints-*"))
                    found.AddRange(Directory.GetFiles(
                        checkpoints, "adapters.smlora", SearchOption.AllDirectories));
            }

            return [.. found
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(20)];
        }
        catch
        {
            return [];
        }
    }

    private static string ResumeLabel(string path)
    {
        string label = Path.GetRelativePath(StudioPaths.Training, path);
        return label.StartsWith("..", StringComparison.Ordinal) ? path : label;
    }

    private TrainingJobSettings? BuildFineTuneJob(out string? error)
    {
        error = null;

        string basePath = _tuneBase.Trim();
        if (!File.Exists(basePath))
        {
            error = $"Base model not found: {basePath}";
            return null;
        }
        if (!Path.GetExtension(basePath).Equals(".smm", StringComparison.OrdinalIgnoreCase))
        {
            error = "A fine-tune base must be a packaged .smm. Convert a .gguf on the Models tab first.";
            return null;
        }

        string corpus = _tuneCorpusPath.Trim();
        if (corpus.Length == 0)
        {
            error = "Point at the corpus of new or corrective text first.";
            return null;
        }
        if (Directory.Exists(corpus))
            corpus = Path.Combine(corpus, "**", _tuneSft ? "*.jsonl" : "*.txt");
        else if (!corpus.Contains('*') && !corpus.Contains('?') && !File.Exists(corpus))
        {
            error = $"Corpus not found: {corpus}";
            return null;
        }

        if (_tuneTargets.Count == 0)
        {
            error = "Pick at least one projection to adapt.";
            return null;
        }

        if (_tuneValidationPercent + _tuneTestPercent >= 100)
        {
            error = "Validation and held-out exam splits together must stay below 100%.";
            return null;
        }

        int rank = Math.Max(1, _tuneRank);
        float alpha = _tuneAlpha > 0 ? _tuneAlpha : rank * 2f;
        int steps = Math.Max(1, _tuneSteps);
        float lr = _tuneLr > 0 ? _tuneLr : 2e-4f;

        string name = string.IsNullOrWhiteSpace(_tuneName)
            ? Path.GetFileNameWithoutExtension(basePath) + "-lora-" + DateTime.Now.ToString("yyyyMMdd-HHmm")
            : _tuneName.Trim();

        int seqLen = _tuneSft ? Math.Clamp(_tuneSftSeqLen, 16, 8192) : 64;
        try
        {
            var config = SmmLoader.LoadConfig(SmmLoader.LoadMeta(basePath));
            if (config is { MaxSeqLen: > 0 }) seqLen = Math.Min(seqLen, config.MaxSeqLen);
        }
        catch
        {
            // The runner reports a broken base with a clearer message.
        }

        string export = string.IsNullOrWhiteSpace(_tuneExport)
            ? Path.Combine(StudioPaths.Training, Sanitize(name) + ".smm")
            : _tuneExport.Trim();

        return new TrainingJobSettings
        {
            Name = name,
            BaseModelPath = basePath,
            SftMode = _tuneSft,
            SftFormatter = _tuneSft ? (_tuneSftFormatter == "auto" ? null : _tuneSftFormatter) : null,
            PlanId = _planId,
            RuleSetVersion = _ruleSetVersion,
            Sources =
            [
                new JobSource
                {
                    Component = new JobComponent
                    {
                        DisplayName = _tuneSft ? "Chat JSONL" : "Text File",
                        TypeName = _tuneSft ? typeof(JsonlSource).FullName! : typeof(TextFileSource).FullName!,
                        Args = _tuneSft
                            ? new Dictionary<string, string>
                            {
                                ["path"] = corpus,
                            }
                            : new Dictionary<string, string>
                            {
                                ["path"] = corpus,
                                ["mode"] = "FilePerDoc",
                            },
                    },
                },
            ],
            LoRARank = rank,
            LoRAAlpha = alpha,
            LoRATargetModules = [.. _tuneTargets.OrderBy(target => target, StringComparer.Ordinal)],
            MergeOnExport = _tuneOutput != "adapter",
            SaveAdapterFile = _tuneOutput != "merged",
            AdapterPath = string.IsNullOrWhiteSpace(_tuneAdapterPath) ? null : _tuneAdapterPath.Trim(),
            ResumeFrom = string.IsNullOrWhiteSpace(_tuneResume) ? null : _tuneResume.Trim(),
            TotalSteps = steps,
            ValidationPercent = Math.Clamp(_tuneValidationPercent, 0, 90),
            TestPercent = Math.Clamp(_tuneTestPercent, 0, 45),
            LogInterval = Math.Max(1, steps / 20),
            BatchSize = 2,
            SeqLen = Math.Max(1, seqLen),
            BatchingKind = _tuneSft ? null : "RandomWindow",
            LearningRate = lr,
            MinLr = lr / 10f,
            WarmupSteps = Math.Max(1, steps / 20),
            WeightDecay = 0f,
            GradClipNorm = 1f,
            LabelSmoothing = 0f,
            CheckpointInterval = Math.Max(1, steps / 4),
            KeepRecent = 2,
            ExportPath = export,
            StartFresh = _tuneStartFresh,
        };
    }

    private async Task StartFineTune()
    {
        _tuneError = null;
        if (Lab.IsRunning || Rehearsal.IsRunning) return;

        TrainingJobSettings? job = BuildFineTuneJob(out string? error);
        if (job is null)
        {
            _tuneError = error;
            return;
        }

        _tuning = true;
        try
        {
            if (!Lab.Start(job, Workspace.PluginsFolder, _computeProviderId))
            {
                _tuneError = "A training run is already active.";
            }
            else
            {
                await ScrollToRunAsync();
            }
        }
        catch (Exception ex)
        {
            _tuneError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _tuning = false;
        }
    }

    private void RehearseFineTune()
    {
        _tuneError = null;
        if (Lab.IsRunning || Rehearsal.IsRunning) return;

        TrainingJobSettings? job = BuildFineTuneJob(out string? error);
        if (job is null)
        {
            _tuneError = error;
            return;
        }

        if (!Rehearsal.Start(job, Workspace.PluginsFolder))
            _tuneError = "A training run, exam or rehearsal is already active.";
    }

    private async Task PreviewSftData()
    {
        _sftPreviewError = null;
        _sftPreview = null;
        _sftPreviewStatus = "";
        if (!_tuneSft || _sftPreviewing || Lab.IsRunning || Rehearsal.IsRunning) return;

        TrainingJobSettings? job = BuildFineTuneJob(out string? error);
        if (job is null)
        {
            _sftPreviewError = error;
            return;
        }

        _sftPreviewing = true;
        _sftPreviewCancel = new CancellationTokenSource();
        _sftPreviewStatus = "Reading chat records...";
        try
        {
            var progress = new Progress<long>(count =>
            {
                _sftPreviewStatus = $"Analyzed {count:N0} conversation(s)...";
                StateHasChanged();
            });

            _sftPreview = await Task.Run(() => SftDatasetAnalyzer.AnalyzeAsync(
                job,
                Workspace.PluginsFolder,
                progress: progress,
                cancellationToken: _sftPreviewCancel.Token));

            _sftPreviewStatus = "";
        }
        catch (OperationCanceledException)
        {
            _sftPreviewStatus = "Preview cancelled.";
        }
        catch (Exception ex)
        {
            _sftPreviewError = $"{ex.GetType().Name}: {ex.Message}";
            _sftPreviewStatus = "";
        }
        finally
        {
            _sftPreviewing = false;
            _sftPreviewCancel?.Dispose();
            _sftPreviewCancel = null;
        }
    }

    private void CancelSftPreview() => _sftPreviewCancel?.Cancel();

    private async Task StartTraining()
    {
        _error = null;
        if (Lab.IsRunning || Rehearsal.IsRunning) return;

        if (string.IsNullOrWhiteSpace(_job.Name))
            _job.Name = $"corpus-{DateTime.Now:yyyyMMdd-HHmm}";

        if (TrainingJobValidation.Warnings(_job).FirstOrDefault() is { } warning)
        {
            _error = warning;
            return;
        }

        _job.TotalSteps = Math.Max(1, _job.TotalSteps);
        _job.LogInterval = Math.Max(1, _job.LogInterval);
        _job.ValidationPercent = Math.Clamp(_job.ValidationPercent, 0, 90);
        _job.TestPercent = Math.Clamp(_job.TestPercent, 0, 45);
        if (string.IsNullOrWhiteSpace(_job.ExportPath))
            _job.ExportPath = Path.Combine(StudioPaths.Training, Sanitize(_job.Name) + ".smm");

        if (_planId is not null)
        {
            _job.PlanId = _planId;
            _job.RuleSetVersion = _ruleSetVersion;
        }

        Lab.Draft = _job;

        _starting = true;
        try
        {
            if (!Lab.Start(_job, Workspace.PluginsFolder, _computeProviderId))
            {
                _error = "A training run is already active.";
            }
            else
            {
                await ScrollToRunAsync();
            }
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name}: {ex.Message}";
            await Task.CompletedTask;
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task ScrollToRunAsync()
    {
        try
        {
            // The panel appears on the next render, so retry briefly instead of
            // scrolling a missing element once.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await InvokeAsync(StateHasChanged);
                await Task.Delay(120);
                bool found = await JS.InvokeAsync<bool>(
                    "eval", "!!document.getElementById('training-run')");
                if (!found) continue;

                await JS.InvokeVoidAsync(
                    "eval",
                    "document.getElementById('training-run')?.scrollIntoView({behavior:'smooth',block:'start'})");
                return;
            }
        }
        catch
        {
            // Scrolling is a convenience; the run panel is still rendered above.
        }
    }

    private void LoadLatestJob()
    {
        string? latest = Lab.SavedJobs.FirstOrDefault();
        if (latest is not null) LoadJob(latest);
    }

    private void ApplyFineTuneJob(TrainingJobSettings job)
    {
        _tuneBase = job.BaseModelPath ?? "";
        _tuneCorpusPath = job.Sources.FirstOrDefault()?.Component.Args.TryGetValue("path", out string? tuneCorpus) == true
            ? tuneCorpus
            : "";
        _tuneSft = job.SftMode;
        _tuneSftFormatter = string.IsNullOrWhiteSpace(job.SftFormatter) ? "auto" : job.SftFormatter;
        _tuneSftSeqLen = job.SftMode ? Math.Max(16, job.SeqLen) : 512;
        _tuneName = job.Name;
        _tuneSteps = Math.Max(1, job.TotalSteps);
        _tuneValidationPercent = job.ValidationPercent;
        _tuneTestPercent = job.TestPercent;
        _tuneRank = Math.Max(1, job.LoRARank);
        _tuneAlpha = job.LoRAAlpha;
        _tuneLr = job.LearningRate;
        _tuneExport = job.ExportPath ?? "";
        _tuneOutput = !job.MergeOnExport ? "adapter" : job.SaveAdapterFile ? "both" : "merged";
        _tuneAdapterPath = job.AdapterPath ?? "";
        _tuneResume = job.ResumeFrom ?? "";
        _tuneStartFresh = job.StartFresh;
        _tuneTargets.Clear();
        foreach (string target in job.LoRATargetModules) _tuneTargets.Add(target);
        _planId = job.PlanId;
        _ruleSetVersion = job.RuleSetVersion;
        _tuneError = null;
    }

    private void LoadJob(string path, bool navigate = true)
    {
        TrainingJobSettings? job = Lab.LoadJob(path, out string? error);
        if (job is null)
        {
            _error = error ?? "Could not load that job.";
            return;
        }

        if (job.IsFineTune)
        {
            ApplyFineTuneJob(job);
            _tab = "improve";
            if (navigate) Nav.NavigateTo("/training?tab=improve");
            return;
        }

        _job = job;
        Lab.Draft = _job;
        _selectedJobPath = path;
        _planId = job.PlanId;
        _ruleSetVersion = job.RuleSetVersion;
        _error = null;
        _tab = "new";
        if (navigate) Nav.NavigateTo("/training?tab=new");
    }

    private IReadOnlyList<TrainingRunSummary> Comparable
        => [.. Lab.RunHistory.Where(run => run.Record.Success && run.Record.Curve.Count >= 2)];

    private TrainingRunRecord? CompareA => FindRecord(_compareA);
    private TrainingRunRecord? CompareB => FindRecord(_compareB);

    private TrainingRunRecord? FindRecord(string path)
        => Lab.RunHistory.FirstOrDefault(run => run.Path == path)?.Record;

    private void EnsureCompareSelection()
    {
        IReadOnlyList<TrainingRunSummary> comparable = Comparable;
        if (comparable.Count == 0)
        {
            _compareA = "";
            _compareB = "";
            return;
        }

        if (!comparable.Any(run => run.Path == _compareA))
            _compareA = comparable[0].Path;
        if (!comparable.Any(run => run.Path == _compareB) || _compareB == _compareA)
            _compareB = comparable.FirstOrDefault(run => run.Path != _compareA)?.Path ?? "";
    }

    private static string OptionLabel(TrainingRunSummary run)
        => $"{run.Record.Name} - {run.Record.FinishedUtc.ToLocalTime():dd MMM HH:mm}";

    private static string Method(TrainingRunRecord record)
        => record.FineTune ? "LoRA fine-tune" : "Pretrain";

    private static string SourcePath(JobSource source)
        => source.Component.Args.TryGetValue("path", out string? path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : source.Component.TypeName;

    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private static string Fmt(double? value) => value?.ToString("0.####") ?? "-";

    private static string ShortHash(string hash) => hash.Length <= 8 ? hash : hash[..8];

    private static string StepAt(int? step) => step is { } value ? $" @ {value}" : "";

    /// <summary>Marks the lower of two values, the winner for loss and elapsed time.</summary>
    private static string Lower(double? a, double? b)
        => a is { } av && b is { } bv && av < bv - 1e-9 ? "better" : "";

    private string ModelSize(TrainingRunRecord record)
        => record.ExportBytes > 0 ? $"{Megabytes(record.ExportBytes)} MB" : "not exported";

    private static bool CanUseAsBase(TrainingRunSummary run)
        => run.Record.Success
           && run.Record.ExportPath is { } export
           && export.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)
           && File.Exists(export);

    private static bool CanExamine(TrainingRunSummary run)
        => run.Record.Success
           && run.Record.ExportPath is { } export
           && export.EndsWith(".smm", StringComparison.OrdinalIgnoreCase)
           && File.Exists(export)
           && run.Record.JobPath is { } job
           && File.Exists(job);

    private void StartExam(TrainingRunSummary run)
    {
        _promotionSummary = null;
        _promotionPath = null;
        Exam.Start(run, Workspace.PluginsFolder);
    }

    private bool CanPromote
        => _compareA.Length > 0
           && _compareB.Length > 0
           && _compareA != _compareB
           && CompareA?.ExamLoss is not null
           && CompareB?.ExamLoss is not null;

    private string PromotionLabel
    {
        get
        {
            if (CompareA is { } candidate && CompareB is { } baseline)
            {
                try
                {
                    TrainingPromotionReport preview = TrainingPromotionReport.Compare(candidate, baseline);
                    return preview.Verdict switch
                    {
                        TrainingPromotionReport.Better => "Promote A over B",
                        TrainingPromotionReport.Worse => "Write comparison report",
                        _ => "Write inconclusive report",
                    };
                }
                catch
                {
                    // Missing or incomparable exam data; the button still writes a report.
                }
            }
            return "Write promotion report";
        }
    }

    private void Promote()
    {
        _promotionSummary = null;
        _promotionPath = null;

        TrainingRunRecord? candidate = CompareA;
        TrainingRunRecord? baseline = CompareB;
        if (candidate is null || baseline is null) return;

        try
        {
            TrainingPromotionReport report = TrainingPromotionReport.Compare(
                candidate, baseline,
                candidateRunPath: _compareA,
                baselineRunPath: _compareB);
            _promotionPath = TrainingPromotionStore.Save(report);
            _promotionSummary = report.Summary;
        }
        catch (Exception ex)
        {
            _promotionSummary = $"Could not write the report: {ex.Message}";
        }
    }

    private static string PerplexityAt(double? perplexity)
        => perplexity is { } value && double.IsFinite(value) ? $" (ppl {value:0.00})" : "";

    private void UseAsBase(TrainingRunSummary run)
    {
        if (!CanUseAsBase(run)) return;
        _tuneBase = run.Record.ExportPath!;
        _tuneName = Path.GetFileNameWithoutExtension(_tuneBase) + "-lora";
        _tuneError = null;
        _tab = "improve";
        Nav.NavigateTo("/training?tab=improve");
    }

    private static bool CanFork(TrainingRunSummary run)
        => run.Record.Success
           && run.Record.JobPath is { } jobPath
           && File.Exists(jobPath);

    private void ForkRun(TrainingRunSummary run)
    {
        _runNotice = null;

        if (run.Record.JobPath is not { } jobPath || !File.Exists(jobPath))
        {
            _runNotice = "This run has no saved job to fork.";
            return;
        }

        TrainingJobSettings? job = Lab.LoadJob(jobPath, out string? error);
        if (job is null)
        {
            _runNotice = error ?? "Could not load the run's job.";
            return;
        }

        string? latest = Checkpoint.FindLatest(job.CheckpointDir);
        int? latestStep = latest is null ? null : Checkpoint.ReadMeta(latest).Step;
        TrainingJobSettings fork = TrainingFork.Create(run.Record, job, latest, latestStep);

        _runNotice = latest is null
            ? $"Forked {run.Record.Name}; adjust a downstream setting and run."
            : $"Forked {run.Record.Name}; continuing from checkpoint step {latestStep}.";

        if (fork.IsFineTune)
        {
            ApplyFineTuneJob(fork);
            _tab = "improve";
            Nav.NavigateTo("/training?tab=improve");
            return;
        }

        _job = fork;
        Lab.Draft = fork;
        _selectedJobPath = "";
        _planId = fork.PlanId;
        _ruleSetVersion = fork.RuleSetVersion;
        _error = null;
        _tab = "new";
        Nav.NavigateTo("/training?tab=new");
    }

    private string? ForkLabel(TrainingRunRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ForkedFromRunId)) return null;

        string? parent = Lab.RunHistory
            .FirstOrDefault(item => item.Record.Id == record.ForkedFromRunId)?.Record.Name;
        return parent is null ? "fork" : $"fork of {parent}";
    }

    private void DeleteRun(TrainingRunSummary run)
    {
        Lab.DeleteRun(run.Path);
        if (_compareA == run.Path) _compareA = "";
        if (_compareB == run.Path) _compareB = "";
        EnsureCompareSelection();
    }

    private static TrainingJobSettings CreateDefaultJob()
    {
        TrainingJobSettings job = TrainingJobDefaults.Create();
        job.Sources = [DefaultTextSource()];
        return job;
    }

    private static JobSource DefaultTextSource() => new()
    {
        Component = new JobComponent
        {
            DisplayName = "Text File",
            TypeName = typeof(TextFileSource).FullName!,
            Args = new Dictionary<string, string>
            {
                ["path"] = "",
                ["mode"] = "LinePerDoc",
            },
        },
    };

    private void ApplyPlan(TrainingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _planId = plan.Id;
        _ruleSetVersion = plan.RuleSetVersion;

        if (plan.PlaybookId is { } id
            && AllPlaybooks.FirstOrDefault(playbook => playbook.Id == id) is { } playbook)
        {
            ApplyPlaybook(playbook);
            return;
        }

        _tab = plan.Destination == PlannerDestination.Improve ? "improve" : "new";
        Nav.NavigateTo($"/training?tab={_tab}");
    }

    private async Task FocusPlannerMove(PlannerAction action)
    {
        switch (action)
        {
            case PlannerAction.Data:
                await FocusTabAsync("new", "quick-source");
                break;
            case PlannerAction.Design:
                await FocusTabAsync("new", "job-hidden");
                break;
            case PlannerAction.Tokenizer:
                await FocusTabAsync("new", "job-vocab");
                break;
            case PlannerAction.LoRA:
                await FocusTabAsync("improve", "tune-corpus");
                break;
            case PlannerAction.Continue:
            case PlannerAction.Resume:
                await FocusTabAsync("new", "job-resume");
                break;
            case PlannerAction.Evaluate:
            case PlannerAction.Compare:
            case PlannerAction.Promote:
                await EnsureTabAsync("runs");
                break;
            case PlannerAction.Package:
                await FocusTabAsync("new", "job-export");
                break;
            case PlannerAction.Pretrain:
                await FocusTabAsync("new", "quick-source");
                break;
            default:
                await FocusTabAsync("new", "training-model");
                break;
        }
    }

    private async Task FocusTabAsync(string tab, string elementId)
    {
        await EnsureTabAsync(tab);
        try
        {
            await JS.InvokeVoidAsync("studioChat.focusAndScroll", elementId);
        }
        catch
        {
            // A browser without the helper should not break the page.
        }
    }

    private async Task EnsureTabAsync(string tab)
    {
        if (_tab != tab)
        {
            _tab = tab;
            Nav.NavigateTo($"/training?tab={tab}");
            await Task.Delay(120);
        }
        else
        {
            await Task.Yield();
        }
    }

    private async Task BrowseMergeSourceAsync()
    {
        PathPickerSelection? selection = await Picker.PickAsync(
            PathPickerMode.Folder,
            Directory.Exists(_mergeSource) ? _mergeSource : StudioPaths.Corpus,
            "Choose the folder to merge");
        if (selection?.Path is { Length: > 0 } path) _mergeSource = path;
    }

    private async Task BrowseMergeOutputAsync()
    {
        string extension = _mergeFormat == "text" ? ".txt" : ".jsonl";
        PathPickerSelection? selection = await Picker.PickAsync(
            PathPickerMode.SaveFile,
            string.IsNullOrWhiteSpace(_mergeOutput)
                ? Path.Combine(StudioPaths.Corpus, "merged" + extension)
                : _mergeOutput,
            "Save the merged corpus as");
        if (selection?.Path is { Length: > 0 } path) _mergeOutput = path;
    }

    private void OnMergeFormatChanged()
    {
        string extension = _mergeFormat == "text" ? ".txt" : ".jsonl";
        if (string.IsNullOrWhiteSpace(_mergeOutput)) return;
        try
        {
            _mergeOutput = Path.ChangeExtension(_mergeOutput, extension);
        }
        catch
        {
            // A malformed path should not block the format switch.
        }
    }

    private async Task MergeCorpusAsync()
    {
        if (_merging) return;
        _merging = true;
        _mergeError = null;
        _mergeStatus = "";
        try
        {
            if (_mergeLayout == "separator" && string.IsNullOrWhiteSpace(_mergeSeparator))
            {
                _mergeError = "Enter the literal separator used between documents.";
                return;
            }

            var options = new CorpusMergeOptions
            {
                Format = _mergeFormat == "text" ? CorpusMergeFormat.Text : CorpusMergeFormat.Jsonl,
                DocumentLayout = _mergeLayout switch
                {
                    "whole" => CorpusDocumentLayout.WholeFile,
                    "blank" => CorpusDocumentLayout.BlankLine,
                    "qa" => CorpusDocumentLayout.QaPairs,
                    "html" => CorpusDocumentLayout.HtmlBlocks,
                    "separator" => CorpusDocumentLayout.Separator,
                    _ => CorpusDocumentLayout.Auto,
                },
                DocumentSeparator = string.IsNullOrWhiteSpace(_mergeSeparator)
                    ? null
                    : _mergeSeparator,
                StripHtml = _mergeStripHtml,
                NormalizeText = _mergeNormalizeText,
                Deduplicate = _mergeDeduplicate,
                DropDocumentPattern = string.IsNullOrWhiteSpace(_mergeDropPattern)
                    ? null
                    : _mergeDropPattern.Trim(),
            };
            CorpusMergeResult result = await CorpusMerger.MergeAsync(
                _mergeSource.Trim(), _mergeOutput.Trim(), options);
            _mergeStatus =
                $"Merged {result.FilesRead} file(s) into {result.Documents} document(s) as " +
                $"{result.Format} ({result.Layout}); {result.DuplicatesRemoved} duplicate(s), " +
                $"{result.Dropped} dropped; {Megabytes(result.OutputBytes)} MB written to {result.OutputPath}.";
        }
        catch (Exception ex)
        {
            _mergeError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _merging = false;
        }
    }

    private async Task SaveJobAsPlaybookAsync()
    {
        string suggested = string.IsNullOrWhiteSpace(_job.Name) ? "My pretrain playbook" : _job.Name;
        string? name = await JS.InvokeAsync<string?>("prompt", "Playbook name", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            SavedTrainingPlaybook saved = Lab.Playbooks.Save(
                SavedTrainingPlaybook.FromJob(name.Trim(), _job));
            _playbookStatus = $"Saved playbook '{saved.Name}'.";
        }
        catch (Exception ex)
        {
            _playbookStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task SaveLoraAsPlaybookAsync()
    {
        string suggested = string.IsNullOrWhiteSpace(_tuneName)
            ? "My LoRA playbook"
            : _tuneName;
        string? name = await JS.InvokeAsync<string?>("prompt", "Playbook name", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var recipe = new TrainingFineTuneRecipe(
                Rank: _tuneRank,
                Alpha: _tuneAlpha,
                Targets: [.. _tuneTargets],
                Steps: _tuneSteps,
                LearningRate: _tuneLr,
                ValidationPercent: _tuneValidationPercent,
                TestPercent: _tuneTestPercent,
                Output: _tuneOutput);
            SavedTrainingPlaybook saved = Lab.Playbooks.Save(
                SavedTrainingPlaybook.FromFineTune(name.Trim(), recipe, _tuneBase));
            _playbookStatus = $"Saved playbook '{saved.Name}'.";
        }
        catch (Exception ex)
        {
            _playbookStatus = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void DeletePlaybook(TrainingPlaybook playbook)
    {
        if (!IsCustomPlaybook(playbook)) return;
        Lab.Playbooks.Delete(playbook.Id["custom:".Length..]);
        _playbookStatus = $"Deleted playbook '{playbook.Name}'.";
    }

    private void ApplyPlaybook(TrainingPlaybook playbook)
    {
        ArgumentNullException.ThrowIfNull(playbook);

        if (playbook.Kind == TrainingPlaybookKind.FineTune && playbook.FineTune is { } tune)
        {
            _tuneRank = tune.Rank;
            _tuneAlpha = tune.Alpha;
            _tuneTargets.Clear();
            foreach (string target in tune.Targets) _tuneTargets.Add(target);
            _tuneSteps = tune.Steps;
            _tuneLr = tune.LearningRate;
            _tuneValidationPercent = tune.ValidationPercent;
            _tuneTestPercent = tune.TestPercent;
            _tuneOutput = tune.Output;
            _tuneResume = "";
            _tuneStartFresh = true;
            _tuneError = null;
            _tuneName = string.IsNullOrWhiteSpace(_tuneBase)
                ? ""
                : Path.GetFileNameWithoutExtension(_tuneBase) + "-lora";
            _tab = "improve";
            Nav.NavigateTo("/training?tab=improve");
            return;
        }

        if (playbook.CreateJob is not { } create) return;

        // Keep the corpus the user already configured; a playbook changes the
        // recipe, not the data.
        List<JobSource> sources = _job.Sources.Count > 0 ? _job.Sources : [DefaultTextSource()];
        _job = create();
        _job.Sources = sources;
        Lab.Draft = _job;
        _selectedJobPath = "";
        _error = null;
        _tab = "new";
        Nav.NavigateTo("/training?tab=new");
    }

    private void SaveJob()
    {
        _error = null;
        if (string.IsNullOrWhiteSpace(_job.Name))
        {
            _error = "Give the model a name before saving the job.";
            return;
        }
        if (!Lab.SaveJob(_job, out string? saveError))
        {
            _error = saveError ?? "Could not save the job.";
            return;
        }

        _selectedJobPath = _job.SourcePath ?? "";
        Lab.Draft = _job;
    }

    private void StartNewJob()
    {
        _job = CreateDefaultJob();
        Lab.Draft = _job;
        _selectedJobPath = "";
        _planId = null;
        _ruleSetVersion = null;
        _error = null;
    }

    private void LoadSelectedJob()
    {
        if (string.IsNullOrWhiteSpace(_selectedJobPath))
        {
            _error = "Pick a saved job first.";
            return;
        }
        LoadJob(_selectedJobPath);
    }

    private async Task LoadRunTargetAsync()
    {
        _remoteAccelerators = [];
        _remoteAcceleratorError = null;
        if (_computeProviderId is null) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _remoteAccelerators = await Compute.GetAcceleratorsAsync(
                _computeProviderId, timeout.Token);
        }
        catch (Exception ex)
        {
            _remoteAcceleratorError = $"Could not read the host's accelerators: {ex.Message}";
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task RunSelectedJob()
    {
        if (string.IsNullOrWhiteSpace(_selectedJobPath))
        {
            _error = "Pick a saved job first.";
            return;
        }

        TrainingJobSettings? job = Lab.LoadJob(_selectedJobPath, out string? loadError);
        if (job is null)
        {
            _error = loadError ?? "Could not load that job.";
            return;
        }

        // A fine-tune has its own form and base; load it there for review rather
        // than starting it invisibly from here.
        if (job.IsFineTune)
        {
            LoadJob(_selectedJobPath);
            return;
        }

        LoadJob(_selectedJobPath, navigate: false);
        await StartTraining();
    }

    private static string Sanitize(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "model" : name.Trim();
    }
}
