using System.Net.Sockets;
using SharpMind.Studio.Core.Compute;

namespace SharpMind.Studio.Services;

/// <summary>What the user selected for one Akamai Cloud (Linode) deployment.</summary>
public sealed record AkamaiDeployOptions(
    string TypeId,
    string Region,
    string ImageId,
    string SshKeyId,
    string SshPublicKey,
    double HourlyCost,
    string Image,
    string ContainerName,
    bool UseGpu);

/// <summary>
/// Creates and destroys an Akamai Cloud (Linode) instance through the Linode
/// API, then reuses the existing SSH setup to install Docker/NVIDIA and start
/// the server container. Existing Linode SSH keys only: no key is uploaded.
/// </summary>
public sealed class AkamaiService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(20);

    private readonly ComputeProviderService _compute;
    private readonly AkamaiClient _client;
    private readonly SshHostService _ssh;

    public AkamaiService(
        ComputeProviderService compute,
        AkamaiClient client,
        SshHostService ssh)
    {
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
    }

    public bool HasApiKey(string id) =>
        !string.IsNullOrWhiteSpace(_compute.AkamaiApiKey(id));

    public Task<AkamaiAccount> ValidateAsync(
        string id,
        string? apiKey = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAccountAsync(Key(id, apiKey), cancellationToken);

    public Task<IReadOnlyList<AkamaiRegion>> ListRegionsAsync(
        string id,
        string? apiKey = null,
        CancellationToken cancellationToken = default) =>
        _client.ListRegionsAsync(Key(id, apiKey), cancellationToken);

    public Task<IReadOnlyList<AkamaiType>> ListTypesAsync(
        string id,
        string? apiKey = null,
        CancellationToken cancellationToken = default) =>
        _client.ListTypesAsync(Key(id, apiKey), cancellationToken);

    public Task<IReadOnlyList<AkamaiImage>> ListImagesAsync(
        string id,
        string? apiKey = null,
        CancellationToken cancellationToken = default) =>
        _client.ListImagesAsync(Key(id, apiKey), cancellationToken);

    public Task<IReadOnlyList<AkamaiSshKey>> ListSshKeysAsync(
        string id,
        string? apiKey = null,
        CancellationToken cancellationToken = default) =>
        _client.ListSshKeysAsync(Key(id, apiKey), cancellationToken);

    public Task<IReadOnlyList<AkamaiInstance>> ListInstancesAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _client.ListInstancesAsync(Key(id), cancellationToken);

    public async Task<AkamaiInstance> DeployAsync(
        string id,
        AkamaiDeployOptions options,
        IProgress<SshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ComputeProviderSettings provider = _compute.Find(id)
            ?? throw new AkamaiException("The Compute host was not found.");
        string apiKey = Key(id);
        if (string.IsNullOrWhiteSpace(provider.SshKeyPath))
        {
            throw new AkamaiException(
                "Add the local private key that matches the Linode SSH key before deploying.");
        }

        if (string.IsNullOrWhiteSpace(options.SshKeyId) ||
            string.IsNullOrWhiteSpace(options.SshPublicKey))
        {
            throw new AkamaiException("Choose the Linode SSH key to install on the instance.");
        }
        if (string.IsNullOrWhiteSpace(options.TypeId) ||
            string.IsNullOrWhiteSpace(options.Region))
        {
            throw new AkamaiException("Choose a Linode type and region.");
        }
        if (string.IsNullOrWhiteSpace(options.ImageId))
            throw new AkamaiException("Choose a Linode image.");
        if (SshSetupScript.LooksUnpublished(options.Image))
        {
            throw new AkamaiException(
                $"The server image {options.Image} has no registry prefix, so the host cannot pull it. "
                + "Press Edit and enter the full name, for example your-name/sharpmind-server:gpu.");
        }

        _compute.SetAkamai(
            id,
            apiKey: null,
            options.TypeId,
            options.Region,
            options.ImageId,
            options.SshKeyId,
            sshKeyName: null,
            options.HourlyCost);

        string label = $"sharpmind-{ShortId(id)}";
        string firewallId = await _client.CreateFirewallAsync(
            apiKey, label, cancellationToken).ConfigureAwait(false);

        AkamaiInstance instance;
        try
        {
            instance = await _client.CreateInstanceAsync(apiKey, new AkamaiCreateInstanceRequest
            {
                Region = options.Region,
                TypeId = options.TypeId,
                ImageId = options.ImageId,
                Label = label,
                SshPublicKey = options.SshPublicKey,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteFirewallAsync(apiKey, firewallId, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        try
        {
            await _client.AssignFirewallDeviceAsync(
                apiKey, firewallId, instance.Id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteInstanceAsync(apiKey, instance.Id, cancellationToken)
                .ConfigureAwait(false);
            await TryDeleteFirewallAsync(apiKey, firewallId, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        _compute.SetAkamaiInstance(
            id,
            instance.Id,
            instance.Status,
            instance.Label,
            instance.CreatedUtc ?? DateTimeOffset.UtcNow,
            firewallId);
        progress?.Report(new SshProgress(
            "akamai", $"Linode {instance.Id} created; waiting for boot..."));

        instance = await WaitForBootAsync(apiKey, instance.Id, progress, cancellationToken)
            .ConfigureAwait(false);
        string publicIp = instance.PublicIpv4
            ?? throw new AkamaiException("Akamai did not assign a public IPv4 address.");

        _compute.Update(
            id,
            provider.Name,
            ComputeProviderKinds.Remote,
            $"http://{publicIp}:8080",
            token: null);
        _compute.SetStudioUrl(id, $"http://{publicIp}:5117");
        _compute.SetSsh(
            id,
            host: publicIp,
            port: 22,
            user: "root",
            keyPath: provider.SshKeyPath,
            password: null,
            passphrase: null,
            containerName: options.ContainerName,
            useGpu: options.UseGpu);

        SshCredentials credentials = _compute.SshCredentials(id)
            ?? throw new AkamaiException(
                "The SSH credentials for the new Linode are not available.");
        string token = _compute.EnsureToken(id);
        var request = new SshSetupRequest
        {
            Image = options.Image,
            Token = token,
            ContainerName = options.ContainerName,
            UseGpu = options.UseGpu,
        };

        await WaitForSshAsync(credentials, progress, cancellationToken).ConfigureAwait(false);
        await _ssh.SetupAsync(credentials, request, progress, cancellationToken)
            .ConfigureAwait(false);

        instance = await _client.GetInstanceAsync(apiKey, instance.Id, cancellationToken)
            .ConfigureAwait(false);
        _compute.SetAkamaiInstance(
            id,
            instance.Id,
            instance.Status,
            instance.Label,
            instance.CreatedUtc,
            firewallId);
        return instance;
    }

    public async Task<AkamaiInstance> RefreshAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ComputeProviderSettings provider = _compute.Find(id)
            ?? throw new AkamaiException("The Compute host was not found.");
        if (string.IsNullOrWhiteSpace(provider.AkamaiInstanceId))
            throw new AkamaiException("This host has no Akamai Cloud instance yet.");

        AkamaiInstance instance = await _client.GetInstanceAsync(
            Key(id), provider.AkamaiInstanceId, cancellationToken).ConfigureAwait(false);

        _compute.SetAkamaiInstance(
            id,
            instance.Id,
            instance.Status,
            instance.Label,
            instance.CreatedUtc ?? provider.AkamaiCreatedUtc,
            provider.AkamaiFirewallId);

        if (instance.IsRunning && instance.PublicIpv4 is { Length: > 0 } publicIp)
        {
            _compute.Update(
                id,
                provider.Name,
                ComputeProviderKinds.Remote,
                $"http://{publicIp}:8080",
                token: null);
            _compute.SetStudioUrl(id, $"http://{publicIp}:5117");
            _compute.SetSsh(
                id,
                host: publicIp,
                port: 22,
                user: "root",
                keyPath: provider.SshKeyPath,
                password: null,
                passphrase: null,
                containerName: provider.SshContainerName ?? "sharpmind",
                useGpu: provider.UseGpu);
        }

        return instance;
    }

    public async Task DestroyAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ComputeProviderSettings provider = _compute.Find(id)
            ?? throw new AkamaiException("The Compute host was not found.");
        if (string.IsNullOrWhiteSpace(provider.AkamaiInstanceId)) return;

        string apiKey = Key(id);
        await _client.DeleteInstanceAsync(
            apiKey, provider.AkamaiInstanceId, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(provider.AkamaiFirewallId))
        {
            await TryDeleteFirewallAsync(
                apiKey, provider.AkamaiFirewallId, cancellationToken).ConfigureAwait(false);
        }

        _compute.SetAkamaiInstance(id, null, "destroyed", null, null, null);
    }

    private async Task<AkamaiInstance> WaitForBootAsync(
        string apiKey,
        string instanceId,
        IProgress<SshProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + BootTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AkamaiInstance instance = await _client.GetInstanceAsync(
                apiKey, instanceId, cancellationToken).ConfigureAwait(false);
            progress?.Report(new SshProgress(
                "akamai", $"Linode {instance.Status}..."));

            if (instance.IsRunning) return instance;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new AkamaiException(
            "Akamai did not finish booting the instance within 20 minutes. "
            + "Use Refresh to check it again, or Destroy instance to stop billing.");
    }

    private static async Task WaitForSshAsync(
        SshCredentials credentials,
        IProgress<SshProgress>? progress,
        CancellationToken cancellationToken)
    {
        SshTarget target = credentials.Target;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CanConnectAsync(target, cancellationToken).ConfigureAwait(false))
            {
                if (attempt > 1)
                {
                    progress?.Report(new SshProgress(
                        "ssh", $"{target.Display} is accepting connections."));
                }

                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AkamaiException(
                    $"SSH on {target.Display} did not open within 5 minutes. The instance is "
                    + "running; check that the Akamai firewall allows TCP 22, then press Test SSH.");
            }

            progress?.Report(new SshProgress(
                "ssh", $"Waiting for SSH on {target.Display} (attempt {attempt})..."));
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> CanConnectAsync(
        SshTarget target,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task TryDeleteInstanceAsync(
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteInstanceAsync(apiKey, instanceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The boot-time failure is the useful error; cleanup is best-effort.
        }
    }

    private async Task TryDeleteFirewallAsync(
        string apiKey,
        string firewallId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteFirewallAsync(apiKey, firewallId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The instance is the important resource; a leftover firewall is harmless.
        }
    }

    private string Key(string id, string? apiKey = null)
    {
        string key = string.IsNullOrWhiteSpace(apiKey)
            ? _compute.AkamaiApiKey(id) ?? ""
            : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new AkamaiException("Add an Akamai Cloud API token first.");
        return key;
    }

    private static string ShortId(string providerId)
    {
        string suffix = providerId.StartsWith("compute-", StringComparison.OrdinalIgnoreCase)
            ? providerId["compute-".Length..]
            : providerId;
        suffix = new string([.. suffix.Where(char.IsLetterOrDigit)]);
        return suffix.Length > 10 ? suffix[..10] : suffix;
    }
}
