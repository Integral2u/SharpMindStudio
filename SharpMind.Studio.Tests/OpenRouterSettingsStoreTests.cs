using SharpMind.Studio.Core.OpenRouter;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class OpenRouterSettingsStoreTests
{
    [Fact]
    public void ApiKey_IsEncryptedAtRestAndRoundTrips()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("openrouter.json");
        string key = temp.Combine("openrouter.key");
        var store = new OpenRouterSettingsStore(settings, key);

        store.SetApiKey("sk-or-secret");
        store.SetDefaults("https://openrouter.ai/api/v1/", OpenRouterSettingsDocument.DefaultModelId);

        Assert.True(File.Exists(settings));
        Assert.DoesNotContain("sk-or-secret", File.ReadAllText(settings));
        Assert.Equal("sk-or-secret", store.ApiKey);
        Assert.True(store.IsConfigured);

        var reopened = new OpenRouterSettingsStore(settings, key);
        Assert.Equal("sk-or-secret", reopened.ApiKey);
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId, reopened.Document.DefaultModel);
        Assert.Equal("https://openrouter.ai/api/v1", reopened.Document.BaseUrl);
        Assert.Contains(OpenRouterSettingsDocument.DefaultModelId, reopened.Document.ActiveModels);
    }

    [Fact]
    public void ClearAndDefaults_AreApplied()
    {
        using var temp = new TempDirectory();
        var store = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));

        store.SetApiKey("sk-or-secret");
        store.ClearApiKey();
        Assert.Null(store.ApiKey);
        Assert.False(store.IsConfigured);

        store.SetDefaults(null, null);
        Assert.Equal(OpenRouterSettingsDocument.DefaultBaseUrl, store.Document.BaseUrl);
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId, store.Document.DefaultModel);

        store.SetDefaults("https://openrouter.ai", null);
        Assert.Equal("https://openrouter.ai/api/v1", store.Document.BaseUrl);

        store.SetDefaults("http://localhost:1234/v1", null);
        Assert.Equal("http://localhost:1234/v1", store.Document.BaseUrl);

        store.SetDefaults("https://openrouter.ai/api", null);
        Assert.Equal("https://openrouter.ai/api/v1", store.Document.BaseUrl);
    }

    [Fact]
    public void ActiveModels_AddRemoveAndDefaultFollowTheList()
    {
        using var temp = new TempDirectory();
        var store = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));

        Assert.Equal(
            [OpenRouterSettingsDocument.DefaultModelId],
            store.Document.ActiveModels);

        Assert.True(store.AddActiveModel("anthropic/claude-sonnet-4"));
        Assert.False(store.AddActiveModel("anthropic/claude-sonnet-4"));
        Assert.Equal(2, store.Document.ActiveModels.Count);

        store.SetDefaultModel("anthropic/claude-sonnet-4");
        Assert.Equal("anthropic/claude-sonnet-4", store.Document.DefaultModel);

        Assert.True(store.RemoveActiveModel("anthropic/claude-sonnet-4"));
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId, store.Document.DefaultModel);
    }

    [Fact]
    public void LegacyGpt4oSeed_IsRemovedAndReplacedWithFree()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("openrouter.json");
        string key = temp.Combine("openrouter.key");
        File.WriteAllText(settings, """
            {
              "schema": "studio-openrouter/1",
              "baseUrl": "https://openrouter.ai/api/v1",
              "defaultModel": "openai/gpt-4o-mini",
              "activeModels": ["openai/gpt-4o-mini"],
              "freeModelsSeeded": false
            }
            """);

        var store = new OpenRouterSettingsStore(settings, key);

        Assert.DoesNotContain("openai/gpt-4o-mini", store.Document.ActiveModels);
        Assert.Contains(OpenRouterSettingsDocument.DefaultModelId, store.Document.ActiveModels);
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId, store.Document.DefaultModel);
        Assert.True(store.Document.LegacyDefaultRemoved);
    }

    [Fact]
    public void RememberModel_KeepsRecentModelsUniqueAndActive()
    {
        using var temp = new TempDirectory();
        var store = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));

        store.RememberModel("openai/gpt-4o-mini");
        store.RememberModel("anthropic/claude-sonnet-4");
        store.RememberModel("openai/gpt-4o-mini");

        Assert.Equal(
            ["openai/gpt-4o-mini", "anthropic/claude-sonnet-4"],
            store.Document.RecentModels);
        Assert.Contains("anthropic/claude-sonnet-4", store.Document.ActiveModels);
    }
}
