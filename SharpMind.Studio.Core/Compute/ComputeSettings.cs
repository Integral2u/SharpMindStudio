using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Serialization;

namespace SharpMind.Studio.Core.Compute;

/// <summary>
/// One SharpMind Compute host. The token is never stored in plain text: it is
/// AES-GCM ciphertext bound to a key file next to the settings, using the same
/// secret box as the Gateway and the other provider keys.
/// </summary>
public sealed class ComputeProviderSettings
{
    public required string Id { get; init; }
    public string Name { get; set; } = "SharpMind Compute";
    public string Kind { get; set; } = ComputeProviderKinds.Local;

    /// <summary>Which Add/Edit flavour this host uses: local-docker, runpod or custom.</summary>
    public string Prefab { get; set; } = ComputeProviderPrefabs.Custom;

    /// <summary>Host root, for example <c>http://127.0.0.1:8080</c> or a Runpod proxy URL.</summary>
    public string BaseUrl { get; set; } = ComputeProviderSettings.DefaultLocalUrl;

    public const string DefaultLocalUrl = "http://127.0.0.1:8080";

    public string? TokenProtected { get; set; }

    /// <summary>Where the combined SharpMind Server serves the Studio web UI.</summary>
    public string? StudioUrl { get; set; }

    /// <summary>The model the host should serve by default.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Models seen on this host, kept so the chooser works while offline.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>Models copied to the host from this machine or HuggingFace.</summary>
    public List<string> UploadedModels { get; set; } = [];

    // --- Local Docker helper -------------------------------------------
    // Only used when Kind is local; a remote host ignores these.

    public string? DockerImage { get; set; }
    public int? DockerPort { get; set; }
    public bool UseGpu { get; set; }
    public string? ContainerName { get; set; }

    // --- SSH helper ------------------------------------------------------
    // Optional: lets Studio log in, install Docker, pull the image and start
    // the combined server container without a terminal.

    public string? SshHost { get; set; }
    public int? SshPort { get; set; } = SshTargets.DefaultPort;
    public string? SshUser { get; set; } = SshTargets.DefaultUser;
    public string? SshKeyPath { get; set; }
    public string? SshPasswordProtected { get; set; }
    public string? SshKeyPassphraseProtected { get; set; }
    public string? SshContainerName { get; set; }

    // --- Runpod helper --------------------------------------------------
    // Optional: lets Studio create and terminate the pod through Runpod's API.

    public string? RunpodApiKeyProtected { get; set; }
    public string? RunpodImage { get; set; }
    public string? RunpodGpuType { get; set; }
    public int? RunpodDiskGb { get; set; }
    public string? RunpodPodId { get; set; }
    public string? RunpodPodStatus { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? LastStatus { get; set; }

    /// <summary>Last measured health, for the provider dot.</summary>
    public bool? Healthy { get; set; }

    public string NormalizedKind =>
        ComputeProviderPrefabs.Find(Prefab).Kind;
}

public static class ComputeProviderKinds
{
    public const string Local = "local";
    public const string Remote = "remote";

    public static string Display(string? kind) =>
        string.Equals(kind, Remote, StringComparison.OrdinalIgnoreCase)
            ? "Remote host"
            : "Local Docker";
}

/// <summary>
/// A host flavour in the Add/Edit form. Adding another provider (Vultr, AWS,
/// a bare server) is a new entry here plus its fields in the panel.
/// </summary>
public sealed record ComputeProviderPrefab(
    string Id,
    string Name,
    string Kind,
    string Description,
    bool Docker,
    bool Runpod,
    string? ReferralUrl = null,
    string? AffiliateCode = null);

public static class ComputeProviderPrefabs
{
    public const string LocalDocker = "local-docker";
    public const string Runpod = "runpod";
    public const string Vultr = "vultr";
    public const string VastAi = "vast-ai";
    public const string MaxCloudOn = "maxcloudon";
    public const string Novita = "novita";
    public const string Custom = "custom";

    public static IReadOnlyList<ComputeProviderPrefab> All { get; } =
    [
        new(LocalDocker, "Local Docker", ComputeProviderKinds.Local,
            "A container on this PC, started from Studio.", Docker: true, Runpod: false),
        new(Runpod, "Runpod GPU", ComputeProviderKinds.Remote,
            "Studio creates the GPU pod through Runpod's API.", Docker: false, Runpod: true,
            ReferralUrl: "https://runpod.io?ref=ltj4z1kb"),
        new(Vultr, "Vultr cloud server", ComputeProviderKinds.Remote,
            "A Vultr instance running the SharpMind Server image.", Docker: false, Runpod: false,
            ReferralUrl: "https://www.vultr.com/?ref=9933964"),
        new(VastAi, "Vast.ai GPU", ComputeProviderKinds.Remote,
            "A Vast.ai GPU instance running the SharpMind Server image. Paste its public URL and token.",
            Docker: false, Runpod: false,
            ReferralUrl: "https://cloud.vast.ai/?ref_id=854920"),
        new(MaxCloudOn, "MaxCloudOn GPU", ComputeProviderKinds.Remote,
            "A MaxCloudOn GPU instance running the SharpMind Server image. Paste its public URL and token.",
            Docker: false, Runpod: false,
            ReferralUrl: "https://maxcloudon.com/",
            AffiliateCode: "SharpMindStudio"),
        new(Novita, "Novita GPU", ComputeProviderKinds.Remote,
            "A Novita GPU instance running the SharpMind Server image. Paste its public URL and token.",
            Docker: false, Runpod: false,
            ReferralUrl: "https://novita.ai/?ref=zgq0ymmy&utm_source=affiliate"),
        new(Custom, "Custom remote server", ComputeProviderKinds.Remote,
            "Any machine that already runs the SharpMind Server image.", Docker: false, Runpod: false),
    ];

    public static ComputeProviderPrefab Find(string? id) =>
        All.FirstOrDefault(prefab =>
            prefab.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(prefab => prefab.Id == Custom);

    public static string Normalize(string? id) => Find(id).Id;
}

/// <summary>The persisted set of Compute hosts and which one is the default.</summary>
public sealed class ComputeSettingsDocument
{
    public const string CurrentSchema = "studio-compute/1";

    public string Schema { get; init; } = CurrentSchema;
    public List<ComputeProviderSettings> Providers { get; set; } = [];
    public string? DefaultProviderId { get; set; }

    /// <summary>The last image pushed by the Publish panel, reused by new hosts.</summary>
    public string? RegistryImage { get; set; }
}

/// <summary>A settings entry resolved with its decrypted token, ready to call.</summary>
public sealed record ComputeProvider(
    string Id,
    string Name,
    string Kind,
    string BaseUrl,
    string Token,
    string? DefaultModel,
    IReadOnlyList<string> Models)
{
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(BaseUrl) && HasToken;
}

/// <summary>JSON-backed Compute host settings with an encrypted token per host.</summary>
public sealed class ComputeSettingsStore
{
    private readonly object _gate = new();
    private readonly string _keyFile;
    private ComputeSettingsDocument _document;

    public ComputeSettingsStore(string settingsPath, string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyFile);
        Path = System.IO.Path.GetFullPath(settingsPath);
        _keyFile = System.IO.Path.GetFullPath(keyFile);
        _document = Load();
    }

    public string Path { get; }

    public ComputeSettingsDocument Document
    {
        get { lock (_gate) return _document; }
    }

    public IReadOnlyList<ComputeProviderSettings> Providers
    {
        get { lock (_gate) return [.. _document.Providers]; }
    }

    public string? DefaultProviderId
    {
        get { lock (_gate) return _document.DefaultProviderId; }
    }

    public string? RegistryImage
    {
        get { lock (_gate) return _document.RegistryImage; }
    }

    public void SetRegistryImage(string? image)
    {
        lock (_gate)
        {
            _document.RegistryImage = string.IsNullOrWhiteSpace(image) ? null : image.Trim();
            SaveLocked();
        }
    }

    public ComputeProviderSettings? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            return _document.Providers.FirstOrDefault(provider =>
                provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string? Token(string id)
    {
        ComputeProviderSettings? provider = Find(id);
        if (provider is null) return null;
        lock (_gate)
            return GatewaySecretBox.Unprotect(provider.TokenProtected, _keyFile);
    }

    public ComputeProvider? Resolve(string? id)
    {
        ComputeProviderSettings? provider = Find(id) ?? Find(_document.DefaultProviderId);
        if (provider is null) return null;

        string token;
        lock (_gate)
            token = GatewaySecretBox.Unprotect(provider.TokenProtected, _keyFile) ?? "";

        return new ComputeProvider(
            provider.Id,
            provider.Name,
            provider.NormalizedKind,
            provider.BaseUrl,
            token,
            provider.DefaultModel,
            [.. provider.Models]);
    }

    public ComputeProviderSettings Add(
        string name,
        string? kind,
        string? baseUrl,
        string? token,
        string? prefab = null)
    {
        ComputeProviderPrefab flavor = prefab is null
            ? ComputeProviderPrefabs.Find(
                string.Equals(kind, ComputeProviderKinds.Local, StringComparison.OrdinalIgnoreCase)
                    ? ComputeProviderPrefabs.LocalDocker
                    : ComputeProviderPrefabs.Custom)
            : ComputeProviderPrefabs.Find(prefab);

        var provider = new ComputeProviderSettings
        {
            Id = $"compute-{Guid.NewGuid().ToString("n")[..10]}",
            Name = string.IsNullOrWhiteSpace(name) ? "SharpMind Compute" : name.Trim(),
            Kind = flavor.Kind,
            Prefab = flavor.Id,
            BaseUrl = NormalizeBaseUrl(baseUrl),
            DockerPort = ComputeProviderPresets.DefaultPort,
            DockerImage = ComputeProviderPresets.DefaultImage,
        };
        provider.ContainerName = ComputeProviderPresets.ContainerName(provider.Id);
        provider.StudioUrl = ComputeProviderPresets.DeriveStudioUrl(provider.BaseUrl);

        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(token))
                provider.TokenProtected = GatewaySecretBox.Protect(token.Trim(), _keyFile);

            _document.Providers.Add(provider);
            _document.DefaultProviderId ??= provider.Id;
            SaveLocked();
        }

        return provider;
    }

    /// <summary>Updates a provider. A null token keeps the current one; blank clears it.</summary>
    public bool Update(
        string id,
        string name,
        string? kind,
        string? baseUrl,
        string? token,
        string? defaultModel = null,
        string? prefab = null)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.Name = string.IsNullOrWhiteSpace(name) ? provider.Name : name.Trim();
            provider.Kind = string.Equals(kind, ComputeProviderKinds.Remote, StringComparison.OrdinalIgnoreCase)
                ? ComputeProviderKinds.Remote
                : ComputeProviderKinds.Local;
            provider.Prefab = prefab is null
                ? (string.Equals(provider.Kind, ComputeProviderKinds.Local, StringComparison.OrdinalIgnoreCase)
                    ? ComputeProviderPrefabs.LocalDocker
                    : provider.Prefab)
                : ComputeProviderPrefabs.Normalize(prefab);
            provider.Kind = ComputeProviderPrefabs.Find(provider.Prefab).Kind;
            provider.BaseUrl = NormalizeBaseUrl(baseUrl);
            provider.StudioUrl = ComputeProviderPresets.DeriveStudioUrl(provider.BaseUrl);

            if (token is not null)
            {
                provider.TokenProtected = string.IsNullOrWhiteSpace(token)
                    ? null
                    : GatewaySecretBox.Protect(token.Trim(), _keyFile);
            }

            if (!string.IsNullOrWhiteSpace(defaultModel))
                provider.DefaultModel = defaultModel.Trim();

            SaveLocked();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            _document.Providers.Remove(provider);
            if (string.Equals(_document.DefaultProviderId, id, StringComparison.OrdinalIgnoreCase))
                _document.DefaultProviderId = _document.Providers.FirstOrDefault()?.Id;
            SaveLocked();
            return true;
        }
    }

    public bool SetDefault(string? id)
    {
        lock (_gate)
        {
            if (id is null)
            {
                _document.DefaultProviderId = null;
                SaveLocked();
                return true;
            }

            if (!_document.Providers.Any(provider =>
                    provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _document.DefaultProviderId = id;
            SaveLocked();
            return true;
        }
    }

    public bool RememberModel(string id, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            string clean = model.Trim();
            if (!provider.Models.Any(value => value.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                provider.Models.Add(clean);
            provider.DefaultModel = clean;
            SaveLocked();
            return true;
        }
    }

    public bool ForgetModel(string id, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            string clean = model.Trim();
            int removed = provider.Models.RemoveAll(value =>
                value.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;

            if (string.Equals(provider.DefaultModel, clean, StringComparison.OrdinalIgnoreCase))
                provider.DefaultModel = provider.Models.FirstOrDefault();
            SaveLocked();
            return true;
        }
    }

    public void SetDefaultModel(string id, string? model)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return;

            provider.DefaultModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            SaveLocked();
        }
    }

    /// <summary>Stores the local Docker helper settings without touching the URL or token.</summary>
    public void SetDocker(string id, string? image, int port, bool useGpu, string containerName)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return;

            provider.DockerImage = string.IsNullOrWhiteSpace(image) ? null : image.Trim();
            provider.DockerPort = port is < 1 or > 65535 ? null : port;
            provider.UseGpu = useGpu;
            provider.ContainerName = string.IsNullOrWhiteSpace(containerName)
                ? null
                : containerName.Trim();
            SaveLocked();
        }
    }

    /// <summary>Sets or clears a provider token. A null token leaves the current value alone.</summary>
    public bool SetToken(string id, string? token)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.TokenProtected = string.IsNullOrWhiteSpace(token)
                ? null
                : GatewaySecretBox.Protect(token.Trim(), _keyFile);
            SaveLocked();
            return true;
        }
    }

    /// <summary>Overrides the derived Studio UI address for a combined server.</summary>
    public bool SetStudioUrl(string id, string? url)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.StudioUrl = string.IsNullOrWhiteSpace(url)
                ? ComputeProviderPresets.DeriveStudioUrl(provider.BaseUrl)
                : ComputeProviderPresets.NormalizeUrl(url.Trim(), 5117);
            SaveLocked();
            return true;
        }
    }

    /// <summary>The decrypted Runpod API key, or null when none is saved.</summary>
    public string? RunpodApiKey(string id)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return provider is null
                ? null
                : GatewaySecretBox.Unprotect(provider.RunpodApiKeyProtected, _keyFile);
        }
    }

    /// <summary>Stores the Runpod deployment settings. A null key keeps the current one.</summary>
    public bool SetRunpod(
        string id,
        string? apiKey,
        string? image,
        string? gpuType,
        int? diskGb)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            if (apiKey is not null)
            {
                provider.RunpodApiKeyProtected = string.IsNullOrWhiteSpace(apiKey)
                    ? null
                    : GatewaySecretBox.Protect(apiKey.Trim(), _keyFile);
            }

            if (!string.IsNullOrWhiteSpace(image)) provider.RunpodImage = image.Trim();
            if (!string.IsNullOrWhiteSpace(gpuType)) provider.RunpodGpuType = gpuType.Trim();
            if (diskGb is > 0) provider.RunpodDiskGb = diskGb;
            SaveLocked();
            return true;
        }
    }

    public bool SetRunpodPod(string id, string? podId, string? status)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.RunpodPodId = string.IsNullOrWhiteSpace(podId) ? null : podId.Trim();
            provider.RunpodPodStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Stores the SSH helper settings. Null password/passphrase keep the saved
    /// secret; a blank value clears it. Secrets are encrypted like every other key.
    /// </summary>
    public bool SetSsh(
        string id,
        string? host,
        int port,
        string? user,
        string? keyPath,
        string? password,
        string? passphrase,
        string? containerName,
        bool useGpu)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.SshHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
            provider.SshPort = port is > 0 and <= 65535 ? port : SshTargets.DefaultPort;
            provider.SshUser = string.IsNullOrWhiteSpace(user)
                ? SshTargets.DefaultUser
                : user.Trim();
            provider.SshKeyPath = string.IsNullOrWhiteSpace(keyPath) ? null : keyPath.Trim();
            provider.UseGpu = useGpu;
            provider.SshContainerName = string.IsNullOrWhiteSpace(containerName)
                ? "sharpmind"
                : containerName.Trim();

            if (password is not null)
            {
                provider.SshPasswordProtected = string.IsNullOrWhiteSpace(password)
                    ? null
                    : GatewaySecretBox.Protect(password, _keyFile);
            }

            if (passphrase is not null)
            {
                provider.SshKeyPassphraseProtected = string.IsNullOrWhiteSpace(passphrase)
                    ? null
                    : GatewaySecretBox.Protect(passphrase, _keyFile);
            }

            SaveLocked();
            return true;
        }
    }

    /// <summary>The decrypted SSH password, or null when the host uses a key.</summary>
    public string? SshPassword(string id)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return provider is null
                ? null
                : GatewaySecretBox.Unprotect(provider.SshPasswordProtected, _keyFile);
        }
    }

    /// <summary>The decrypted private-key passphrase, or null when the key is unencrypted.</summary>
    public string? SshKeyPassphrase(string id)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return provider is null
                ? null
                : GatewaySecretBox.Unprotect(provider.SshKeyPassphraseProtected, _keyFile);
        }
    }

    /// <summary>Updates the server image without touching the Docker ports or token.</summary>
    public bool SetImage(string id, string? image)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.DockerImage = string.IsNullOrWhiteSpace(image) ? null : image.Trim();
            SaveLocked();
            return true;
        }
    }

    /// <summary>Replaces the API address and re-derives the Studio URL.</summary>
    public bool SetBaseUrl(string id, string? url)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.BaseUrl = NormalizeBaseUrl(url);
            provider.StudioUrl = ComputeProviderPresets.DeriveStudioUrl(provider.BaseUrl);
            SaveLocked();
            return true;
        }
    }

    /// <summary>Changes the host flavour and keeps Kind in sync with it.</summary>
    public bool SetPrefab(string id, string? prefab)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return false;

            provider.Prefab = ComputeProviderPrefabs.Normalize(prefab);
            provider.Kind = ComputeProviderPrefabs.Find(provider.Prefab).Kind;
            SaveLocked();
            return true;
        }
    }

    public void RecordProbe(string id, bool healthy, string? status)
    {
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return;

            provider.Healthy = healthy;
            provider.LastStatus = status;
            if (healthy) provider.LastSeenUtc = DateTimeOffset.UtcNow;
            SaveLocked();
        }
    }

    public void ReplaceModels(string id, IEnumerable<string> models, string? healthyModel)
    {
        ArgumentNullException.ThrowIfNull(models);
        lock (_gate)
        {
            ComputeProviderSettings? provider = _document.Providers.FirstOrDefault(value =>
                value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return;

            provider.Models = [.. models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            if (!string.IsNullOrWhiteSpace(healthyModel))
                provider.DefaultModel = healthyModel.Trim();
            else if (string.IsNullOrWhiteSpace(provider.DefaultModel)
                     || !provider.Models.Any(model => model.Equals(provider.DefaultModel, StringComparison.OrdinalIgnoreCase)))
                provider.DefaultModel = provider.Models.FirstOrDefault();

            SaveLocked();
        }
    }

    private static string NormalizeBaseUrl(string? url)
    {
        string value = string.IsNullOrWhiteSpace(url)
            ? ComputeProviderSettings.DefaultLocalUrl
            : url.Trim().TrimEnd('/');
        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            value = value[..^3].TrimEnd('/');
        return value;
    }

    private ComputeSettingsDocument Load()
    {
        try
        {
            if (!File.Exists(Path)) return NormalizeDocument(new ComputeSettingsDocument());
            ComputeSettingsDocument document = JsonFile.Read<ComputeSettingsDocument>(Path);
            if (document.Schema != ComputeSettingsDocument.CurrentSchema)
                return NormalizeDocument(new ComputeSettingsDocument());
            return NormalizeDocument(document);
        }
        catch
        {
            return NormalizeDocument(new ComputeSettingsDocument());
        }
    }

    private static ComputeSettingsDocument NormalizeDocument(ComputeSettingsDocument document)
    {
        document.Providers ??= [];
        foreach (ComputeProviderSettings provider in document.Providers)
        {
            provider.Models ??= [];
            provider.UploadedModels ??= [];
            if (string.IsNullOrWhiteSpace(provider.Prefab))
            {
                provider.Prefab = string.Equals(
                    provider.Kind, ComputeProviderKinds.Local, StringComparison.OrdinalIgnoreCase)
                    ? ComputeProviderPrefabs.LocalDocker
                    : (!string.IsNullOrWhiteSpace(provider.RunpodPodId)
                       || !string.IsNullOrWhiteSpace(provider.RunpodApiKeyProtected))
                        ? ComputeProviderPrefabs.Runpod
                        : ComputeProviderPrefabs.Custom;
            }
            provider.Prefab = ComputeProviderPrefabs.Normalize(provider.Prefab);
            provider.Kind = ComputeProviderPrefabs.Find(provider.Prefab).Kind;
            provider.BaseUrl = NormalizeBaseUrl(provider.BaseUrl);
            provider.StudioUrl = string.IsNullOrWhiteSpace(provider.StudioUrl)
                ? ComputeProviderPresets.DeriveStudioUrl(provider.BaseUrl)
                : provider.StudioUrl;
            provider.DockerImage ??= ComputeProviderPresets.DefaultImage;
            provider.DockerPort ??= ComputeProviderPresets.DefaultPort;
            provider.ContainerName ??= ComputeProviderPresets.ContainerName(provider.Id);
            provider.SshPort ??= SshTargets.DefaultPort;
            provider.SshUser ??= SshTargets.DefaultUser;
            provider.SshContainerName ??= "sharpmind";
            provider.RunpodImage ??= ComputeProviderPresets.DefaultServerImage;
            provider.RunpodGpuType ??= ComputeProviderPresets.DefaultGpuType;
            provider.RunpodDiskGb ??= ComputeProviderPresets.DefaultRunpodDiskGb;
        }

        if (document.Providers.Count == 0)
        {
            var local = new ComputeProviderSettings
            {
                Id = $"compute-{Guid.NewGuid().ToString("n")[..10]}",
                Name = "Local Docker",
                Kind = ComputeProviderKinds.Local,
                Prefab = ComputeProviderPrefabs.LocalDocker,
                BaseUrl = ComputeProviderSettings.DefaultLocalUrl,
                DockerPort = ComputeProviderPresets.DefaultPort,
                DockerImage = ComputeProviderPresets.DefaultImage,
            };
            local.ContainerName = ComputeProviderPresets.ContainerName(local.Id);
            document.Providers.Add(local);
        }

        if (string.IsNullOrWhiteSpace(document.DefaultProviderId)
            || !document.Providers.Any(provider =>
                provider.Id.Equals(document.DefaultProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            document.DefaultProviderId = document.Providers[0].Id;
        }

        return document;
    }

    private void SaveLocked()
    {
        try
        {
            JsonFile.Write(Path, _document);
        }
        catch
        {
        }
    }
}
