using Microsoft.Extensions.Options;
using SharpMind.Studio;
using SharpMind.Studio.Core.Gateway;
using SharpMind.Studio.Core.Storage;
using SharpMind.Studio.Services;
using Xunit;

namespace SharpMind.Studio.Tests;

[Collection("studio-paths")]
public sealed class GatewayVaultTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public GatewayVaultTests()
    {
        StudioPaths.UseRoot(_temp.Path);
        StudioPaths.EnsureCreated();
    }

    [Fact]
    public void UpstreamAuthorization_CanComeFromTheVault()
    {
        var secrets = new TestSecretResolver(("UPSTREAM_TOKEN", "upstream-secret"));
        using var gateway = new GatewayService(Options.Create(new StudioOptions()), secrets);

        gateway.SaveProxySettings(
            GatewayConfigModes.Proxy,
            "http://127.0.0.1:5117",
            "inline-token");
        Assert.Equal("inline-token", gateway.UpstreamAuthorization);

        gateway.UseUpstreamAuthorizationSecret("UPSTREAM_TOKEN");

        Assert.Equal("upstream-secret", gateway.UpstreamAuthorization);
        Assert.Equal("UPSTREAM_TOKEN", gateway.Config.UpstreamAuthorizationSecret);
        Assert.Null(gateway.Config.UpstreamAuthorizationProtected);

        gateway.SaveProxySettings(
            GatewayConfigModes.Proxy,
            "http://127.0.0.1:5117",
            "inline-again");

        Assert.Equal("inline-again", gateway.UpstreamAuthorization);
        Assert.Null(gateway.Config.UpstreamAuthorizationSecret);
    }

    [Fact]
    public void Clear_RemovesBothTheInlineValueAndTheReference()
    {
        var secrets = new TestSecretResolver(("UPSTREAM_TOKEN", "upstream-secret"));
        using var gateway = new GatewayService(Options.Create(new StudioOptions()), secrets);

        gateway.UseUpstreamAuthorizationSecret("UPSTREAM_TOKEN");
        gateway.SaveProxySettings(
            GatewayConfigModes.Proxy,
            "http://127.0.0.1:5117",
            clearAuthorization: true);

        Assert.Null(gateway.UpstreamAuthorization);
        Assert.Null(gateway.Config.UpstreamAuthorizationSecret);
    }

    public void Dispose() => _temp.Dispose();
}
