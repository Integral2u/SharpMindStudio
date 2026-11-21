using SharpMind.Studio.Core.Vault;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class VaultStoreTests
{
    [Fact]
    public void Store_RoundTripsAndDoesNotWriteTheValue()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("vault.json");
        string key = temp.Combine("vault.key");

        var store = new VaultStore(path, key);
        store.Set("GITHUB_TOKEN", "ghp_s3cret", "GitHub read token");

        var reopened = new VaultStore(path, key);
        VaultSecretInfo secret = Assert.Single(reopened.Secrets);
        Assert.Equal("GITHUB_TOKEN", secret.Name);
        Assert.Equal("GitHub read token", secret.Description);
        Assert.Equal("ghp_s3cret", reopened.Resolve("GITHUB_TOKEN"));
        Assert.Equal(VaultSecretState.Ok, reopened.State("GITHUB_TOKEN"));

        string json = File.ReadAllText(path);
        Assert.DoesNotContain("ghp_s3cret", json);
        Assert.Contains("GITHUB_TOKEN", json);
    }

    [Fact]
    public void Store_LooksUpNamesCaseInsensitivelyAndKeepsTheStoredSpelling()
    {
        using var temp = new TempDirectory();
        var store = new VaultStore(temp.Combine("vault.json"), temp.Combine("vault.key"));
        store.Set("GitHub_Token", "ghp_s3cret");

        Assert.Equal("ghp_s3cret", store.Resolve("github_token"));
        VaultSecretInfo secret = Assert.Single(store.Secrets);
        Assert.Equal("GitHub_Token", secret.Name);
    }

    [Fact]
    public void Store_ReplacesAValueWithoutChangingTheNameOrCreatedTime()
    {
        using var temp = new TempDirectory();
        var store = new VaultStore(temp.Combine("vault.json"), temp.Combine("vault.key"));

        VaultSecretInfo created = store.Set("TOKEN", "first", "before");
        VaultSecretInfo replaced = store.Set("token", "second", "after");

        Assert.Equal(created.Name, replaced.Name);
        Assert.Equal(created.CreatedUtc, replaced.CreatedUtc);
        Assert.Equal("after", replaced.Description);
        Assert.Equal("second", store.Resolve("TOKEN"));
        Assert.Single(store.Secrets);
    }

    [Fact]
    public void Store_RemovesASecret()
    {
        using var temp = new TempDirectory();
        var store = new VaultStore(temp.Combine("vault.json"), temp.Combine("vault.key"));
        store.Set("TOKEN", "s3cret");

        Assert.True(store.Remove("token"));
        Assert.False(store.Remove("token"));
        Assert.Null(store.Resolve("TOKEN"));
        Assert.Equal(VaultSecretState.Missing, store.State("TOKEN"));
        Assert.Empty(store.Secrets);
    }

    [Fact]
    public void Store_ReportsUnreadableWhenTheKeyIsGone()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("vault.json");
        string key = temp.Combine("vault.key");
        var store = new VaultStore(path, key);
        store.Set("TOKEN", "s3cret");

        File.Delete(key);
        File.Delete(key + ".dpapi");

        var reopened = new VaultStore(path, key);
        Assert.Equal(VaultSecretState.Unreadable, reopened.State("TOKEN"));
        Assert.Null(reopened.Resolve("TOKEN"));
        Assert.Single(reopened.Secrets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1TOKEN")]
    [InlineData("TOKEN-NAME")]
    [InlineData("TOKEN NAME")]
    [InlineData("TOKEN.NAME")]
    [InlineData("TOKEN/NAME")]
    public void Store_RejectsInvalidNames(string name)
    {
        using var temp = new TempDirectory();
        var store = new VaultStore(temp.Combine("vault.json"), temp.Combine("vault.key"));

        Assert.ThrowsAny<ArgumentException>(() => store.Set(name, "s3cret"));
    }

    [Fact]
    public void Store_RejectsANameThatIsTooLong()
    {
        using var temp = new TempDirectory();
        var store = new VaultStore(temp.Combine("vault.json"), temp.Combine("vault.key"));

        Assert.ThrowsAny<ArgumentException>(
            () => store.Set(new string('A', VaultStore.MaxNameLength + 1), "s3cret"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Store_RejectsAnEmptyValue(string value)
    {
        using var temp = new TempDirectory();
        var store = new VaultStore(temp.Combine("vault.json"), temp.Combine("vault.key"));

        Assert.ThrowsAny<ArgumentException>(() => store.Set("TOKEN", value));
    }

    [Fact]
    public void Store_RejectsAValueThatIsTooLong()
    {
        using var temp = new TempDirectory();
        var store = new VaultStore(temp.Combine("vault.json"), temp.Combine("vault.key"));

        Assert.ThrowsAny<ArgumentException>(
            () => store.Set("TOKEN", new string('x', VaultStore.MaxValueLength + 1)));
    }

    [Fact]
    public void Store_SurvivesADamagedFile()
    {
        using var temp = new TempDirectory();
        string path = temp.Combine("vault.json");
        File.WriteAllText(path, "{ not json");

        var store = new VaultStore(path, temp.Combine("vault.key"));
        Assert.Empty(store.Secrets);
        Assert.Equal(VaultSecretState.Missing, store.State("TOKEN"));
    }
}
