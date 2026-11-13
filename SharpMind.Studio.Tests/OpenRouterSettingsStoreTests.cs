using SharpMind.Studio.Core.OpenRouter;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class OpenRouterSettingsStoreTests
{
    private const string OpenRouter = RemoteProviders.OpenRouter;
    private const string EuroRouter = RemoteProviders.EuroRouter;

    [Fact]
    public void ApiKey_IsEncryptedAtRestAndRoundTrips()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("openrouter.json");
        string key = temp.Combine("openrouter.key");
        var store = new OpenRouterSettingsStore(settings, key);

        store.SetApiKey(OpenRouter, "sk-or-secret");
        store.SetDefaults(OpenRouter, "https://openrouter.ai/api/v1/",
            OpenRouterSettingsDocument.DefaultModelId);

        Assert.True(File.Exists(settings));
        Assert.DoesNotContain("sk-or-secret", File.ReadAllText(settings));
        Assert.Equal("sk-or-secret", store.GetApiKey(OpenRouter));
        Assert.True(store.IsConfigured(OpenRouter));

        var reopened = new OpenRouterSettingsStore(settings, key);
        Assert.Equal("sk-or-secret", reopened.GetApiKey(OpenRouter));
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId,
            reopened.Profile(OpenRouter).DefaultModel);
        Assert.Equal("https://openrouter.ai/api/v1", reopened.Profile(OpenRouter).BaseUrl);
        Assert.Contains(OpenRouterSettingsDocument.DefaultModelId,
            reopened.Profile(OpenRouter).ActiveModels);
    }

    [Fact]
    public void ClearAndDefaults_AreApplied()
    {
        using var temp = new TempDirectory();
        var store = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));

        store.SetApiKey(OpenRouter, "sk-or-secret");
        store.ClearApiKey(OpenRouter);
        Assert.Null(store.GetApiKey(OpenRouter));
        Assert.False(store.IsConfigured(OpenRouter));

        store.SetDefaults(OpenRouter, null, null);
        Assert.Equal(OpenRouterSettingsDocument.DefaultBaseUrl, store.Profile(OpenRouter).BaseUrl);
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId,
            store.Profile(OpenRouter).DefaultModel);

        store.SetDefaults(OpenRouter, "https://openrouter.ai", null);
        Assert.Equal("https://openrouter.ai/api/v1", store.Profile(OpenRouter).BaseUrl);

        store.SetDefaults(OpenRouter, "http://localhost:1234/v1", null);
        Assert.Equal("http://localhost:1234/v1", store.Profile(OpenRouter).BaseUrl);

        store.SetDefaults(OpenRouter, "https://openrouter.ai/api", null);
        Assert.Equal("https://openrouter.ai/api/v1", store.Profile(OpenRouter).BaseUrl);
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
            store.Profile(OpenRouter).ActiveModels);

        Assert.True(store.AddActiveModel(OpenRouter, "anthropic/claude-sonnet-4"));
        Assert.False(store.AddActiveModel(OpenRouter, "anthropic/claude-sonnet-4"));
        Assert.Equal(2, store.Profile(OpenRouter).ActiveModels.Count);

        store.SetDefaultModel(OpenRouter, "anthropic/claude-sonnet-4");
        Assert.Equal("anthropic/claude-sonnet-4", store.Profile(OpenRouter).DefaultModel);

        Assert.True(store.RemoveActiveModel(OpenRouter, "anthropic/claude-sonnet-4"));
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId,
            store.Profile(OpenRouter).DefaultModel);
    }

    [Fact]
    public void Providers_KeepSeparateKeysAndModelLists()
    {
        using var temp = new TempDirectory();
        var store = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));

        store.SetApiKey(OpenRouter, "sk-or-secret");
        store.SetApiKey(EuroRouter, "eur-secret");
        store.AddActiveModel(EuroRouter, "openai/gpt-4.1");
        store.SetDefaults(EuroRouter, "https://api.eurouter.ai", "openai/gpt-4.1");

        Assert.Equal("sk-or-secret", store.GetApiKey(OpenRouter));
        Assert.Equal("eur-secret", store.GetApiKey(EuroRouter));
        Assert.DoesNotContain("openai/gpt-4.1", store.Profile(OpenRouter).ActiveModels);
        Assert.Equal(["openai/gpt-4.1"], store.Profile(EuroRouter).ActiveModels);
        Assert.Equal("https://api.eurouter.ai/api/v1", store.Profile(EuroRouter).BaseUrl);
        Assert.Equal("https://openrouter.ai/api/v1", store.Profile(OpenRouter).BaseUrl);

        var reopened = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));
        Assert.Equal("eur-secret", reopened.GetApiKey(EuroRouter));
    }

    [Fact]
    public void NewStore_SeedsEuroRouterWithItsNormalizedBaseUrl()
    {
        using var temp = new TempDirectory();
        var store = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));

        Assert.Equal("https://api.eurouter.ai/api/v1", store.Profile(EuroRouter).BaseUrl);
        Assert.Empty(store.Profile(EuroRouter).ActiveModels);
        Assert.Null(store.GetApiKey(EuroRouter));
    }

    [Fact]
    public void LegacyDocument_IsMigratedIntoTheOpenRouterProfile()
    {
        using var temp = new TempDirectory();
        string settings = temp.Combine("openrouter.json");
        string key = temp.Combine("openrouter.key");
        File.WriteAllText(settings, """
            {
              "schema": "studio-openrouter/1",
              "baseUrl": "https://openrouter.ai/api/v1",
              "defaultModel": "anthropic/claude-sonnet-4",
              "activeModels": ["anthropic/claude-sonnet-4"],
              "freeModelsSeeded": true
            }
            """);

        var store = new OpenRouterSettingsStore(settings, key);

        Assert.Equal("https://openrouter.ai/api/v1", store.Profile(OpenRouter).BaseUrl);
        Assert.Equal("anthropic/claude-sonnet-4", store.Profile(OpenRouter).DefaultModel);
        Assert.Contains("anthropic/claude-sonnet-4", store.Profile(OpenRouter).ActiveModels);
        Assert.True(store.Profile(OpenRouter).FreeModelsSeeded);

        // The next save writes the new schema with the profiles.
        store.SetApiKey(OpenRouter, "sk-or-secret");
        var reopened = new OpenRouterSettingsStore(settings, key);
        Assert.Equal(OpenRouterSettingsDocument.CurrentSchema, reopened.Document.Schema);
        Assert.Equal("sk-or-secret", reopened.GetApiKey(OpenRouter));
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

        Assert.DoesNotContain("openai/gpt-4o-mini", store.Profile(OpenRouter).ActiveModels);
        Assert.Contains(OpenRouterSettingsDocument.DefaultModelId,
            store.Profile(OpenRouter).ActiveModels);
        Assert.Equal(OpenRouterSettingsDocument.DefaultModelId,
            store.Profile(OpenRouter).DefaultModel);
        Assert.True(store.Profile(OpenRouter).LegacyDefaultRemoved);
    }

    [Fact]
    public void RememberModel_KeepsRecentModelsUniqueAndActive()
    {
        using var temp = new TempDirectory();
        var store = new OpenRouterSettingsStore(
            temp.Combine("openrouter.json"),
            temp.Combine("openrouter.key"));

        store.RememberModel(OpenRouter, "openai/gpt-4o-mini");
        store.RememberModel(OpenRouter, "anthropic/claude-sonnet-4");
        store.RememberModel(OpenRouter, "openai/gpt-4o-mini");

        Assert.Equal(
            ["openai/gpt-4o-mini", "anthropic/claude-sonnet-4"],
            store.Profile(OpenRouter).RecentModels);
        Assert.Contains("anthropic/claude-sonnet-4",
            store.Profile(OpenRouter).ActiveModels);
    }
}
