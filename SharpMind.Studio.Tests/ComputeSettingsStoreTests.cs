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
}
