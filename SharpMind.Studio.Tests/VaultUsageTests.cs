using SharpMind.Studio.Core.Mcp;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class VaultUsageTests
{
    [Fact]
    public void Find_ReportsEveryPlaceAMcpServerReferencesASecret()
    {
        using var temp = new TempDirectory();
        var store = new McpSettingsStore(temp.Path);
        store.Save(new McpServerSettings
        {
            Id = "files",
            Name = "Files",
            Command = "npx",
            Args = ["--token", "%vault:ARG_TOKEN%"],
            Environment =
            {
                ["GITHUB_TOKEN"] = "%vault:GITHUB_TOKEN%",
                ["PLAIN"] = "not-a-secret",
                ["ESCAPED"] = "%%vault:ESCAPED%%",
            },
        });
        store.Save(new McpServerSettings
        {
            Id = "other",
            Name = "Other",
            Command = "%vault:CMD_TOKEN%",
        });

        var usage = new VaultUsageService(store);

        Assert.Contains(usage.Find("GITHUB_TOKEN"), use =>
            use.Where.Contains("env GITHUB_TOKEN"));
        Assert.Contains(usage.Find("ARG_TOKEN"), use =>
            use.Where.Contains("arguments"));
        Assert.Contains(usage.Find("CMD_TOKEN"), use =>
            use.Where.Contains("command"));
        Assert.Empty(usage.Find("not-a-secret"));
        Assert.Empty(usage.Find("ESCAPED"));
        Assert.Empty(usage.Find("NOPE"));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        using var temp = new TempDirectory();
        var store = new McpSettingsStore(temp.Path);
        store.Save(new McpServerSettings
        {
            Id = "files",
            Name = "Files",
            Command = "npx",
            Environment = { ["TOKEN"] = "%vault:GitHub_Token%" },
        });

        var usage = new VaultUsageService(store);

        Assert.Single(usage.Find("GITHUB_TOKEN"));
        Assert.Single(usage.Find("github_token"));
    }
}
