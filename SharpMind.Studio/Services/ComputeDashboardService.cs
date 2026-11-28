using SharpMind.Studio.Core.Compute;

namespace SharpMind.Studio.Services;

/// <summary>
/// Caches Vultr instances for the left menu, the Compute page and the exit
/// prompt. Every Vultr account is listed once per refresh, and instances are
/// matched back to the host entry that created them. Instances tagged
/// <c>sharpmind</c> without a saved host are surfaced so they can be destroyed.
/// </summary>
public sealed class ComputeDashboardService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

    private readonly ComputeProviderService _compute;
    private readonly VultrClient _client;
    private readonly AkamaiService _akamai;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<VultrComputeInstance> _instances = [];
    private IReadOnlyList<AkamaiComputeInstance> _akamaiInstances = [];
    private DateTimeOffset _refreshedUtc;
    private string? _vultrError;
    private string? _akamaiError;
    private bool _refreshing;

    public ComputeDashboardService(
        ComputeProviderService compute,
        VultrClient client,
        AkamaiService akamai)
    {
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _akamai = akamai ?? throw new ArgumentNullException(nameof(akamai));
    }

    public event Action? Changed;

    public IReadOnlyList<VultrComputeInstance> Instances => _instances;

    public IReadOnlyList<AkamaiComputeInstance> AkamaiInstances => _akamaiInstances;

    public int InstanceCount => _instances.Count + _akamaiInstances.Count;

    public IReadOnlyList<VultrComputeInstance> Running =>
        [.. _instances.Where(instance => instance.IsRunning)];

    public int RunningCount =>
        _instances.Count(instance => instance.IsRunning)
        + _akamaiInstances.Count(instance => instance.IsRunning);

    public int OwnedRunningCount =>
        _instances.Count(instance => instance.IsRunning && ComputeTeardown.IsOwned(instance))
        + _akamaiInstances.Count(instance => instance.IsRunning);

    public int UntrackedRunningCount => _instances.Count(instance =>
        instance.IsRunning && !ComputeTeardown.IsOwned(instance));

    public double HourlyCost =>
        Running.Sum(instance => instance.HourlyCost)
        + _akamaiInstances.Where(instance => instance.IsRunning)
            .Sum(instance => instance.HourlyCost);

    public double OwnedHourlyCost =>
        Running.Where(ComputeTeardown.IsOwned).Sum(instance => instance.HourlyCost)
        + _akamaiInstances.Where(instance => instance.IsRunning)
            .Sum(instance => instance.HourlyCost);

    public double EstimatedCost =>
        Running.Sum(instance => instance.EstimatedCost)
        + _akamaiInstances.Where(instance => instance.IsRunning)
            .Sum(instance => instance.EstimatedCost);

    /// <summary>The Vultr account error, shown inside the Vultr group.</summary>
    public string? VultrError => _vultrError;

    /// <summary>The Akamai account error, shown inside the Akamai group.</summary>
    public string? AkamaiError => _akamaiError;

    public string? Error => string.Join(
        " ",
        new[] { _vultrError, _akamaiError }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public DateTimeOffset? RefreshedUtc => _refreshedUtc == default ? null : _refreshedUtc;

    public bool IsRefreshing => _refreshing;

    public bool HasAccounts => _compute.Providers.Any(provider =>
        provider.Prefab == ComputeProviderPrefabs.Vultr && _compute.HasVultrApiKey(provider.Id));

    /// <summary>
    /// Lists Vultr instances for every saved account. Cached briefly so opening
    /// the app and the exit prompt do not hammer the API. Force skips the cache.
    /// </summary>
    public async Task RefreshAsync(
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        if (!force
            && _refreshedUtc != default
            && DateTimeOffset.UtcNow - _refreshedUtc < CacheLifetime)
        {
            return;
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        _refreshing = true;
        Notify();
        try
        {
            var byKey = new Dictionary<string, List<ComputeProviderSettings>>(StringComparer.Ordinal);
            foreach (ComputeProviderSettings provider in _compute.Providers)
            {
                if (provider.Prefab != ComputeProviderPrefabs.Vultr) continue;
                string? key = _compute.VultrApiKey(provider.Id);
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!byKey.TryGetValue(key, out List<ComputeProviderSettings>? list))
                {
                    list = [];
                    byKey[key] = list;
                }
                list.Add(provider);
            }

            var accounts = new List<VultrAccountInstances>();
            var failures = new List<VultrAccountFailure>();

            foreach ((string key, List<ComputeProviderSettings> hosts) in byKey)
            {
                IReadOnlyList<VultrInstance> instances;
                try
                {
                    instances = await _client.ListInstancesAsync(key, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (VultrException ex)
                {
                    failures.Add(new VultrAccountFailure(hosts[0].Id, ex.Message));
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(new VultrAccountFailure(
                        hosts[0].Id, $"{ex.GetType().Name}: {ex.Message}"));
                    continue;
                }

                var costs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (VultrPlan plan in await _client
                        .ListPlansAsync(key, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        costs[plan.Id] = plan.HourlyCost;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Prices are nice to have; the instance list still works.
                }

                accounts.Add(new VultrAccountInstances(
                    hosts[0].Id,
                    [.. hosts.Select(host => new VultrHostEntry(
                        host.Id,
                        host.Name,
                        host.VultrInstanceId,
                        host.VultrHourlyCost))],
                    instances,
                    costs));
            }

            VultrComputeInventory inventory =
                VultrComputeInventoryBuilder.Build(accounts, failures);

            foreach (string providerId in inventory.MissingTrackedProviderIds)
            {
                // The instance list can lag a fresh create by a few seconds; do
                // not forget an instance that was tracked less than 15 minutes ago.
                ComputeProviderSettings? host = _compute.Find(providerId);
                if (host?.VultrCreatedUtc is { } created
                    && DateTimeOffset.UtcNow - created < TimeSpan.FromMinutes(15))
                {
                    continue;
                }

                _compute.SetVultrInstance(providerId, null, "destroyed", null, null, null);
            }

            // Keep the host cards' status line in sync with the live instance.
            foreach (VultrComputeInstance view in inventory.Instances)
            {
                if (view.OwnerProviderId is not { Length: > 0 } ownerId) continue;
                ComputeProviderSettings? owner = _compute.Find(ownerId);
                if (owner is null) continue;
                if (string.Equals(owner.VultrInstanceStatus, view.Status, StringComparison.Ordinal)
                    && string.Equals(owner.VultrLabel, view.Label, StringComparison.Ordinal))
                {
                    continue;
                }

                _compute.SetVultrInstance(
                    owner.Id,
                    view.InstanceId,
                    view.Status,
                    view.Label,
                    view.CreatedUtc ?? owner.VultrCreatedUtc,
                    owner.VultrFirewallGroupId);
            }

            _instances = inventory.Instances;
            _vultrError = inventory.Failures.Count == 0
                ? null
                : string.Join(" ", inventory.Failures.Select(failure => failure.Message));

            (IReadOnlyList<AkamaiComputeInstance> akamaiRows, string? akamaiError) =
                await LoadAkamaiAsync(cancellationToken).ConfigureAwait(false);
            _akamaiInstances = akamaiRows;
            _akamaiError = akamaiError;
            _refreshedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _refreshing = false;
            _gate.Release();
            Notify();
        }
    }

    /// <summary>Destroys one instance and clears the host entry that owned it.</summary>
    public async Task DestroyAsync(
        VultrComputeInstance view,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);

        // Never delete an instance another Studio or a deleted host entry owns.
        if (!ComputeTeardown.IsOwned(view))
        {
            throw new InvalidOperationException(
                "This instance is not tracked by this Studio. Destroy it in the Vultr console.");
        }

        string? key = _compute.VultrApiKey(view.AccountProviderId);
        if (string.IsNullOrWhiteSpace(key))
            throw new VultrException("The Vultr API key for this instance is not saved.");

        await _client.DeleteInstanceAsync(key, view.InstanceId, cancellationToken)
            .ConfigureAwait(false);

        ComputeProviderSettings? owner = view.OwnerProviderId is { Length: > 0 } ownerId
            ? _compute.Find(ownerId)
            : null;

        if (!string.IsNullOrWhiteSpace(owner?.VultrFirewallGroupId))
        {
            try
            {
                await _client.DeleteFirewallGroupAsync(
                    key, owner.VultrFirewallGroupId, cancellationToken).ConfigureAwait(false);
            }
            catch (VultrException)
            {
                // The instance is what bills; a leftover group costs nothing.
            }
        }

        if (owner is not null)
            _compute.SetVultrInstance(owner.Id, null, "destroyed", null, null, null);

        _instances = [.. _instances.Where(instance => instance.InstanceId != view.InstanceId)];
        Notify();
    }

    /// <summary>Destroys one Akamai instance and its cloud firewall.</summary>
    public async Task DestroyAkamaiAsync(
        AkamaiComputeInstance view,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);

        await _akamai.DestroyAsync(view.ProviderId, cancellationToken).ConfigureAwait(false);
        _akamaiInstances = [.. _akamaiInstances
            .Where(instance => instance.ProviderId != view.ProviderId)];
        Notify();
    }

    /// <summary>
    /// Destroys every running instance, tracked or orphaned. Returns the number
    /// destroyed and one message per instance that could not be deleted.
    /// </summary>
    public async Task<(int Destroyed, IReadOnlyList<string> Failures)> DestroyRunningAsync(
        CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken, force: true).ConfigureAwait(false);

        int destroyed = 0;
        var failures = new List<string>();
        foreach (VultrComputeInstance view in Running
            .Where(ComputeTeardown.IsOwned)
            .ToList())
        {
            try
            {
                await DestroyAsync(view, cancellationToken).ConfigureAwait(false);
                destroyed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{view.Label}: {ex.Message}");
            }
        }

        foreach (AkamaiComputeInstance view in _akamaiInstances
            .Where(instance => instance.IsRunning)
            .ToList())
        {
            try
            {
                await DestroyAkamaiAsync(view, cancellationToken).ConfigureAwait(false);
                destroyed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{view.Label}: {ex.Message}");
            }
        }

        await RefreshAsync(cancellationToken, force: true).ConfigureAwait(false);
        return (destroyed, failures);
    }

    /// <summary>
    /// Lists the Akamai hosts this Studio created. The live Linode status is
    /// refreshed when an API key is saved; a rejected token is reported without
    /// hiding the instance that is already recorded.
    /// </summary>
    private async Task<(IReadOnlyList<AkamaiComputeInstance> Rows, string? Error)> LoadAkamaiAsync(
        CancellationToken cancellationToken)
    {
        var rows = new List<AkamaiComputeInstance>();
        var failures = new List<string>();

        foreach (ComputeProviderSettings provider in _compute.Providers.ToList())
        {
            if (provider.Prefab != ComputeProviderPrefabs.Akamai ||
                string.IsNullOrWhiteSpace(provider.AkamaiInstanceId))
            {
                continue;
            }

            AkamaiInstance? live = null;
            if (_akamai.HasApiKey(provider.Id))
            {
                try
                {
                    live = await _akamai.RefreshAsync(provider.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{provider.Name}: {ex.Message}");
                }
            }

            ComputeProviderSettings current = _compute.Find(provider.Id) ?? provider;
            rows.Add(new AkamaiComputeInstance(
                current.Id,
                current.Name,
                current.AkamaiInstanceId!,
                live?.Label ?? current.AkamaiLabel ?? current.Name,
                live?.Status ?? current.AkamaiInstanceStatus ?? "",
                live?.PublicIpv4 ?? HostFromUrl(current.BaseUrl),
                live?.Region ?? current.AkamaiRegion ?? "",
                live?.TypeId ?? current.AkamaiTypeId ?? "",
                current.AkamaiHourlyCost ?? 0,
                live?.CreatedUtc ?? current.AkamaiCreatedUtc));
        }

        return (
            [.. rows
                .OrderByDescending(row => row.IsRunning)
                .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)],
            failures.Count == 0 ? null : string.Join(" ", failures));
    }

    private static string? HostFromUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : null;

    private void Notify() => Changed?.Invoke();
}
