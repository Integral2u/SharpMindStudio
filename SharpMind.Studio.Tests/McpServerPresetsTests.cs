using SharpMind.Studio.Core.Mcp;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class McpServerPresetsTests
{
    [Fact]
    public void Catalogue_HasUniqueIdsAndAUsableCustomEntry()
    {
        Assert.NotEmpty(McpServerPresets.All);
        Assert.Equal(
            McpServerPresets.All.Count,
            McpServerPresets.All.Select(preset => preset.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(McpServerPresets.Custom, McpServerPresets.Find("missing").Id);
    }

    [Fact]
    public void Catalogue_EntriesThatStartAProcessHaveACommand()
    {
        foreach (McpServerPreset preset in McpServerPresets.All)
        {
            if (preset.Id == McpServerPresets.Custom) continue;

            if (McpTransports.IsHttp(preset.Transport))
            {
                Assert.Equal("", preset.Command);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.Command));
            }
        }
    }

    [Fact]
    public void Catalogue_DoesNotContainAPlaintextSecret()
    {
        foreach (McpServerPreset preset in McpServerPresets.All)
        {
            foreach (McpPresetPair header in preset.Headers)
            {
                if (header.Value.Contains("Bearer", StringComparison.OrdinalIgnoreCase))
                    Assert.Contains("%vault:", header.Value);
            }

            foreach (McpPresetPair environment in preset.Environment)
                Assert.DoesNotContain("secret", environment.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FilesystemPreset_IsAValidStdioEntry()
    {
        McpServerPreset preset = McpServerPresets.Find(McpServerPresets.Filesystem);
        var server = Apply(preset);

        Assert.Equal(McpTransports.Stdio, server.Transport);
        Assert.True(server.IsValid);
        Assert.Equal("Node.js", preset.Requires);
    }

    [Fact]
    public void RemotePreset_IsAnHttpEntryWithAVaultHeaderTemplate()
    {
        McpServerPreset preset = McpServerPresets.Find(McpServerPresets.Remote);
        var server = Apply(preset);

        Assert.True(server.IsHttp);
        Assert.False(server.IsValid);
        Assert.Equal("Authorization", preset.Headers[0].Name);
        Assert.Contains("%vault:", preset.Headers[0].Value);
    }

    private static McpServerSettings Apply(McpServerPreset preset) => new()
    {
        Id = preset.Id,
        Name = preset.Name,
        Transport = preset.Transport,
        Command = preset.Command,
        Args = [.. preset.Args],
        Url = preset.Url,
        Headers = preset.Headers.ToDictionary(
            pair => pair.Name,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase),
        Environment = preset.Environment.ToDictionary(
            pair => pair.Name,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase),
    };
}
