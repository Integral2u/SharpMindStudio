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
        store.SetDefaults("https://openrouter.ai/api/v1/", "openai/gpt-4o-mini", "https://example.test", "Test");

        Assert.True(File.Exists(settings));
        Assert.DoesNotContain("sk-or-secret", File.ReadAllText(settings));
        Assert.Equal("sk-or-secret", store.ApiKey);
        Assert.True(store.IsConfigured);

        var reopened = new OpenRouterSettingsStore(settings, key);
        Assert.Equal("sk-or-secret", reopened.ApiKey);
        Assert.Equal("openai/gpt-4o-mini", reopened.Document.DefaultModel);
        Assert.Equal("https://openrouter.ai/api/v1", reopened.Document.BaseUrl);
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

        store.SetDefaults(null, null, null, null);
        Assert.Equal(OpenRouterSettingsDocument.DefaultBaseUrl, store.Document.BaseUrl);
        Assert.Null(store.Document.DefaultModel);

        store.SetDefaults("https://openrouter.ai", null, null, null);
        Assert.Equal("https://openrouter.ai/api/v1", store.Document.BaseUrl);

        store.SetDefaults("http://localhost:1234/v1", null, null, null);
        Assert.Equal("http://localhost:1234/v1", store.Document.BaseUrl);

        store.SetDefaults("https://openrouter.ai/api", null, null, null);
        Assert.Equal("https://openrouter.ai/api/v1", store.Document.BaseUrl);
    }

    [Fact]
    public void RememberModel_KeepsRecentModelsUnique()
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
    }
}
