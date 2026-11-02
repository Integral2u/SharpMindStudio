using SharpMind.Studio.Core.Gateway;

namespace SharpMind.Studio.Tests;

public sealed class GatewaySecurityTests
{
    [Fact]
    public void TokensAndPinsAreHashedAndVerifiable()
    {
        string token = GatewaySecurity.NewToken();
        string pin = GatewaySecurity.NewPin();
        string salt = GatewaySecurity.NewSalt();
        string hash = GatewaySecurity.HashPin(pin, salt);

        Assert.Equal(43, token.Length);
        Assert.Equal(6, pin.Length);
        Assert.NotEqual(pin, hash);
        Assert.True(GatewaySecurity.VerifyPin(pin, salt, hash));
        Assert.False(GatewaySecurity.VerifyPin("000000", salt, hash));
        Assert.Equal(64, GatewaySecurity.HashToken(token).Length);
    }
}

public sealed class GatewayStoreTests
{
    [Fact]
    public void Pairing_Approve_CreatesTrustedDevice()
    {
        using var temp = new TempDirectory();
        var store = new GatewayStore(temp.Path);
        string token = GatewaySecurity.NewToken();
        string pin = "123456";
        PairingSession session = store.CreatePairing(
            GatewaySecurity.HashToken(token), pin, "Phone", "UA", "10.0.0.2", TimeSpan.FromMinutes(5));

        Assert.True(GatewaySecurity.VerifyPin(pin, session.PinSalt, session.PinHash));
        PairingSession awaiting = store.UpdatePairing(session.Id, value => value with
        {
            Status = PairingStatuses.AwaitingApproval,
        })!;
        Assert.Equal(PairingStatuses.AwaitingApproval, awaiting.Status);

        string deviceToken = GatewaySecurity.NewToken();
        GatewayDevice device = store.AddDevice(new GatewayDevice
        {
            Id = "device-1",
            Name = awaiting.DeviceName,
            TokenHash = GatewaySecurity.HashToken(deviceToken),
            Mode = GatewayModes.Mobile,
        });

        Assert.NotNull(store.FindDeviceByTokenHash(GatewaySecurity.HashToken(deviceToken)));
        Assert.Null(store.FindDeviceByTokenHash(GatewaySecurity.HashToken("wrong")));
        store.RevokeDevice(device.Id);
        Assert.Null(store.FindDeviceByTokenHash(GatewaySecurity.HashToken(deviceToken)));
    }

    [Fact]
    public void DeviceLoginToken_IsSingleUse_AndRotatesDeviceToken()
    {
        using var temp = new TempDirectory();
        var store = new GatewayStore(temp.Path);
        string deviceToken = GatewaySecurity.NewToken();
        store.AddDevice(new GatewayDevice
        {
            Id = "device-1",
            Name = "Phone",
            TokenHash = GatewaySecurity.HashToken(deviceToken),
            Mode = GatewayModes.Mobile,
        });

        string login = GatewaySecurity.NewToken();
        Assert.True(store.SetDeviceLoginToken(
            "device-1", GatewaySecurity.HashToken(login), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.NotNull(store.FindDeviceByLoginTokenHash(GatewaySecurity.HashToken(login)));

        string rotated = GatewaySecurity.NewToken();
        Assert.True(store.RedeemDeviceLogin("device-1", GatewaySecurity.HashToken(rotated)));

        Assert.Null(store.FindDeviceByLoginTokenHash(GatewaySecurity.HashToken(login)));
        Assert.Null(store.FindDeviceByTokenHash(GatewaySecurity.HashToken(deviceToken)));
        Assert.NotNull(store.FindDeviceByTokenHash(GatewaySecurity.HashToken(rotated)));
    }

    [Fact]
    public void ExpiredDeviceLoginToken_IsRejected()
    {
        using var temp = new TempDirectory();
        var store = new GatewayStore(temp.Path);
        store.AddDevice(new GatewayDevice
        {
            Id = "device-1",
            Name = "Phone",
            TokenHash = GatewaySecurity.HashToken(GatewaySecurity.NewToken()),
        });

        string login = GatewaySecurity.NewToken();
        Assert.True(store.SetDeviceLoginToken(
            "device-1", GatewaySecurity.HashToken(login), DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Null(store.FindDeviceByLoginTokenHash(GatewaySecurity.HashToken(login)));
    }

    [Fact]
    public void Config_RoundTrips()
    {
        using var temp = new TempDirectory();
        var store = new GatewayStore(temp.Path);
        store.SaveConfig(new GatewayConfig
        {
            Mode = "proxy",
            UpstreamUrl = "http://192.168.1.10:5117",
            UpstreamAuthorizationProtected = "protected-value",
        });

        GatewayConfig config = store.LoadConfig();
        Assert.Equal("proxy", config.Mode);
        Assert.Equal("http://192.168.1.10:5117", config.UpstreamUrl);
    }
}
