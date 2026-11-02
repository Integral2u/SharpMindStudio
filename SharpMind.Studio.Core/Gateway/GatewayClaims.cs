using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpMind.Studio.Core.Gateway;

/// <summary>Claims a gateway signs for a paired device when it proxies to Studio.</summary>
public sealed record GatewayClaim(
    string DeviceId,
    string Mode,
    string Method,
    string Path,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Short-lived HMAC claims. The gateway signs each proxied request; the
/// upstream Studio verifies the same secret, so the device token itself never
/// has to travel to the upstream host.
/// </summary>
public static class GatewayClaims
{
    public const string HeaderName = "X-SharpMind-Gateway";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(15);

    public static string Sign(
        string secret,
        string deviceId,
        string mode,
        string method,
        string pathAndQuery,
        DateTimeOffset now,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (mode is not (GatewayModes.Mobile or GatewayModes.Full))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown device mode.");

        var claim = new GatewayClaim(
            deviceId,
            mode,
            NormalizeMethod(method),
            NormalizePath(pathAndQuery),
            now,
            now.Add(lifetime ?? TimeSpan.FromSeconds(30)));

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claim);
        byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return $"{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    public static bool TryValidate(
        string? secret,
        string? header,
        string method,
        string pathAndQuery,
        DateTimeOffset now,
        out GatewayClaim? claim)
    {
        claim = null;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(header)) return false;

        int dot = header.IndexOf('.');
        if (dot <= 0 || dot == header.Length - 1) return false;

        byte[] payload;
        byte[] signature;
        try
        {
            payload = FromBase64Url(header[..dot]);
            signature = FromBase64Url(header[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        if (signature.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(signature, expected))
            return false;

        GatewayClaim? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GatewayClaim>(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null) return false;
        if (string.IsNullOrWhiteSpace(parsed.DeviceId)) return false;
        if (parsed.Mode is not (GatewayModes.Mobile or GatewayModes.Full)) return false;
        if (parsed.IssuedUtc > now + ClockSkew) return false;
        if (parsed.ExpiresUtc < now - ClockSkew) return false;
        if (!string.Equals(parsed.Method, NormalizeMethod(method), StringComparison.Ordinal)) return false;
        if (!string.Equals(parsed.Path, NormalizePath(pathAndQuery), StringComparison.Ordinal)) return false;

        claim = parsed;
        return true;
    }

    private static string NormalizeMethod(string method) =>
        string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();

    private static string NormalizePath(string pathAndQuery) =>
        string.IsNullOrWhiteSpace(pathAndQuery) ? "/" : pathAndQuery;

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => "",
        };
        return Convert.FromBase64String(padded);
    }
}
