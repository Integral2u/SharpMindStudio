namespace SharpMind.Studio.Core.Compute;

/// <summary>
/// Helpers that turn the few values a hosting dashboard gives you into a
/// working Compute URL. The aim is that a user never has to know the port or
/// the proxy path.
/// </summary>
public static class ComputeProviderPresets
{
    public const int DefaultPort = 8080;
    public const string DefaultImage = "sharpmind-compute:local";

    public static string LocalUrl(int port = DefaultPort) => $"http://127.0.0.1:{port}";

    /// <summary>A stable Docker container name derived from the provider id.</summary>
    public static string ContainerName(string providerId)
    {
        string suffix = providerId.StartsWith("compute-", StringComparison.OrdinalIgnoreCase)
            ? providerId["compute-".Length..]
            : providerId;
        suffix = new string([.. suffix.Where(char.IsLetterOrDigit)]).ToLowerInvariant();
        return string.IsNullOrEmpty(suffix) ? "sharpmind-compute" : $"sharpmind-compute-{suffix}";
    }

    /// <summary>
    /// Runpod exposes a pod port as <c>https://{podId}-{port}.proxy.runpod.net</c>.
    /// Accepts a bare pod id, a full proxy URL, or a URL already containing the port.
    /// </summary>
    public static string? TryRunpodUrl(string? input, int port = DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string value = input.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Host.EndsWith(".proxy.runpod.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".runpod.net", StringComparison.OrdinalIgnoreCase))
            {
                return $"{uri.Scheme}://{uri.Host}";
            }

            // A plain https host (for example a Vultr IP) is used as given.
            return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
        }

        // Strip an accidental pod prefix such as "pod-".
        string pod = value;
        int dash = pod.IndexOf('-');
        if (dash > 0
            && pod[(dash + 1)..].StartsWith(port.ToString(), StringComparison.Ordinal)
            && pod[(dash + 1)..].Length > port.ToString().Length)
        {
            pod = pod[..dash];
        }

        return $"https://{pod}-{port}.proxy.runpod.net";
    }

    /// <summary>Normalizes any host input to scheme://host[:port] without a trailing slash.</summary>
    public static string NormalizeUrl(string? input, int port = DefaultPort)
    {
        if (string.IsNullOrWhiteSpace(input)) return LocalUrl(port);
        string value = input.Trim();

        if (!value.Contains("://", StringComparison.Ordinal))
            value = (value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                     || value.StartsWith("127.", StringComparison.Ordinal)
                     || value.StartsWith("0.0.0.0", StringComparison.Ordinal))
                ? "http://" + value
                : "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return value.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
    }

    /// <summary>A copy/paste token with no ambiguous characters.</summary>
    public static string NewToken(int length = 40)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }
}
