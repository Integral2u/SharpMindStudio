using System.Net;

namespace SharpMind.Studio.Core;

/// <summary>Where the Studio UI the user is looking at is hosted.</summary>
public enum StudioEndpointKind
{
    Local,
    Remote,
    Cloud,
}

/// <summary>
/// Classifies the host in the current URL. Loopback is the local Studio;
/// anything else is a remote Studio (another PC, a LAN address or a future
/// cloud host). Cloud is reserved for an explicit hosted endpoint later.
/// </summary>
public static class StudioEndpoint
{
    public static StudioEndpointKind Classify(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return StudioEndpointKind.Local;

        string value = host.Trim().Trim('[', ']');
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return StudioEndpointKind.Local;

        if (IPAddress.TryParse(value, out IPAddress? address))
            return IPAddress.IsLoopback(address) ? StudioEndpointKind.Local : StudioEndpointKind.Remote;

        return StudioEndpointKind.Remote;
    }

    public static string Label(StudioEndpointKind kind) => kind switch
    {
        StudioEndpointKind.Cloud => "CLOUD HOST",
        StudioEndpointKind.Remote => "REMOTE HOST",
        _ => "LOCAL HOST",
    };
}
