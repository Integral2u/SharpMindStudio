using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ComputeSettingsStoreTests
{
    [Fact]
    public void FirstLoad_SeedsOneLocalDockerHost()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"));

        ComputeProviderSettings provider = Assert.Single(store.Providers);
        Assert.Equal(ComputeProviderKinds.Local, provider.NormalizedKind);
        Assert.Equal(ComputeProviderSettings.DefaultLocalUrl, provider.BaseUrl);
        Assert.Equal(store.DefaultProviderId, provider.Id);
        Assert.Equal(ComputeProviderPresets.DefaultPort, provider.DockerPort);
        Assert.False(string.IsNullOrWhiteSpace(provider.ContainerName));
        Assert.Null(store.Token(provider.Id));
    }

    [Fact]
    public void Token_IsEncryptedAtRestAndRoundTrips()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        string key = temp.Combine("compute.key");
        var store = new ComputeSettingsStore(settings, key);

        ComputeProviderSettings provider = store.Add(
            "Runpod", ComputeProviderKinds.Remote,
            "https://abc-8080.proxy.runpod.net", "super-secret-token");

        Assert.True(File.Exists(settings));
        Assert.DoesNotContain("super-secret-token", File.ReadAllText(settings));

        var reopened = new ComputeSettingsStore(settings, key);
        ComputeProvider resolved = Assert.IsType<ComputeProvider>(reopened.Resolve(provider.Id));
        Assert.Equal("super-secret-token", resolved.Token);
        Assert.Equal("https://abc-8080.proxy.runpod.net", resolved.BaseUrl);
        Assert.True(resolved.IsUsable);
        Assert.Equal("https://abc-5117.proxy.runpod.net", reopened.Find(provider.Id)!.StudioUrl);
    }

    [Fact]
    public void Update_NullTokenKeepsAndBlankClears()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"));
        ComputeProviderSettings provider = store.Add("Host", ComputeProviderKinds.Remote, "https://host", "first");

        Assert.True(store.Update(provider.Id, "Renamed", ComputeProviderKinds.Remote, "https://host/v1", null));
        Assert.Equal("first", store.Token(provider.Id));
        Assert.Equal("Renamed", store.Find(provider.Id)!.Name);
        Assert.Equal("https://host", store.Find(provider.Id)!.BaseUrl);

        Assert.True(store.Update(provider.Id, "Renamed", ComputeProviderKinds.Remote, "https://host", ""));
        Assert.Null(store.Token(provider.Id));
    }

    [Fact]
    public void DefaultHost_MovesWhenTheCurrentOneIsRemoved()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"));
        ComputeProviderSettings first = store.Providers[0];
        ComputeProviderSettings second = store.Add("Second", ComputeProviderKinds.Remote, "https://second", "t");

        Assert.Equal(first.Id, store.DefaultProviderId);
        Assert.True(store.SetDefault(second.Id));
        Assert.Equal(second.Id, store.DefaultProviderId);

        Assert.True(store.Remove(second.Id));
        Assert.Equal(first.Id, store.DefaultProviderId);
        Assert.Single(store.Providers);
    }

    [Fact]
    public void RememberAndForgetModel_UpdatesTheDefault()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"));
        string id = store.Providers[0].Id;

        Assert.True(store.RememberModel(id, "tinystories"));
        Assert.Equal("tinystories", store.Find(id)!.DefaultModel);
        Assert.Contains("tinystories", store.Find(id)!.Models);

        Assert.True(store.RememberModel(id, "other"));
        Assert.Equal("other", store.Find(id)!.DefaultModel);
        Assert.Equal(2, store.Find(id)!.Models.Count);

        Assert.True(store.ForgetModel(id, "other"));
        Assert.Equal("tinystories", store.Find(id)!.DefaultModel);
    }

    [Fact]
    public void DockerSettings_Persist()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        var store = new ComputeSettingsStore(settings, temp.Combine("compute.key"));
        string id = store.Providers[0].Id;
        store.SetToken(id, "token");
        store.SetDocker(id, "my-compute:latest", 9090, useGpu: true, "sharpmind-compute-test");

        var reopened = new ComputeSettingsStore(settings, temp.Combine("compute.key"));
        ComputeProviderSettings provider = reopened.Find(id)!;
        Assert.Equal("my-compute:latest", provider.DockerImage);
        Assert.Equal(9090, provider.DockerPort);
        Assert.True(provider.UseGpu);
        Assert.Equal("sharpmind-compute-test", provider.ContainerName);

        ComputeProvider resolved = Assert.IsType<ComputeProvider>(reopened.Resolve(id));
        Assert.Equal("token", resolved.Token);
        Assert.Equal(provider.BaseUrl, resolved.BaseUrl);
    }

    [Fact]
    public void RunpodSettings_StoreTheKeyEncryptedAndKeepPodState()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        string key = temp.Combine("compute.key");
        var store = new ComputeSettingsStore(settings, key);
        string id = store.Providers[0].Id;

        store.SetRunpod(id, "rpa_secret", "me/sharpmind-server:gpu", "NVIDIA L4", 40);
        Assert.DoesNotContain("rpa_secret", File.ReadAllText(settings));
        Assert.Equal("rpa_secret", store.RunpodApiKey(id));

        store.SetRunpod(id, null, null, null, null);
        Assert.Equal("rpa_secret", store.RunpodApiKey(id));
        Assert.Equal("me/sharpmind-server:gpu", store.Find(id)!.RunpodImage);

        store.SetRunpodPod(id, "pod123", "RUNNING");
        var reopened = new ComputeSettingsStore(settings, key);
        Assert.Equal("pod123", reopened.Find(id)!.RunpodPodId);
        Assert.Equal("RUNNING", reopened.Find(id)!.RunpodPodStatus);
        Assert.Equal("NVIDIA L4", reopened.Find(id)!.RunpodGpuType);
        Assert.Equal(40, reopened.Find(id)!.RunpodDiskGb);
        Assert.Equal("rpa_secret", reopened.RunpodApiKey(id));
    }

    [Fact]
    public void Prefabs_DriveKindAndReload()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        var store = new ComputeSettingsStore(settings, temp.Combine("compute.key"));

        ComputeProviderSettings runpod = store.Add(
            "Runpod", ComputeProviderKinds.Remote, "https://pod", "t",
            ComputeProviderPrefabs.Runpod);
        Assert.Equal(ComputeProviderPrefabs.Runpod, runpod.Prefab);
        Assert.Equal(ComputeProviderKinds.Remote, runpod.NormalizedKind);

        ComputeProviderSettings local = store.Add(
            "Local", ComputeProviderKinds.Remote, "http://127.0.0.1:8080", "t",
            ComputeProviderPrefabs.LocalDocker);
        Assert.Equal(ComputeProviderKinds.Local, local.NormalizedKind);

        ComputeProviderSettings custom = store.Add(
            "Custom", ComputeProviderKinds.Remote, "https://server", "t");
        Assert.Equal(ComputeProviderPrefabs.Custom, custom.Prefab);

        var reopened = new ComputeSettingsStore(settings, temp.Combine("compute.key"));
        Assert.Equal(ComputeProviderPrefabs.Runpod, reopened.Find(runpod.Id)!.Prefab);
        Assert.Equal(ComputeProviderKinds.Remote, reopened.Find(runpod.Id)!.NormalizedKind);
        Assert.Equal(ComputeProviderPrefabs.LocalDocker, reopened.Find(local.Id)!.Prefab);
    }

    [Fact]
    public void RegistryImage_IsRememberedForNewHosts()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        var store = new ComputeSettingsStore(settings, temp.Combine("compute.key"));
        Assert.Null(store.RegistryImage);

        store.SetRegistryImage("me/sharpmind-server:gpu");
        Assert.Equal("me/sharpmind-server:gpu", store.RegistryImage);

        var reopened = new ComputeSettingsStore(settings, temp.Combine("compute.key"));
        Assert.Equal("me/sharpmind-server:gpu", reopened.RegistryImage);

        store.SetRegistryImage(null);
        Assert.Null(store.RegistryImage);
    }

    [Fact]
    public void RecordProbe_KeepsStatusAndHealth()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"));
        string id = store.Providers[0].Id;

        store.RecordProbe(id, true, "Online.");
        Assert.True(store.Find(id)!.Healthy);
        Assert.Equal("Online.", store.Find(id)!.LastStatus);
        Assert.NotNull(store.Find(id)!.LastSeenUtc);

        store.RecordProbe(id, false, "Offline.");
        Assert.False(store.Find(id)!.Healthy);
        Assert.Equal("Offline.", store.Find(id)!.LastStatus);
    }

    [Fact]
    public void EveryComputeCredential_CanComeFromTheVault()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        string key = temp.Combine("compute.key");
        var secrets = new TestSecretResolver(
            ("HOST_TOKEN", "host-secret"),
            ("RUNPOD_KEY", "runpod-secret"),
            ("VULTR_KEY", "vultr-secret"),
            ("SSH_PASSWORD", "ssh-password"),
            ("SSH_PASSPHRASE", "ssh-passphrase"));
        var store = new ComputeSettingsStore(settings, key, secrets);

        ComputeProviderSettings provider = store.Add(
            "Host", ComputeProviderKinds.Remote, "https://host", "inline-token");

        store.SetTokenSecret(provider.Id, "HOST_TOKEN");
        store.SetRunpodApiKeySecret(provider.Id, "RUNPOD_KEY");
        store.SetVultrApiKeySecret(provider.Id, "VULTR_KEY");
        store.SetSshPasswordSecret(provider.Id, "SSH_PASSWORD");
        store.SetSshKeyPassphraseSecret(provider.Id, "SSH_PASSPHRASE");

        Assert.Equal("host-secret", store.Token(provider.Id));
        Assert.Equal("runpod-secret", store.RunpodApiKey(provider.Id));
        Assert.Equal("vultr-secret", store.VultrApiKey(provider.Id));
        Assert.Equal("ssh-password", store.SshPassword(provider.Id));
        Assert.Equal("ssh-passphrase", store.SshKeyPassphrase(provider.Id));

        string json = File.ReadAllText(settings);
        Assert.DoesNotContain("host-secret", json);
        Assert.DoesNotContain("runpod-secret", json);
        Assert.DoesNotContain("vultr-secret", json);
        Assert.DoesNotContain("ssh-password", json);
        Assert.DoesNotContain("ssh-passphrase", json);

        store.SetToken(provider.Id, "inline-again");
        Assert.Equal("inline-again", store.Token(provider.Id));
        Assert.Null(store.Find(provider.Id)!.TokenSecret);
    }

    [Fact]
    public void AkamaiKeyAndDeployment_RoundTripEncrypted()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("compute.json");
        string key = temp.Combine("compute.key");
        var store = new ComputeSettingsStore(settings, key);
        ComputeProviderSettings provider = store.Add(
            "Akamai", ComputeProviderKinds.Remote, "http://127.0.0.1:8080",
            null, ComputeProviderPrefabs.Akamai);

        store.SetAkamai(
            provider.Id, "linode-secret", "g1-gpu-rtx6000-1", "us-east",
            "linode/ubuntu24.04", "42", "laptop", 1.5);
        store.SetAkamaiInstance(
            provider.Id, "123", "running", "sharpmind-abc",
            DateTimeOffset.UtcNow, "9000");

        string json = File.ReadAllText(settings);
        Assert.DoesNotContain("linode-secret", json);

        var reopened = new ComputeSettingsStore(settings, key);
        Assert.Equal("linode-secret", reopened.AkamaiApiKey(provider.Id));
        ComputeProviderSettings saved = reopened.Find(provider.Id)!;
        Assert.Equal(ComputeProviderPrefabs.Akamai, saved.Prefab);
        Assert.Equal("g1-gpu-rtx6000-1", saved.AkamaiTypeId);
        Assert.Equal("us-east", saved.AkamaiRegion);
        Assert.Equal("123", saved.AkamaiInstanceId);
        Assert.Equal("9000", saved.AkamaiFirewallId);
        Assert.Equal(1.5, saved.AkamaiHourlyCost);
    }

    [Fact]
    public void MissingVaultSecret_ReadsAsNoCredential()
    {
        using var temp = new TempDirectory();
        var store = new ComputeSettingsStore(
            temp.Combine("compute.json"),
            temp.Combine("compute.key"),
            new TestSecretResolver());
        ComputeProviderSettings provider = store.Add(
            "Host", ComputeProviderKinds.Remote, "https://host", null);

        store.SetTokenSecret(provider.Id, "MISSING");

        Assert.Null(store.Token(provider.Id));
        Assert.Equal("MISSING", store.Find(provider.Id)!.TokenSecret);
    }
}
