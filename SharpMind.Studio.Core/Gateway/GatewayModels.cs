namespace SharpMind.Studio.Core.Gateway;

public static class GatewayModes
{
    public const string Mobile = "mobile";
    public const string Full = "full";
}

public static class PairingStatuses
{
    public const string Open = "open";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
}

public sealed record GatewayDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? UserAgent { get; init; }
    public required string TokenHash { get; init; }
    public string Mode { get; init; } = GatewayModes.Mobile;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? LastIp { get; init; }
    public bool Revoked { get; init; }
}

public sealed record PairingSession
{
    public required string Id { get; init; }
    public required string TokenHash { get; init; }
    public required string PinHash { get; init; }
    public required string PinSalt { get; init; }
    public required string DeviceName { get; init; }
    public string? UserAgent { get; init; }
    public string? Ip { get; init; }
    public string Status { get; init; } = PairingStatuses.Open;
    public int Attempts { get; init; }
    public string? ApprovedMode { get; init; }
    public string? DeviceId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresUtc { get; init; }
}

public sealed record GatewayConfig
{
    public const string CurrentSchema = "studio-gateway/1";

    public string Schema { get; init; } = CurrentSchema;
    public string Mode { get; init; } = "local";
    public string? UpstreamUrl { get; init; }
    public string? UpstreamAuthorizationProtected { get; init; }
    public bool LanEnabled { get; init; }
}
