using System.Security.Claims;

namespace SharpMind.Studio.Services;

/// <summary>Per-circuit device mode propagated from the gateway claims.</summary>
public sealed class StudioDeviceAccess
{
    public bool IsDevice { get; private set; }
    public bool IsMobile => IsDevice && string.Equals(Mode, "mobile", StringComparison.OrdinalIgnoreCase);
    public string Mode { get; private set; } = "full";
    public string? DeviceId { get; private set; }

    public void SetFrom(ClaimsPrincipal user)
    {
        IsDevice = user.FindFirst("studio:device") is not null;
        Mode = user.FindFirst("studio:mode")?.Value ?? "full";
        DeviceId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
