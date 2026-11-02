using SharpMind.Studio.Core.Gateway;

namespace SharpMind.Studio.Tests;

public sealed class GatewayClaimsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Secret = "test-gateway-secret";

    [Fact]
    public void SignAndValidate_RoundTripsTheClaim()
    {
        string header = GatewayClaims.Sign(
            Secret, "device-1", GatewayModes.Mobile, "get", "/chat?tab=1", Now);

        Assert.True(GatewayClaims.TryValidate(
            Secret, header, "GET", "/chat?tab=1", Now, out GatewayClaim? claim));
        Assert.NotNull(claim);
        Assert.Equal("device-1", claim.DeviceId);
        Assert.Equal(GatewayModes.Mobile, claim.Mode);
        Assert.Equal("GET", claim.Method);
        Assert.Equal("/chat?tab=1", claim.Path);
    }

    [Fact]
    public void TryValidate_RejectsATamperedHeader()
    {
        string header = GatewayClaims.Sign(
            Secret, "device-1", GatewayModes.Mobile, "GET", "/chat", Now);
        char[] characters = header.ToCharArray();
        int index = header.IndexOf('.') + 3;
        characters[index] = characters[index] == 'A' ? 'B' : 'A';
        string tampered = new(characters);

        Assert.False(GatewayClaims.TryValidate(
            Secret, tampered, "GET", "/chat", Now, out _));
    }

    [Fact]
    public void TryValidate_RejectsAnotherSecret()
    {
        string header = GatewayClaims.Sign(
            Secret, "device-1", GatewayModes.Mobile, "GET", "/chat", Now);

        Assert.False(GatewayClaims.TryValidate(
            "other-secret", header, "GET", "/chat", Now, out _));
    }

    [Fact]
    public void TryValidate_RejectsAnExpiredClaim()
    {
        string header = GatewayClaims.Sign(
            Secret, "device-1", GatewayModes.Mobile, "GET", "/chat", Now,
            TimeSpan.FromSeconds(30));

        Assert.False(GatewayClaims.TryValidate(
            Secret, header, "GET", "/chat", Now.AddMinutes(5), out _));
    }

    [Fact]
    public void TryValidate_RejectsADifferentMethodOrPath()
    {
        string header = GatewayClaims.Sign(
            Secret, "device-1", GatewayModes.Mobile, "GET", "/chat", Now);

        Assert.False(GatewayClaims.TryValidate(
            Secret, header, "POST", "/chat", Now, out _));
        Assert.False(GatewayClaims.TryValidate(
            Secret, header, "GET", "/work", Now, out _));
    }

    [Fact]
    public void TryValidate_RejectsAMissingHeader()
    {
        Assert.False(GatewayClaims.TryValidate(Secret, null, "GET", "/", Now, out _));
        Assert.False(GatewayClaims.TryValidate(Secret, "not-a-claim", "GET", "/", Now, out _));
    }

    [Fact]
    public void Sign_RejectsUnknownDeviceModes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GatewayClaims.Sign(Secret, "device-1", "admin", "GET", "/", Now));
    }
}

public sealed class GatewaySecretBoxTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsAValue()
    {
        using var temp = new TempDirectory();
        string keyFile = temp.Combine("gateway.key");

        string protectedText = GatewaySecretBox.Protect("super-secret", keyFile);

        Assert.NotEqual("super-secret", protectedText);
        Assert.Equal("super-secret", GatewaySecretBox.Unprotect(protectedText, keyFile));
    }

    [Fact]
    public void Unprotect_ReturnsNullWhenTheKeyIsMissing()
    {
        using var temp = new TempDirectory();
        string protectedText = GatewaySecretBox.Protect("super-secret", temp.Combine("gateway.key"));

        Assert.Null(GatewaySecretBox.Unprotect(
            protectedText, temp.Combine("missing", "gateway.key")));
    }

    [Fact]
    public void Unprotect_ReturnsNullWithAnotherKey()
    {
        using var temp = new TempDirectory();
        string protectedText = GatewaySecretBox.Protect("super-secret", temp.Combine("a.key"));
        GatewaySecretBox.EnsureKey(temp.Combine("b.key"));

        Assert.Null(GatewaySecretBox.Unprotect(protectedText, temp.Combine("b.key")));
    }

    [Fact]
    public void Unprotect_ReturnsNullForGarbageInput()
    {
        using var temp = new TempDirectory();
        string keyFile = temp.Combine("gateway.key");
        GatewaySecretBox.EnsureKey(keyFile);

        Assert.Null(GatewaySecretBox.Unprotect("not-base64", keyFile));
        Assert.Null(GatewaySecretBox.Unprotect(null, keyFile));
        Assert.Null(GatewaySecretBox.Unprotect(Convert.ToBase64String([1, 2, 3]), keyFile));
    }
}

public sealed class GatewayProxyRulesTests
{
    [Fact]
    public void IsActive_RequiresProxyModeAnUpstreamUrlAndASecret()
    {
        var proxy = new GatewayConfig
        {
            Mode = GatewayConfigModes.Proxy,
            UpstreamUrl = "http://192.168.1.10:5117",
        };

        Assert.True(GatewayProxyRules.IsActive(proxy, "secret"));
        Assert.False(GatewayProxyRules.IsActive(proxy, null));
        Assert.False(GatewayProxyRules.IsActive(proxy, ""));
        Assert.False(GatewayProxyRules.IsActive(
            proxy with { Mode = GatewayConfigModes.Local }, "secret"));
        Assert.False(GatewayProxyRules.IsActive(
            proxy with { UpstreamUrl = null }, "secret"));
    }

    [Theory]
    [InlineData("http://192.168.1.10:5117")]
    [InlineData("https://studio.example:443/")]
    [InlineData("http://studio.local")]
    public void TryNormalizeUpstream_AcceptsHttpAndHttps(string value)
    {
        Assert.True(GatewayProxyRules.TryNormalizeUpstream(value, out Uri? uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("studio.local:5117")]
    [InlineData("ftp://studio.local")]
    [InlineData("file:///C:/models")]
    [InlineData("http://user:password@studio.local")]
    public void TryNormalizeUpstream_RejectsEverythingElse(string? value)
    {
        Assert.False(GatewayProxyRules.TryNormalizeUpstream(value, out _));
    }
}
