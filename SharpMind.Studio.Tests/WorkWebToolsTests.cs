using System.Net;
using SharpMind.Studio.Core.Work;
using Xunit;

namespace SharpMind.Studio.Tests;

/// <summary>
/// FetchUrl runs model-authored URLs, so it must never reach loopback, private
/// networks, cloud metadata or an embedded-credential URL.
/// </summary>
public sealed class WorkWebToolsTests
{
    private static WorkWebTools AllowAll() => new(_ => Task.FromResult(true));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void IsPublicAddress_RejectsLoopbackPrivateAndMetadata(string address)
        => Assert.False(WorkWebTools.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2001:4860:4860::8888")]
    public void IsPublicAddress_AcceptsPublicUnicast(string address)
        => Assert.True(WorkWebTools.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task FetchUrl_RejectsNonHttpSchemes()
    {
        string result = await AllowAll().FetchUrl("file:///C:/Windows/win.ini");
        Assert.Contains("absolute http or https", result);
    }

    [Fact]
    public async Task FetchUrl_RejectsEmbeddedCredentials()
    {
        string result = await AllowAll().FetchUrl("http://user:pass@example.com/");
        Assert.Contains("credentials", result);
    }

    [Fact]
    public async Task FetchUrl_RejectsLoopbackBeforeConnecting()
    {
        string result = await AllowAll().FetchUrl("http://127.0.0.1:5117/gateway/api/settings");
        Assert.Contains("not a public address", result);
    }

    [Fact]
    public async Task FetchUrl_RejectsCloudMetadata()
    {
        string result = await AllowAll().FetchUrl("http://169.254.169.254/latest/meta-data/");
        Assert.Contains("not a public address", result);
    }

    [Fact]
    public async Task FetchUrl_RejectsPrivateAddresses()
    {
        string result = await AllowAll().FetchUrl("http://10.0.0.5/");
        Assert.Contains("not a public address", result);
    }

    [Fact]
    public async Task FetchUrl_RejectsLoopbackHostname()
    {
        string result = await AllowAll().FetchUrl("http://localhost:5117/");
        Assert.Contains("public address", result);
    }

    [Fact]
    public async Task FetchUrl_HonoursTheProjectPermissionBeforeAnyNetworkCall()
    {
        var tools = new WorkWebTools(_ => Task.FromResult(false));

        string result = await tools.FetchUrl("https://example.com/");

        Assert.StartsWith("Permission denied", result);
    }
}
