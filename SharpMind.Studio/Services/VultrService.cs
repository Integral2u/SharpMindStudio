using SharpMind.Studio.Core.Compute;

namespace SharpMind.Studio.Services;

/// <summary>What the user selected for one Vultr deployment.</summary>
public sealed record VultrDeployOptions(
    string PlanId,
    string Region,
    int OsId,
    string SshKeyId,
    double HourlyCost,
    string Image,
    string ContainerName,
    bool UseGpu);

/// <summary>
/// Creates and destroys a Vultr instance through the Vultr API, then reuses the
/// existing SSH setup to install Docker/NVIDIA and start the server container.
/// Existing Vultr SSH keys only: no key is uploaded from this machine.
/// </summary>
public sealed class VultrService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(20);

    private readonly ComputeProviderService _compute;
    private readonly VultrClient _client;
    private readonly SshHostService _ssh;

    public VultrService(
        ComputeProviderService compute,
        VultrClient client,
        SshHostService ssh)
    {
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
    }

    public bool HasApiKey(string id) => !string.IsNullOrWhiteSpace(_compute.VultrApiKey(id));

    public Task<VultrAccount> ValidateAsync(
        string id,
        string? apiKey = null,
        CancellationToken cancellationToken = default) =>
        _client.GetAccountAsync(Key(id, apiKey), cancellationToken);

    public Task<IReadOnlyList<VultrPlan>> ListPlansAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _client.ListPlansAsync(Key(id), cancellationToken);

    public Task<IReadOnlyList<VultrRegion>> ListRegionsAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _client.ListRegionsAsync(Key(id), cancellationToken);

    public Task<IReadOnlyList<VultrOs>> ListOsAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _client.ListOsAsync(Key(id), cancellationToken);

    public Task<IReadOnlyList<VultrSshKey>> ListSshKeysAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _client.ListSshKeysAsync(Key(id), cancellationToken);

    public Task<IReadOnlyList<VultrInstance>> ListInstancesAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        _client.ListInstancesAsync(Key(id), cancellationToken);

    /// <summary>
    /// Creates the firewall and instance, waits for boot, fills the host URLs and
    /// runs the SSH setup. The instance is tracked before the wait so a failure
    /// still leaves a destroyable record.
    /// </summary>
    public async Task<VultrInstance> DeployAsync(
        string id,
        VultrDeployOptions options,
        IProgress<SshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ComputeProviderSettings provider = _compute.Find(id)
            ?? throw new VultrException("The Compute host was not found.");
        string apiKey = Key(id);
        if (string.IsNullOrWhiteSpace(provider.SshKeyPath))
        {
            throw new VultrException(
                "Add the local private key that matches the Vultr SSH key before deploying.");
        }

        if (string.IsNullOrWhiteSpace(options.SshKeyId))
            throw new VultrException("Choose the Vultr SSH key to install on the instance.");
        if (string.IsNullOrWhiteSpace(options.PlanId) || string.IsNullOrWhiteSpace(options.Region))
            throw new VultrException("Choose a Vultr plan and region.");
        if (options.OsId <= 0)
            throw new VultrException("Choose a Vultr operating system.");

        _compute.SetVultr(
            id,
            apiKey: null,
            options.PlanId,
            options.Region,
            options.OsId,
            options.SshKeyId,
            sshKeyName: null,
            options.HourlyCost);

        string label = $"sharpmind-{ShortId(id)}";
        string firewallId = await EnsureFirewallAsync(apiKey, label, cancellationToken)
            .ConfigureAwait(false);

        VultrInstance instance;
        try
        {
            instance = await _client.CreateInstanceAsync(apiKey, new VultrCreateInstanceRequest
            {
                Region = options.Region,
                Plan = options.PlanId,
                OsId = options.OsId,
                Label = label,
                Hostname = label,
                SshKeyIds = [options.SshKeyId],
                FirewallGroupId = firewallId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteFirewallAsync(apiKey, firewallId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        _compute.SetVultrInstance(
            id,
            instance.Id,
            instance.Status,
            instance.Label,
            instance.CreatedUtc ?? DateTimeOffset.UtcNow,
            firewallId);
        progress?.Report(new SshProgress("vultr", $"Instance {instance.Id} created; waiting for boot..."));

        instance = await WaitForBootAsync(apiKey, instance.Id, progress, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(instance.MainIp))
            throw new VultrException("Vultr did not assign a public IPv4 address.");

        string baseUrl = $"http://{instance.MainIp}:8080";
        string studioUrl = $"http://{instance.MainIp}:5117";
        _compute.Update(id, provider.Name, ComputeProviderKinds.Remote, baseUrl, token: null);
        _compute.SetStudioUrl(id, studioUrl);
        _compute.SetSsh(
            id,
            host: instance.MainIp,
            port: 22,
            user: "root",
            keyPath: provider.SshKeyPath,
            password: null,
            passphrase: null,
            containerName: options.ContainerName,
            useGpu: options.UseGpu);

        SshCredentials credentials = _compute.SshCredentials(id)
            ?? throw new VultrException("The SSH credentials for the new instance are not available.");
        string token = _compute.EnsureToken(id);
        var request = new SshSetupRequest
        {
            Image = options.Image,
            Token = token,
            ContainerName = options.ContainerName,
            UseGpu = options.UseGpu,
        };

        await _ssh.SetupAsync(credentials, request, progress, cancellationToken)
            .ConfigureAwait(false);

        instance = await _client.GetInstanceAsync(apiKey, instance.Id, cancellationToken)
            .ConfigureAwait(false);
        _compute.SetVultrInstance(
            id,
            instance.Id,
            instance.Status,
            instance.Label,
            instance.CreatedUtc,
            firewallId);
        return instance;
    }

    public async Task<VultrInstance> RefreshAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ComputeProviderSettings provider = _compute.Find(id)
            ?? throw new VultrException("The Compute host was not found.");
        if (string.IsNullOrWhiteSpace(provider.VultrInstanceId))
            throw new VultrException("This host has no Vultr instance yet.");

        VultrInstance instance = await _client.GetInstanceAsync(
            Key(id), provider.VultrInstanceId, cancellationToken).ConfigureAwait(false);

        _compute.SetVultrInstance(
            id,
            instance.Id,
            instance.Status,
            instance.Label,
            instance.CreatedUtc ?? provider.VultrCreatedUtc,
            provider.VultrFirewallGroupId);

        if (instance.IsRunning && !string.IsNullOrWhiteSpace(instance.MainIp))
        {
            _compute.Update(
                id,
                provider.Name,
                ComputeProviderKinds.Remote,
                $"http://{instance.MainIp}:8080",
                token: null);
            _compute.SetStudioUrl(id, $"http://{instance.MainIp}:5117");
            _compute.SetSsh(
                id,
                host: instance.MainIp,
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

    /// <summary>Deletes the instance and its firewall group, then clears the tracked id.</summary>
    public async Task DestroyAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ComputeProviderSettings provider = _compute.Find(id)
            ?? throw new VultrException("The Compute host was not found.");
        if (string.IsNullOrWhiteSpace(provider.VultrInstanceId)) return;

        string apiKey = Key(id);
        await _client.DeleteInstanceAsync(apiKey, provider.VultrInstanceId, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(provider.VultrFirewallGroupId))
        {
            await TryDeleteFirewallAsync(apiKey, provider.VultrFirewallGroupId, cancellationToken)
                .ConfigureAwait(false);
        }

        _compute.SetVultrInstance(id, null, "destroyed", null, null, null);
    }

    private async Task<VultrInstance> WaitForBootAsync(
        string apiKey,
        string instanceId,
        IProgress<SshProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + BootTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VultrInstance instance = await _client.GetInstanceAsync(
                apiKey, instanceId, cancellationToken).ConfigureAwait(false);
            progress?.Report(new SshProgress(
                "vultr", $"Instance {instance.Status} {instance.PowerStatus}..."));

            if (instance.IsRunning
                && instance.MainIp is { Length: > 0 } ip
                && ip != "0.0.0.0")
            {
                return instance;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new VultrException(
            "Vultr did not finish booting the instance within 20 minutes. "
            + "Use Refresh to check it again, or Destroy instance to stop billing.");
    }

    private async Task<string> EnsureFirewallAsync(
        string apiKey,
        string label,
        CancellationToken cancellationToken)
    {
        string group = await _client.CreateFirewallGroupAsync(
            apiKey, label, cancellationToken).ConfigureAwait(false);

        foreach (string port in new[] { "22", "5117", "8080" })
        {
            await _client.AddFirewallRuleAsync(
                apiKey, group, "tcp", port, cancellationToken).ConfigureAwait(false);
        }

        return group;
    }

    private async Task TryDeleteFirewallAsync(
        string apiKey,
        string groupId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteFirewallGroupAsync(apiKey, groupId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The instance is the important resource; a leftover group is harmless.
        }
    }

    private string Key(string id, string? apiKey = null)
    {
        string key = string.IsNullOrWhiteSpace(apiKey)
            ? _compute.VultrApiKey(id) ?? ""
            : apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new VultrException("Add a Vultr API key first.");
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
