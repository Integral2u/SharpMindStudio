using SharpMind.Studio.Core.Compute;
using Xunit;

namespace SharpMind.Studio.Tests;

public sealed class ComputeProviderPresetsTests
{
    [Theory]
    [InlineData("abc123", "https://abc123-8080.proxy.runpod.net")]
    [InlineData("  abc123  ", "https://abc123-8080.proxy.runpod.net")]
    [InlineData("abc123-8080.proxy.runpod.net", "https://abc123-8080.proxy.runpod.net")]
    [InlineData("https://abc123-8080.proxy.runpod.net", "https://abc123-8080.proxy.runpod.net")]
    public void RunpodUrl_IsBuiltFromPodIdOrUrl(string input, string expected)
    {
        Assert.Equal(expected, ComputeProviderPresets.TryRunpodUrl(input));
    }

    [Fact]
    public void RunpodUrl_HandlesPortOverride()
    {
        Assert.Equal(
            "https://abc123-9000.proxy.runpod.net",
            ComputeProviderPresets.TryRunpodUrl("abc123", 9000));
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:8080")]
    [InlineData("", "http://127.0.0.1:8080")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("localhost:9090", "http://localhost:9090")]
    [InlineData("192.168.1.10", "https://192.168.1.10")]
    [InlineData("http://192.168.1.10:8080/", "http://192.168.1.10:8080")]
    [InlineData("https://example.com/v1", "https://example.com")]
    public void NormalizeUrl_AddsSchemeAndDropsPaths(string? input, string expected)
    {
        Assert.Equal(expected, ComputeProviderPresets.NormalizeUrl(input));
    }

    [Fact]
    public void Token_HasRequestedLengthAndIsUrlSafe()
    {
        string token = ComputeProviderPresets.NewToken(40);
        Assert.Equal(40, token.Length);
        Assert.All(token, character => Assert.True(char.IsLetterOrDigit(character)));
        Assert.NotEqual(token, ComputeProviderPresets.NewToken(40));
    }

    [Fact]
    public void ContainerName_IsStableAndDockerSafe()
    {
        Assert.Equal("sharpmind-compute-abc123", ComputeProviderPresets.ContainerName("compute-abc123"));
        Assert.Equal("sharpmind-compute-abc123", ComputeProviderPresets.ContainerName("abc123"));
        Assert.Equal("sharpmind-compute", ComputeProviderPresets.ContainerName("!!!"));
    }
}
