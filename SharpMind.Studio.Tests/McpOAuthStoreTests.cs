using SharpMind.Studio.Core.Mcp;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class McpOAuthStoreTests
{
    [Fact]
    public void Store_RoundTripsEncryptedTokens()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("mcp-oauth.json");
        string key = temp.Combine("mcp-oauth.key");
        var store = new McpOAuthStore(path, key);

        store.Save("remote", new McpOAuthSession(
            "access-secret",
            "refresh-secret",
            DateTimeOffset.UtcNow.AddHours(1),
            "Bearer",
            "mcp:read",
            "client-1",
            "client-secret",
            "registration-secret",
            "https://auth.example",
            "https://auth.example/token",
            "https://mcp.example/mcp"));

        string json = File.ReadAllText(path);
        Assert.DoesNotContain("access-secret", json);
        Assert.DoesNotContain("refresh-secret", json);
        Assert.DoesNotContain("client-secret", json);
        Assert.Contains("client-1", json);

        var reopened = new McpOAuthStore(path, key);
        McpOAuthSession? session = reopened.Get("REMOTE");
        Assert.NotNull(session);
        Assert.Equal("access-secret", session.AccessToken);
        Assert.Equal("refresh-secret", session.RefreshToken);
        Assert.Equal("client-1", session.ClientId);
        Assert.Equal("client-secret", session.ClientSecret);
        Assert.Equal("https://auth.example/token", session.TokenEndpoint);
        Assert.True(reopened.Contains("remote"));
    }

    [Fact]
    public void Remove_ClearsTheSession()
    {
        using var temp = new TempDirectory();
        var store = new McpOAuthStore(
            temp.Combine("mcp-oauth.json"),
            temp.Combine("mcp-oauth.key"));

        store.Save("remote", Session());
        Assert.True(store.Remove("remote"));
        Assert.False(store.Remove("remote"));
        Assert.False(store.Contains("remote"));
        Assert.Null(store.Get("remote"));
    }

    [Fact]
    public void Store_ReportsNoTokensWhenTheKeyIsMissing()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("mcp-oauth.json");
        string key = temp.Combine("mcp-oauth.key");
        var store = new McpOAuthStore(path, key);
        store.Save("remote", Session());

        File.Delete(key);
        File.Delete(key + ".dpapi");

        var reopened = new McpOAuthStore(path, key);
        McpOAuthSession? session = reopened.Get("remote");
        Assert.NotNull(session);
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
        Assert.Equal("client-1", session.ClientId);
    }

    private static McpOAuthSession Session() => new(
        "access",
        "refresh",
        DateTimeOffset.UtcNow.AddHours(1),
        "Bearer",
        null,
        "client-1",
        null,
        null,
        "https://auth.example",
        "https://auth.example/token",
        "https://mcp.example/mcp");
}
