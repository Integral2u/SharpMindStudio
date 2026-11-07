using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SharpMind.Studio.Core.Compute;
using SharpMind.Studio.Core.Inference;
using SharpMind.Studio.Core.OpenRouter;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Services;

namespace SharpMind.Studio.Components.Pages;

public partial class Settings
{
    [SupplyParameterFromQuery(Name = "tab")]
    public string? TabQuery { get; set; }

    [SupplyParameterFromQuery(Name = "theme")]
    public string? ThemeQuery { get; set; }

    [SupplyParameterFromQuery(Name = "color")]
    public string? ColorQuery { get; set; }

    private string _tab = "appearance";
    private string _status = "";
    private string? _defaultModelPath;
    private string? _lastApplied;
    private string _studioHost = ThemeService.DefaultHost;
    private int _studioPort = ThemeService.DefaultPort;
    private bool _closeToTray = true;
    private string _closeToTrayStatus = "";
    private string _serverStatus = "";
    private bool _lanEnabled;
    private string _lanStatus = "";
    private string? _computeStatus;
    private string? _computeError;
    private string? _computeErrorHostId;
    private bool _computeBusy;
    private bool _computeEditing;
    private string? _editingComputeId;
    private string _computeName = "";
    private string _computeKind = ComputeProviderKinds.Local;
    private string _computePrefab = ComputeProviderPrefabs.LocalDocker;
    private string _computeBaseUrl = ComputeProviderSettings.DefaultLocalUrl;
    private string _computeToken = "";
    private string _computeStudioUrl = "";
    private string _runpodKey = "";
    private string _runpodImage = ComputeProviderPresets.DefaultServerImage;
    private string _runpodGpu = ComputeProviderPresets.DefaultGpuType;
    private int _runpodDisk = ComputeProviderPresets.DefaultRunpodDiskGb;
    private string? _runpodStatus;
    private string? _runpodError;
    private bool _runpodBusy;
    private string _computeImage = ComputeProviderPresets.DefaultImage;
    private int _computePort = ComputeProviderPresets.DefaultPort;
    private bool _computeUseGpu;
    private string _computePodId = "";
    private bool _computeShowToken;
    private bool _dockerBusy;
    private string _dockerStatus = "Not checked yet.";
    private string? _dockerError;
    private string? _dockerLogs;
    private DockerEngineStatus? _dockerEngine;
    private bool _sshBusy;
    private string? _sshHostId;
    private string? _sshStage;
    private string? _sshError;
    private string _sshLog = "";
    private readonly List<string> _sshLines = [];
    private string _computeSshHost = "";
    private int _computeSshPort = SshTargets.DefaultPort;
    private string _computeSshUser = SshTargets.DefaultUser;
    private string _computeSshKeyPath = "";
    private string _computeSshPassphrase = "";
    private string _computeSshPassword = "";
    private string _computeSshContainer = "sharpmind";
    private string _sshKeyStatus = "";
    private string _sshPublicKey = "";
    private string _computeModelPath = "";
    private double _computeUploadProgress;
    private string? _uploadingId;
    private InferenceOverrides _system = new();
    private IReadOnlyList<ModelFamily> _families = [];
    private IReadOnlyList<ModelDescriptor> _models = [];
    private string? _selectedFamilyId;
    private string? _selectedModelPath;
    private InferenceOverrides _familyDraft = new();
    private InferenceOverrides _modelDraft = new();
    private IReadOnlyDictionary<string, string>? _familySources;
    private IReadOnlyDictionary<string, string>? _modelSources;
    private ResolvedInference? _familyResolved;
    private ResolvedInference? _modelResolved;

    protected override async Task OnParametersSetAsync()
    {
        _tab = TabQuery is "system" or "families" or "models" or "openrouter" or "pair" ? TabQuery : "appearance";
        _system = Inference.System;
        _studioHost = Theme.StudioHost;
        _studioPort = Theme.StudioPort;
        _closeToTray = Theme.CloseToTray;
        _lanEnabled = Gateway.Config.LanEnabled;
        if (_tab is "system" or "families" or "models") await LoadCatalogAsync();

        if (!string.IsNullOrWhiteSpace(ThemeQuery))
        {
            string? normalized = ThemeService.AvailableThemes.FirstOrDefault(
                value => value.Equals(ThemeQuery, StringComparison.OrdinalIgnoreCase));
            if (normalized is not null)
            {
                string? custom = ThemeService.NormalizeColor(ColorQuery);
                string key = $"{normalized}|{custom}";
                if (key != _lastApplied)
                {
                    await Theme.SetThemeAsync(normalized, custom ?? Theme.CustomColor);
                    _lastApplied = key;
                    _status = $"{ThemeService.Label(Theme.Theme)} applied.";
                }
            }
        }

        if (_defaultModelPath is null || _models.All(model => model.Path != _defaultModelPath))
            _defaultModelPath = _models.Any(model => model.Path == Theme.LastChatModel)
                ? Theme.LastChatModel
                : _models.FirstOrDefault()?.Path;

        if (_selectedFamilyId is null || _families.All(f => f.Id != _selectedFamilyId))
        {
            _selectedFamilyId = _families.FirstOrDefault()?.Id;
            LoadFamilyDraft();
        }

        if (_selectedModelPath is null || _models.All(m => m.Path != _selectedModelPath))
        {
            _selectedModelPath = _models.FirstOrDefault()?.Path;
            LoadModelDraft();
        }
    }

    private async Task LoadCatalogAsync()
    {
        _models = await Catalog.RefreshAsync();
        var families = Catalog.Families.ToList();
        foreach (string key in Inference.Families.Keys)
        {
            if (families.All(family => family.Id != key))
                families.Add(new ModelFamily(key, key));
        }
        _families = [.. families.OrderBy(family => family.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task RefreshModels()
    {
        await Catalog.RefreshAsync(force: true);
        await LoadCatalogAsync();
        _status = $"Found {_models.Count} model(s).";
    }

    private void LoadFamilyDraft()
    {
        if (_selectedFamilyId is null) return;
        _familyDraft = Inference.FamilyOverrides(_selectedFamilyId) ?? new InferenceOverrides();
        _familyResolved = Inference.Resolve(_selectedFamilyId, null, null);
        _familySources = _familyResolved.Sources;
    }

    private void LoadModelDraft()
    {
        if (_selectedModelPath is null) return;
        ModelDescriptor? model = _models.FirstOrDefault(value => value.Path == _selectedModelPath);
        _modelDraft = Inference.ModelOverrides(_selectedModelPath) ?? new InferenceOverrides();
        _modelResolved = Inference.Resolve(model?.Family.Id, _selectedModelPath, null);
        _modelSources = _modelResolved.Sources;
    }

    private async Task SaveDefaultModel()
    {
        if (string.IsNullOrWhiteSpace(_defaultModelPath)) return;
        await Theme.SetLastChatModelAsync(_defaultModelPath);
        _status = "Default chat model saved.";
    }
    private async Task SaveServerAsync()
    {
        if (string.IsNullOrWhiteSpace(_studioHost) || _studioPort is < 1 or > 65535)
        {
            _serverStatus = "Enter a valid host and port.";
            return;
        }

        await Theme.SetServerAsync(_studioHost, _studioPort);
        _studioHost = Theme.StudioHost;
        _studioPort = Theme.StudioPort;
        _lanEnabled = Gateway.Config.LanEnabled;
        _serverStatus = $"Connection saved: {_studioHost}:{_studioPort}";
    }

    private async Task UseProviderStudioAsync(ChangeEventArgs args)
    {
        string id = args.Value?.ToString() ?? "";
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider?.StudioUrl is not { Length: > 0 } url)
        {
            if (id.Length > 0) _serverStatus = "That host has no Studio URL yet.";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            _serverStatus = "That Studio URL is not valid.";
            return;
        }

        _studioHost = uri.Host;
        _studioPort = uri.Port;
        await SaveServerAsync();
    }

    private async Task SaveCloseToTrayAsync()
    {
        await Theme.SetCloseToTrayAsync(_closeToTray);
        _closeToTrayStatus = _closeToTray
            ? "The close button will minimize to the tray."
            : "The close button will exit Studio.";
    }

    private async Task SetCloseToTray(ChangeEventArgs args)
    {
        _closeToTray = string.Equals(
            args.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        await SaveCloseToTrayAsync();
    }

    private void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Opening Explorer is a convenience; the path is still shown above.
        }
    }

    private void SetLanEnabled(ChangeEventArgs args)
    {
        _lanEnabled = args.Value is true;
        Gateway.SaveLanEnabled(_lanEnabled);
        _lanStatus = _lanEnabled
            ? "LAN pairing is on. Phones on this network can connect now."
            : "LAN pairing is off. Phones are disconnected.";
    }

    private async Task ResetServerAsync()
    {
        await Theme.ResetServerAsync();
        _studioHost = Theme.StudioHost;
        _studioPort = Theme.StudioPort;
        _lanEnabled = Gateway.Config.LanEnabled;
        _serverStatus = "Local defaults restored: 127.0.0.1:5117";
    }

    private async Task NewCompute()
    {
        _computeEditing = true;
        _editingComputeId = null;
        _computePrefab = ComputeProviderPrefabs.LocalDocker;
        _computeName = "Local Docker";
        _computeKind = ComputeProviderKinds.Local;
        _computeBaseUrl = ComputeProviderSettings.DefaultLocalUrl;
        _computeToken = "";
        _computeImage = ComputeProviderPresets.DefaultImage;
        _computePort = ComputeProviderPresets.DefaultPort;
        _computeUseGpu = false;
        _computePodId = "";
        _computeStudioUrl = ComputeProviderPresets.DeriveStudioUrl(
            ComputeProviderPresets.LocalUrl(_computePort)) ?? "";
        _computeShowToken = false;
        _runpodKey = "";
        _runpodImage = Compute.RegistryImage ?? ComputeProviderPresets.DefaultServerImage;
        _runpodGpu = ComputeProviderPresets.DefaultGpuType;
        _runpodDisk = ComputeProviderPresets.DefaultRunpodDiskGb;
        ResetSshEditor();
        _runpodStatus = null;
        _computeStatus = null;
        _computeError = null;
        _computeErrorHostId = null;
        await ScrollToEditorAsync();
    }

    private async Task EditCompute(ComputeProviderSettings provider)
    {
        _computeEditing = true;
        _editingComputeId = provider.Id;
        _computeName = provider.Name;
        _computePrefab = provider.Prefab;
        _computeKind = provider.NormalizedKind;
        _computeBaseUrl = provider.BaseUrl;
        _computeToken = "";
        _computeImage = provider.Prefab is ComputeProviderPrefabs.Vultr or ComputeProviderPrefabs.Custom
            ? SshSetupScript.ResolveImage(provider, Compute.RegistryImage)
            : provider.DockerImage ?? ComputeProviderPresets.DefaultImage;
        _computePort = provider.DockerPort ?? ComputeProviderPresets.DefaultPort;
        _computeUseGpu = provider.UseGpu;
        _computePodId = "";
        _computeStudioUrl = provider.StudioUrl
            ?? ComputeProviderPresets.DeriveStudioUrl(provider.BaseUrl)
            ?? "";
        _computeShowToken = false;
        LoadSshEditor(provider);
        _runpodKey = "";
        _runpodImage = string.IsNullOrWhiteSpace(provider.RunpodImage)
            || string.Equals(provider.RunpodImage, ComputeProviderPresets.DefaultServerImage, StringComparison.OrdinalIgnoreCase)
                ? Compute.RegistryImage ?? provider.RunpodImage ?? ComputeProviderPresets.DefaultServerImage
                : provider.RunpodImage;
        _runpodGpu = provider.RunpodGpuType ?? ComputeProviderPresets.DefaultGpuType;
        _runpodDisk = provider.RunpodDiskGb ?? ComputeProviderPresets.DefaultRunpodDiskGb;
        _runpodStatus = null;
        _computeStatus = null;
        _computeError = null;
        _computeErrorHostId = null;
        await ScrollToEditorAsync();
    }

    private async Task ScrollToEditorAsync()
    {
        try
        {
            await InvokeAsync(StateHasChanged);
            await JS.InvokeVoidAsync(
                "eval",
                "document.getElementById('compute-editor')?.scrollIntoView({behavior:'smooth',block:'start'})");
        }
        catch
        {
            // Scrolling is a convenience; the editor is still rendered above the list.
        }
    }

    private void CancelCompute()
    {
        _computeEditing = false;
        _editingComputeId = null;
        _computeToken = "";
        _computeError = null;
        _computeErrorHostId = null;
    }

    private void SaveCompute()
    {
        _computeError = null;
        _computeErrorHostId = null;
        if (string.IsNullOrWhiteSpace(_computeName))
            _computeName = _computeKind == ComputeProviderKinds.Local ? "Local Docker" : "SharpMind Compute";

        bool local = _computePrefab == ComputeProviderPrefabs.LocalDocker;
        string baseUrl = local
            ? ComputeProviderPresets.LocalUrl(_computePort)
            : ComputeProviderPresets.NormalizeUrl(_computeBaseUrl);

        if (_editingComputeId is null)
        {
            ComputeProviderSettings provider = Compute.Add(
                _computeName,
                _computeKind,
                baseUrl,
                string.IsNullOrWhiteSpace(_computeToken) ? null : _computeToken,
                _computePrefab);
            ApplyDockerSettings(provider.Id);
            Compute.SetStudioUrl(provider.Id, _computeStudioUrl);
            SaveRunpodSettings(provider.Id);
            SaveSshSettings(provider.Id);
            _computeStatus = local
                ? "Host added. Press Start container below."
                : "Host added. Press Test to read its models.";
        }
        else
        {
            Compute.Update(
                _editingComputeId,
                _computeName,
                _computeKind,
                baseUrl,
                string.IsNullOrWhiteSpace(_computeToken) ? null : _computeToken,
                prefab: _computePrefab);
            ApplyDockerSettings(_editingComputeId);
            Compute.SetStudioUrl(_editingComputeId, _computeStudioUrl);
            SaveRunpodSettings(_editingComputeId);
            SaveSshSettings(_editingComputeId);
            _computeStatus = "Compute host saved.";
        }

        _computeEditing = false;
        _editingComputeId = null;
        _computeToken = "";
    }

    private void SyncPrefab()
    {
        _computeKind = ComputeProviderPrefabs.Find(_computePrefab).Kind;
        if (_editingComputeId is null)
        {
            _computeName = _computePrefab switch
            {
                ComputeProviderPrefabs.LocalDocker => "Local Docker",
                ComputeProviderPrefabs.Runpod => "Runpod GPU",
                ComputeProviderPrefabs.Vultr => "Vultr",
                _ => "SharpMind Server",
            };
        }

        if (_computePrefab == ComputeProviderPrefabs.LocalDocker)
            SyncComputeStudioUrl();
        else if (_computePrefab is ComputeProviderPrefabs.Vultr or ComputeProviderPrefabs.Custom
                 && string.Equals(_computeImage, ComputeProviderPresets.DefaultImage, StringComparison.OrdinalIgnoreCase))
        {
            _computeImage = Compute.RegistryImage ?? ComputeProviderPresets.DefaultServerImage;
        }
    }

    private void ApplyDockerSettings(string id)
    {
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider is null) return;
        string container = string.IsNullOrWhiteSpace(provider.ContainerName)
            ? DockerHostService.DefaultContainerName(id)
            : provider.ContainerName;
        Compute.SetDocker(id, _computeImage, _computePort, _computeUseGpu, container);
    }

    private void SaveRunpodSettings(string id)
    {
        Compute.SetRunpod(
            id,
            string.IsNullOrWhiteSpace(_runpodKey) ? null : _runpodKey,
            _runpodImage,
            _runpodGpu,
            _runpodDisk);
    }

    private void ResetSshEditor()
    {
        _computeSshHost = "";
        _computeSshPort = SshTargets.DefaultPort;
        _computeSshUser = SshTargets.DefaultUser;
        _computeSshKeyPath = "";
        _computeSshPassphrase = "";
        _computeSshPassword = "";
        _computeSshContainer = "sharpmind";
        _sshKeyStatus = "";
        _sshPublicKey = "";
    }

    private void LoadSshEditor(ComputeProviderSettings provider)
    {
        _computeSshHost = provider.SshHost ?? "";
        _computeSshPort = provider.SshPort is > 0 and <= 65535
            ? provider.SshPort.Value
            : SshTargets.DefaultPort;
        _computeSshUser = string.IsNullOrWhiteSpace(provider.SshUser)
            ? SshTargets.DefaultUser
            : provider.SshUser;
        _computeSshKeyPath = provider.SshKeyPath ?? "";
        _computeSshPassphrase = "";
        _computeSshPassword = "";
        _computeSshContainer = string.IsNullOrWhiteSpace(provider.SshContainerName)
            ? "sharpmind"
            : provider.SshContainerName;
        _sshKeyStatus = "";
        _sshPublicKey = ReadPublicKey(provider.SshKeyPath);
    }

    private static string ReadPublicKey(string? privateKeyPath)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath)) return "";
        try
        {
            string publicPath = privateKeyPath + ".pub";
            return File.Exists(publicPath) ? File.ReadAllText(publicPath).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private void SaveSshSettings(string id)
    {
        Compute.SetSsh(
            id,
            _computeSshHost,
            _computeSshPort,
            _computeSshUser,
            _computeSshKeyPath,
            string.IsNullOrWhiteSpace(_computeSshPassword) ? null : _computeSshPassword,
            string.IsNullOrWhiteSpace(_computeSshPassphrase) ? null : _computeSshPassphrase,
            _computeSshContainer,
            _computeUseGpu);
    }

    private async Task BrowseSshKey()
    {
        PathPickerSelection? selection = await Picker.PickAsync(
            PathPickerMode.File,
            SshKeyStartFolder(),
            "Choose an SSH private key");
        if (selection?.Path is { Length: > 0 } path)
        {
            _computeSshKeyPath = path;
            _sshPublicKey = ReadPublicKey(path);
            _sshKeyStatus = _sshPublicKey.Length > 0
                ? "Selected an existing key. Copy its public key if the host does not know it yet."
                : "Selected a key without a .pub file. The host must already trust this key.";
        }
    }

    private void CreateSshKey()
    {
        _sshKeyStatus = "";
        try
        {
            string path = string.IsNullOrWhiteSpace(_computeSshKeyPath)
                ? SshKeyGenerator.DefaultPrivateKeyPath
                : _computeSshKeyPath;
            bool existed = File.Exists(path);
            SshKeyPair pair = SshKeyGenerator.Ensure(
                string.IsNullOrWhiteSpace(_computeSshKeyPath) ? null : _computeSshKeyPath);
            _computeSshKeyPath = pair.PrivateKeyPath;
            _sshPublicKey = pair.PublicKey;
            _sshKeyStatus = existed
                ? $"Using the existing key at {pair.PrivateKeyPath}. Add its public key to the host if it is not there yet."
                : $"Created {pair.PrivateKeyPath}. Add the public key to the host, then press Test SSH.";
        }
        catch (Exception ex)
        {
            _sshKeyStatus = $"Could not create a key: {ex.Message}";
        }
    }

    private async Task CopySshPublicKey()
    {
        if (_sshPublicKey.Length == 0) return;
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", _sshPublicKey);
            _sshKeyStatus = "Public key copied.";
        }
        catch (Exception ex)
        {
            _sshKeyStatus = $"Could not copy the public key: {ex.Message}";
        }
    }

    private static string SshKeyStartFolder()
    {
        string ssh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return Directory.Exists(ssh) ? ssh : "";
    }

    private string SshBusyLabel(string id, string fallback) =>
        _sshBusy && string.Equals(_sshHostId, id, StringComparison.OrdinalIgnoreCase)
            ? "Working…"
            : fallback;

    private void ResetSshLog()
    {
        _sshLines.Clear();
        _sshLog = "";
        _sshError = null;
        _sshStage = null;
    }

    private void AddSshLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string text = line.TrimEnd();
        if (text.StartsWith("SM_", StringComparison.Ordinal)) return;
        const int maxLines = 400;
        _sshLines.Add(text);
        if (_sshLines.Count > maxLines)
            _sshLines.RemoveRange(0, _sshLines.Count - maxLines);
        _sshLog = string.Join('\n', _sshLines);
    }

    private IProgress<SshProgress> SshProgressFor(string id) =>
        new Progress<SshProgress>(progress => _ = InvokeAsync(() =>
        {
            if (!string.Equals(_sshHostId, id, StringComparison.OrdinalIgnoreCase)) return;
            _sshStage = StageLabel(progress.Stage);
            AddSshLine(progress.Line);
            StateHasChanged();
        }));

    private static string StageLabel(string stage) => stage switch
    {
        "connect" => "Connecting over SSH…",
        "probe" => "Checking the host…",
        "docker" => "Installing Docker…",
        "toolkit" => "Installing the NVIDIA container toolkit…",
        "pull" => "Pulling the server image…",
        "run" => "Starting the container…",
        "ready" => "Container is running.",
        "logs" => "Reading container logs…",
        "stop" => "Stopping the container…",
        _ => "Working…",
    };

    private async Task TestSshAsync(string id)
    {
        if (_sshBusy) return;
        _sshBusy = true;
        _sshHostId = id;
        ResetSshLog();
        _sshStage = "Connecting over SSH…";
        await InvokeAsync(StateHasChanged);
        try
        {
            SshCredentials credentials = Compute.SshCredentials(id)
                ?? throw new InvalidOperationException(
                    "No SSH host is configured. Press Edit and add the host, user and key or password.");
            SshProbe probe = await Ssh.ProbeAsync(credentials, SshProgressFor(id));
            AddSshLine(probe.Summary);
            _sshStage = probe.HasDocker
                ? $"SSH works. Docker {probe.DockerVersion} is installed."
                : "SSH works. Docker is not installed yet; Set up & deploy installs it.";
            if (probe.HasGpu)
                AddSshLine($"GPU: {probe.Gpu}");
            if (probe.HasGpu && !probe.HasNvidiaToolkit)
                AddSshLine("NVIDIA container toolkit is missing; setup installs it on a GPU host.");
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            _sshError = ex.Message;
            AddSshLine($"ERROR: {ex.Message}");
        }
        finally
        {
            _sshBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SetupSshAsync(string id)
    {
        if (_sshBusy) return;
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider is null) return;

        _sshBusy = true;
        _sshHostId = id;
        ResetSshLog();
        _sshStage = "Connecting over SSH…";
        await InvokeAsync(StateHasChanged);
        try
        {
            SshCredentials credentials = Compute.SshCredentials(id)
                ?? throw new InvalidOperationException(
                    "No SSH host is configured. Press Edit and add the host, user and key or password.");

            string token = Compute.EnsureToken(id);
            var request = new SshSetupRequest
            {
                Image = SshSetupScript.ResolveImage(provider, Compute.RegistryImage),
                Token = token,
                ContainerName = string.IsNullOrWhiteSpace(provider.SshContainerName)
                    ? "sharpmind"
                    : provider.SshContainerName,
                UseGpu = provider.UseGpu,
            };
            AddSshLine($"Deploying {request.Image} as '{request.ContainerName}' on {credentials.Target.Display}.");
            SshSetupResult result = await Ssh.SetupAsync(credentials, request, SshProgressFor(id));
            foreach (string warning in result.Warnings) AddSshLine($"Warning: {warning}");

            if (Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out Uri? current)
                && SshTargets.IsLoopback(current.Host))
            {
                Compute.SetBaseUrl(id, $"http://{credentials.Target.Host}:{request.ApiPort}");
                AddSshLine($"Base URL set to http://{credentials.Target.Host}:{request.ApiPort}.");
            }

            _sshStage = $"Container '{result.ContainerName}' is {result.ContainerStatus}.";
            AddSshLine(_sshStage);

            try
            {
                using var apiTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await Compute.TestAsync(id, apiTimeout.Token);
                _sshStage += " Compute API answered /health.";
                AddSshLine("Compute API answered /health.");
            }
            catch (OperationCanceledException)
            {
                _sshStage += " API did not answer within 25 seconds.";
                AddSshLine(
                    "The container is running, but the API did not answer within 25 seconds. "
                    + "Open TCP 8080 in the provider firewall (and check the container logs), then press Test.");
            }
            catch (Exception ex)
            {
                _sshStage += " API is not reachable from this PC yet.";
                AddSshLine(
                    $"The container is running, but the API did not answer: {ex.Message} "
                    + "Open TCP 8080 in the provider firewall, then press Test.");
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            _sshError = ex.Message;
            AddSshLine($"ERROR: {ex.Message}");
        }
        finally
        {
            _sshBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SshLogsAsync(string id)
    {
        if (_sshBusy) return;
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider is null) return;

        _sshBusy = true;
        _sshHostId = id;
        ResetSshLog();
        _sshStage = "Reading container logs…";
        await InvokeAsync(StateHasChanged);
        try
        {
            SshCredentials credentials = Compute.SshCredentials(id)
                ?? throw new InvalidOperationException("No SSH host is configured.");
            string container = string.IsNullOrWhiteSpace(provider.SshContainerName)
                ? "sharpmind"
                : provider.SshContainerName;
            string logs = await Ssh.LogsAsync(credentials, container, 120);
            AddSshLine(logs.Length == 0 ? "(no log output)" : logs);
            _sshStage = $"Last 120 lines from '{container}'.";
        }
        catch (Exception ex)
        {
            _sshError = ex.Message;
            AddSshLine($"ERROR: {ex.Message}");
        }
        finally
        {
            _sshBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SshStopAsync(string id)
    {
        if (_sshBusy) return;
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider is null) return;

        _sshBusy = true;
        _sshHostId = id;
        ResetSshLog();
        _sshStage = "Stopping the container…";
        await InvokeAsync(StateHasChanged);
        try
        {
            SshCredentials credentials = Compute.SshCredentials(id)
                ?? throw new InvalidOperationException("No SSH host is configured.");
            string container = string.IsNullOrWhiteSpace(provider.SshContainerName)
                ? "sharpmind"
                : provider.SshContainerName;
            string status = await Ssh.StopAsync(credentials, container);
            _sshStage = status.Length == 0
                ? $"Container '{container}' stopped."
                : $"Container '{container}': {status}";
            AddSshLine(_sshStage);
        }
        catch (Exception ex)
        {
            _sshError = ex.Message;
            AddSshLine($"ERROR: {ex.Message}");
        }
        finally
        {
            _sshBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void SetRunpodImage(string id, string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return;
        Compute.SetRunpod(id, null, image.Trim(), null, null);
        _runpodError = null;
        _runpodStatus = $"Server image set to {image.Trim()}.";
    }

    private async Task DeployRunpodAsync(string id)
    {
        _runpodBusy = true;
        _computeError = null;
        _runpodError = null;
        _runpodStatus = "Creating the pod...";
        StateHasChanged();
        try
        {
            RunpodPod pod = await Runpod.DeployAsync(id);
            _runpodStatus = $"Pod {pod.Id} is {pod.Status}.";
            StateHasChanged();

            for (int attempt = 0; attempt < 90; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                pod = await Runpod.RefreshAsync(id);
                _runpodStatus = $"Pod {pod.Id} is {pod.Status}.";
                StateHasChanged();
                if (pod.Status is "RUNNING" or "ERROR" or "EXITED" or "TERMINATED")
                    break;
            }

            if (pod.Status == "RUNNING")
            {
                _runpodStatus = "Pod running. Waiting for the API...";
                StateHasChanged();
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        await Compute.TestAsync(id);
                        await Compute.RefreshModelsAsync(id);
                        _runpodStatus = "Pod running and the API answered. Upload a model to start.";
                        break;
                    }
                    catch
                    {
                        _runpodStatus = $"Pod running; API not ready yet ({attempt + 1}/30)...";
                        StateHasChanged();
                    }
                }
            }
            else
            {
                _runpodStatus = pod.Status == "RUNNING"
                    ? $"Pod {pod.Id} is running."
                    : $"Pod {pod.Id} is still {pod.Status}. Press Refresh to check again.";
            }
        }
        catch (Exception ex)
        {
            _computeError = ex.Message;
            _runpodError = ex.Message;
            _runpodStatus = null;
        }
        finally
        {
            _runpodBusy = false;
        }
    }

    private async Task RefreshRunpodAsync(string id)
    {
        _runpodBusy = true;
        _computeError = null;
        _runpodError = null;
        StateHasChanged();
        try
        {
            RunpodPod pod = await Runpod.RefreshAsync(id);
            _runpodStatus = $"Pod {pod.Id} is {pod.Status}.";
            if (pod.Status == "RUNNING")
            {
                try { await Compute.TestAsync(id); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _computeError = ex.Message;
            _runpodError = ex.Message;
        }
        finally
        {
            _runpodBusy = false;
        }
    }

    private async Task TerminateRunpodAsync(string id)
    {
        _runpodBusy = true;
        _computeError = null;
        _runpodError = null;
        _runpodStatus = "Terminating the pod...";
        StateHasChanged();
        try
        {
            await Runpod.TerminateAsync(id);
            _runpodStatus = "Pod terminated.";
        }
        catch (Exception ex)
        {
            _computeError = ex.Message;
            _runpodError = ex.Message;
            _runpodStatus = null;
        }
        finally
        {
            _runpodBusy = false;
        }
    }

    private async Task DeleteHostModelAsync(string id, string model)
    {
        _computeErrorHostId = id;
        _computeError = null;
        bool confirmed = await JS.InvokeAsync<bool>(
            "confirm", $"Delete '{model}' from the host? The local copy is not touched.");
        if (!confirmed) return;

        _computeBusy = true;
        _computeStatus = $"Deleting {model}...";
        StateHasChanged();
        try
        {
            await Compute.DeleteModelAsync(id, model);
            _computeStatus = $"{model} deleted from the host.";
            await RefreshComputeModelsAsync(id);
        }
        catch (Exception ex)
        {
            _computeError = ex.Message;
            _computeStatus = null;
        }
        finally
        {
            _computeBusy = false;
        }
    }

    private void GenerateComputeToken()
    {
        _computeToken = ComputeProviderPresets.NewToken();
        _computeShowToken = true;
        _computeStatus = _computeKind == ComputeProviderKinds.Local
            ? "Token generated. Save the host, then Start container."
            : "Token generated. Copy it into SHARPMIND_TOKEN on the remote host before deploying.";
    }

    private void BuildRunpodUrl()
    {
        if (string.IsNullOrWhiteSpace(_computePodId))
        {
            _computeError = "Enter the Runpod Pod ID first.";
            return;
        }

        _computeBaseUrl = ComputeProviderPresets.TryRunpodUrl(_computePodId)
            ?? _computeBaseUrl;
        _computeStudioUrl = ComputeProviderPresets.DeriveStudioUrl(_computeBaseUrl)
            ?? _computeStudioUrl;
        _computeError = null;
        _computeStatus = $"URL set to {_computeBaseUrl}.";
    }

    private void SyncComputeStudioUrl()
    {
        if (_computeKind != ComputeProviderKinds.Local) return;
        _computeStudioUrl = ComputeProviderPresets.DeriveStudioUrl(
            ComputeProviderPresets.LocalUrl(_computePort)) ?? _computeStudioUrl;
    }

    private async Task CheckDockerAsync(string id)
    {
        _dockerBusy = true;
        _dockerError = null;
        _dockerStatus = "Checking Docker...";
        StateHasChanged();
        try
        {
            DockerEngineStatus engine = await Docker.GetEngineStatusAsync();
            _dockerEngine = engine;
            if (!engine.Available)
            {
                _dockerStatus = engine.Message;
                return;
            }

            ComputeProviderSettings? provider = Compute.Find(id);
            string container = provider?.ContainerName
                ?? DockerHostService.DefaultContainerName(id);
            DockerContainerStatus status = await Docker.GetContainerStatusAsync(container);
            if (!status.Exists)
            {
                _dockerStatus = "Docker is running. The container is not created yet.";
                return;
            }

            _dockerStatus = status.Running
                ? $"Container {container} is running."
                : $"Container {container} is {status.State}.";
        }
        catch (Exception ex)
        {
            _dockerError = ex.Message;
            _dockerStatus = "Docker check failed.";
        }
        finally
        {
            _dockerBusy = false;
        }
    }

    private async Task StartDockerContainerAsync(string id)
    {
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider is null) return;

        _dockerBusy = true;
        _dockerError = null;
        StateHasChanged();
        try
        {
            DockerEngineStatus engine = await Docker.GetEngineStatusAsync();
            _dockerEngine = engine;
            if (!engine.Available)
            {
                _dockerStatus = engine.Message;
                return;
            }

            string token = Compute.EnsureToken(id);
            string container = string.IsNullOrWhiteSpace(provider.ContainerName)
                ? DockerHostService.DefaultContainerName(id)
                : provider.ContainerName;
            int port = provider.DockerPort ?? ComputeProviderPresets.DefaultPort;
            string image = provider.DockerImage ?? ComputeProviderPresets.DefaultImage;
            string data = DockerHostService.DefaultDataFolder(container);

            Compute.Update(
                id,
                provider.Name,
                ComputeProviderKinds.Local,
                ComputeProviderPresets.LocalUrl(port),
                token: null);
            Compute.SetDocker(id, image, port, provider.UseGpu, container);

            _dockerStatus = $"Starting {image} on port {port}...";
            StateHasChanged();

            DockerContainerStatus status = await Docker.StartAsync(new DockerStartRequest
            {
                ContainerName = container,
                Image = image,
                Port = port,
                Token = token,
                DataFolder = data,
                UseGpu = provider.UseGpu,
            });

            _dockerStatus = status.Running
                ? $"Container {container} is running on port {port}."
                : $"Container {container} is {status.State}.";
            _computeStatus = _dockerStatus;

            await TestComputeAsync(id);
            await RefreshComputeModelsAsync(id);
        }
        catch (Exception ex)
        {
            _dockerError = ex.Message;
            _dockerStatus = "The container did not start.";
        }
        finally
        {
            _dockerBusy = false;
        }
    }

    private async Task StopDockerContainerAsync(string id)
    {
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider is null) return;

        _dockerBusy = true;
        _dockerError = null;
        StateHasChanged();
        try
        {
            string container = string.IsNullOrWhiteSpace(provider.ContainerName)
                ? DockerHostService.DefaultContainerName(id)
                : provider.ContainerName;
            await Docker.StopAsync(container, remove: false);
            _dockerStatus = $"Container {container} stopped.";
            _computeStatus = _dockerStatus;
        }
        catch (Exception ex)
        {
            _dockerError = ex.Message;
        }
        finally
        {
            _dockerBusy = false;
        }
    }

    private async Task DockerLogsAsync(string id)
    {
        _dockerBusy = true;
        _dockerError = null;
        StateHasChanged();
        try
        {
            ComputeProviderSettings? provider = Compute.Find(id);
            string container = provider?.ContainerName
                ?? DockerHostService.DefaultContainerName(id);
            _dockerLogs = await Docker.GetLogsAsync(container);
        }
        catch (Exception ex)
        {
            _dockerError = ex.Message;
        }
        finally
        {
            _dockerBusy = false;
        }
    }

    private void StartDockerDesktop()
    {
        Docker.StartDockerDesktop();
        _dockerStatus = "Starting Docker Desktop. Press Check again in a moment.";
    }

    private async Task CopyProviderTokenAsync(string id)
    {
        string? token = Compute.Token(id);
        if (string.IsNullOrWhiteSpace(token))
        {
            _computeStatus = "This host has no token yet. Generate one in Edit.";
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", token);
            _computeStatus = "Token copied. Use it as SHARPMIND_TOKEN on the host.";
        }
        catch (Exception ex)
        {
            _computeStatus = $"Could not copy the token: {ex.Message}";
        }
    }

    private async Task TestComputeAsync(string id)
    {
        _computeBusy = true;
        _computeError = null;
        _computeErrorHostId = id;
        if (UndeployedRunpod(id) is { } message)
        {
            _computeError = message;
            _computeStatus = null;
            _computeBusy = false;
            return;
        }
        _computeStatus = "Testing the host...";
        StateHasChanged();
        try
        {
            ComputeHealth health = await Compute.TestAsync(id);
            _computeStatus = health.ModelConfigured
                ? $"Online. {health.Model} is {(health.ModelLoaded ? "loaded" : "ready")}."
                : "Online. Upload a model to start chatting.";
        }
        catch (Exception ex)
        {
            _computeError = ex.Message;
            _computeStatus = null;
        }
        finally
        {
            _computeBusy = false;
        }
    }

    /// <summary>Returns a setup message when a Runpod host has not been deployed yet.</summary>
    private string? UndeployedRunpod(string id)
    {
        ComputeProviderSettings? provider = Compute.Find(id);
        if (provider is null || provider.Prefab != ComputeProviderPrefabs.Runpod) return null;
        if (!string.IsNullOrWhiteSpace(provider.RunpodPodId)
            && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out Uri? uri)
            && !uri.IsLoopback)
        {
            return null;
        }

        return "This Runpod host has no running pod yet. Press Deploy pod first"
            + " (and save the host if you just added the API key).";
    }

    private async Task RefreshComputeModelsAsync(string id)
    {
        _computeBusy = true;
        _computeError = null;
        _computeErrorHostId = id;
        if (UndeployedRunpod(id) is { } message)
        {
            _computeError = message;
            _computeStatus = null;
            _computeBusy = false;
            return;
        }
        _computeStatus = "Reading models on the host...";
        StateHasChanged();
        try
        {
            IReadOnlyList<ComputeModelInfo> models = await Compute.RefreshModelsAsync(id);
            _computeStatus = models.Count == 0
                ? "The host has no models yet. Upload one below."
                : $"{models.Count} model(s) on the host.";
        }
        catch (Exception ex)
        {
            _computeError = ex.Message;
            _computeStatus = null;
        }
        finally
        {
            _computeBusy = false;
        }
    }

    private void UseComputeModel(string id, string? model)
    {
        Compute.SetDefaultModel(id, string.IsNullOrWhiteSpace(model) ? null : model);
        _computeStatus = string.IsNullOrWhiteSpace(model)
            ? "The host will use its first model."
            : $"Using {model}.";
    }

    private void SetDefaultCompute(string id)
    {
        Compute.SetDefault(id);
        _computeStatus = "Default Compute host updated.";
    }

    private void RemoveCompute(ComputeProviderSettings provider)
    {
        Compute.Remove(provider.Id);
        if (string.Equals(_editingComputeId, provider.Id, StringComparison.OrdinalIgnoreCase))
            CancelCompute();
        _computeStatus = $"{provider.Name} removed.";
    }

    private void DuplicateCompute(ComputeProviderSettings provider)
    {
        ComputeProviderSettings copy = Compute.Duplicate(provider.Id);
        _computeStatus = $"{copy.Name} added. Edit it to change its name or URL.";
    }

    private async Task BrowseComputeModel()
    {
        PathPickerSelection? selection = await Picker.PickAsync(
            PathPickerMode.File,
            StudioPaths.Models,
            "Choose a model to upload",
            "*.smm;*.gguf");
        if (selection?.Path is { Length: > 0 } path)
            _computeModelPath = path;
    }

    private async Task UploadComputeModelAsync(string id)
    {
        _computeErrorHostId = id;
        _computeError = null;
        if (string.IsNullOrWhiteSpace(_computeModelPath) || !File.Exists(_computeModelPath))
        {
            _computeError = "Choose an existing .smm or .gguf file first.";
            return;
        }

        if (UndeployedRunpod(id) is { } message)
        {
            _computeError = message;
            _computeStatus = null;
            return;
        }

        _computeBusy = true;
        _uploadingId = id;
        _computeUploadProgress = 0;
        _computeStatus = $"Uploading {Path.GetFileName(_computeModelPath)}...";
        StateHasChanged();
        try
        {
            var progress = new Progress<double>(value =>
            {
                _computeUploadProgress = value;
                _ = InvokeAsync(StateHasChanged);
            });
            ComputeUploadResult result = await Compute.UploadModelAsync(
                id, _computeModelPath.Trim(), progress);
            _computeStatus = result.Active
                ? $"{result.Name} uploaded and active ({FormatBytes(result.Bytes)})."
                : $"{result.Name} uploaded ({FormatBytes(result.Bytes)}).";
            _computeUploadProgress = 1;
            await RefreshComputeModelsAsync(id);
        }
        catch (Exception ex)
        {
            _computeError = ex.Message;
            _computeStatus = null;
        }
        finally
        {
            _uploadingId = null;
            _computeBusy = false;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} bytes",
    };
    private void SaveSystemFromEditor(InferenceOverrides value)
    {
        _system = value;
        Inference.SetSystem(value);
        _status = "System defaults saved.";
    }

    private void SaveFamilyFromEditor(InferenceOverrides value)
    {
        _familyDraft = value;
        if (_selectedFamilyId is null) return;
        Inference.SetFamily(_selectedFamilyId, value);
        _familyResolved = Inference.Resolve(_selectedFamilyId, null, null);
        _familySources = _familyResolved.Sources;
        _status = "Family overrides saved.";
    }

    private void SaveModelFromEditor(InferenceOverrides value)
    {
        _modelDraft = value;
        if (_selectedModelPath is null) return;
        Inference.SetModel(_selectedModelPath, value);
        ModelDescriptor? model = _models.FirstOrDefault(item => item.Path == _selectedModelPath);
        _modelResolved = Inference.Resolve(model?.Family.Id, _selectedModelPath, null);
        _modelSources = _modelResolved.Sources;
        _status = "Model overrides saved.";
    }

    private async Task ApplyCustomColorAsync(ChangeEventArgs args)
    {
        string? color = ThemeService.NormalizeColor(args.Value?.ToString());
        if (color is null) return;
        await Theme.SetThemeAsync("custom", color);
        _status = "Custom accent applied.";
    }
    private void SaveSystem()
    {
        Inference.SetSystem(_system);
        _status = "System defaults saved.";
    }

    private void SaveFamily()
    {
        if (_selectedFamilyId is null) return;
        Inference.SetFamily(_selectedFamilyId, _familyDraft);
        _familyResolved = Inference.Resolve(_selectedFamilyId, null, null);
        _familySources = _familyResolved.Sources;
        _status = "Family overrides saved.";
    }

    private void DeleteFamily()
    {
        if (_selectedFamilyId is null) return;
        Inference.DeleteFamily(_selectedFamilyId);
        _familyDraft = new InferenceOverrides();
        _familyResolved = Inference.Resolve(_selectedFamilyId, null, null);
        _familySources = _familyResolved.Sources;
        _status = "Family overrides cleared.";
    }

    private void SaveModel()
    {
        if (_selectedModelPath is null) return;
        Inference.SetModel(_selectedModelPath, _modelDraft);
        ModelDescriptor? model = _models.FirstOrDefault(value => value.Path == _selectedModelPath);
        _modelResolved = Inference.Resolve(model?.Family.Id, _selectedModelPath, null);
        _modelSources = _modelResolved.Sources;
        _status = "Model overrides saved.";
    }

    private void DeleteModel()
    {
        if (_selectedModelPath is null) return;
        Inference.DeleteModel(_selectedModelPath);
        _modelDraft = new InferenceOverrides();
        _status = "Model overrides cleared.";
    }
}
