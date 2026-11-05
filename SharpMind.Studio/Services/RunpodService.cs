using SharpMind.Studio.Core.Compute;

namespace SharpMind.Studio.Services;

/// <summary>
/// Deploys and terminates the GPU pod for a Remote Compute provider through
/// Runpod's REST API, so the user does not have to use the Runpod console.
/// </summary>
public sealed class RunpodService
{
    private readonly ComputeProviderService _compute;
    private readonly RunpodClient _client;

    public RunpodService(ComputeProviderService compute, RunpodClient client)
    {
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool HasApiKey(string id) => !string.IsNullOrWhiteSpace(_compute.RunpodApiKey(id));

    public async Task ValidateAsync(string id, string? apiKey, CancellationToken cancellationToken = default)
    {
        string key = string.IsNullOrWhiteSpace(apiKey)
            ? _compute.RunpodApiKey(id) ?? ""
            : apiKey.Trim();
        await _client.ValidateApiKeyAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunpodPod> DeployAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ComputeProviderSettings provider = _compute.Find(providerId)
            ?? throw new RunpodException("The Compute host was not found.");

        string apiKey = _compute.RunpodApiKey(providerId)
            ?? throw new RunpodException("Add a Runpod API key first.");
        string image = string.IsNullOrWhiteSpace(provider.RunpodImage)
            ? ComputeProviderPresets.DefaultServerImage
            : provider.RunpodImage.Trim();

        if (!LooksLikeRegistryImage(image))
        {
            throw new RunpodException(
                "Publish the image to Docker Hub first, then use <user>/sharpmind-server:gpu.");
        }

        // The same token the provider will use; Studio must keep it to talk to the pod.
        string token = _compute.EnsureToken(providerId);
        string name = $"sharpmind-{ShortId(providerId)}";

        var request = new RunpodCreateRequest
        {
            Name = name,
            Image = image,
            GpuType = string.IsNullOrWhiteSpace(provider.RunpodGpuType)
                ? ComputeProviderPresets.DefaultGpuType
                : provider.RunpodGpuType.Trim(),
            DiskGb = provider.RunpodDiskGb ?? ComputeProviderPresets.DefaultRunpodDiskGb,
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SHARPMIND_TOKEN"] = token,
                ["SHARPMIND_DATA"] = "/data/SharpMind",
                ["SHARPMIND_MODELS_DIR"] = "/data/SharpMind/Models",
                ["SHARPMIND_PLUGINS"] = "/app/plugins",
            },
        };

        RunpodPod pod = await _client
            .CreatePodAsync(apiKey, request, cancellationToken)
            .ConfigureAwait(false);
        _compute.SetRunpodPod(providerId, pod.Id, pod.Status);
        return pod;
    }

    /// <summary>Refreshes the pod and, once it is running, fills the provider URLs.</summary>
    public async Task<RunpodPod> RefreshAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ComputeProviderSettings provider = _compute.Find(providerId)
            ?? throw new RunpodException("The Compute host was not found.");
        if (string.IsNullOrWhiteSpace(provider.RunpodPodId))
            throw new RunpodException("This host has no Runpod pod yet.");

        string apiKey = _compute.RunpodApiKey(providerId)
            ?? throw new RunpodException("Add a Runpod API key first.");

        RunpodPod pod = await _client
            .GetPodAsync(apiKey, provider.RunpodPodId, cancellationToken)
            .ConfigureAwait(false);
        _compute.SetRunpodPod(providerId, pod.Id, pod.Status);

        if (string.Equals(pod.Status, "RUNNING", StringComparison.OrdinalIgnoreCase))
            ApplyPodUrls(providerId, pod.Id);

        return pod;
    }

    public async Task TerminateAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ComputeProviderSettings provider = _compute.Find(providerId)
            ?? throw new RunpodException("The Compute host was not found.");
        if (string.IsNullOrWhiteSpace(provider.RunpodPodId)) return;

        string apiKey = _compute.RunpodApiKey(providerId)
            ?? throw new RunpodException("Add a Runpod API key first.");

        await _client
            .TerminatePodAsync(apiKey, provider.RunpodPodId, cancellationToken)
            .ConfigureAwait(false);
        _compute.SetRunpodPod(providerId, null, "TERMINATED");
    }

    public void ApplyPodUrls(string providerId, string podId)
    {
        ComputeProviderSettings? provider = _compute.Find(providerId);
        if (provider is null) return;

        string api = $"https://{podId}-8080.proxy.runpod.net";
        _compute.Update(providerId, provider.Name, ComputeProviderKinds.Remote, api, token: null);
        _compute.SetStudioUrl(providerId, $"https://{podId}-5117.proxy.runpod.net");
    }

    private static bool LooksLikeRegistryImage(string image) =>
        image.Contains('/', StringComparison.Ordinal);

    private static string ShortId(string providerId)
    {
        string suffix = providerId.StartsWith("compute-", StringComparison.OrdinalIgnoreCase)
            ? providerId["compute-".Length..]
            : providerId;
        suffix = new string([.. suffix.Where(char.IsLetterOrDigit)]);
        return suffix.Length > 10 ? suffix[..10] : suffix;
    }
}
