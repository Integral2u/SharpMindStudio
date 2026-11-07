using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using SharpMind.Core;

namespace SharpMind.Studio.Core.Work;

/// <summary>
/// Read-only web access for Work. The project's Web permission gates every
/// request, and the HTTP stack refuses to connect to loopback, private,
/// link-local or metadata addresses so a prompted model cannot reach Studio's
/// own admin API or a cloud metadata service.
/// </summary>
public sealed class WorkWebTools(Func<WorkToolAccess, Task<bool>>? permission = null)
{
    private const int MaxCharacters = 512 * 1024;
    private const int MaxRedirects = 5;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectToPublicHostAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    [ToolDesc("Fetches a public http or https URL as text. Use this only when the project allows web access.")]
    public async Task<string> FetchUrl(
        [ToolDesc("Absolute http or https URL.")] string url)
    {
        if (!TryCreateAllowedUri(url, out Uri? uri, out string? uriError))
            return uriError;

        if (!await IsAllowedAsync(uri))
            return $"Permission denied: FetchUrl may not access '{uri}' with the current project permission.";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            Uri current = uri;
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using HttpResponseMessage response = await Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!TryCreateAllowedUri(next.ToString(), out Uri? redirect, out string? redirectError))
                        return redirectError;
                    if (!await IsAllowedAsync(redirect))
                        return $"Permission denied: FetchUrl may not access '{redirect}' with the current project permission.";
                    current = redirect;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await ReadLimitedAsync(response, timeout.Token);
            }

            return $"Error fetching URL: more than {MaxRedirects} redirects.";
        }
        catch (OperationCanceledException)
        {
            return "Error fetching URL: the request timed out.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return $"Error fetching URL: {ex.Message}";
        }
    }

    /// <summary>
    /// True when the address is a normal public unicast address. Loopback,
    /// private, link-local, carrier-grade NAT, multicast, reserved and
    /// documentation ranges are rejected.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            if (b[0] == 0) return false;                                  // 0.0.0.0/8
            if (b[0] == 10) return false;                                 // 10/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;    // 172.16/12
            if (b[0] == 192 && b[1] == 168) return false;                 // 192.168/16
            if (b[0] == 169 && b[1] == 254) return false;                 // 169.254/16 (metadata)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;   // 100.64/10 CGNAT
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;      // 192.0.0/24
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;  // 198.18/15 benchmark
            if (b[0] >= 224) return false;                                // multicast + reserved
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast)
                return false;

            byte[] b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                      // fc00::/7 unique local
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
                return false;                                             // 2001:db8::/32 documentation
            return true;
        }

        return false;
    }

    private async Task<bool> IsAllowedAsync(Uri uri)
    {
        if (permission is null) return false;
        return await permission(
            new WorkToolAccess(nameof(FetchUrl), WorkToolCategory.Web, uri.ToString()));
    }

    private static async ValueTask<Stream> ConnectToPublicHostAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        foreach (IPAddress address in addresses)
        {
            if (!IsPublicAddress(address)) continue;

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
            }
        }

        throw new HttpRequestException(
            $"FetchUrl refused '{host}': it did not resolve to a public address.");
    }

    private static bool TryCreateAllowedUri(
        string? value,
        out Uri uri,
        out string error)
    {
        uri = null!;
        error = "";

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "URL must be an absolute http or https address.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "URLs with embedded credentials are not allowed.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "URL must include a host.";
            return false;
        }

        if (IPAddress.TryParse(parsed.Host, out IPAddress? address)
            && !IsPublicAddress(address))
        {
            error = $"FetchUrl refused '{parsed.Host}': it is not a public address.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadLimitedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] buffer = new char[MaxCharacters + 1];
        int read = await reader
            .ReadBlockAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
            .ConfigureAwait(false);
        string text = new(buffer, 0, read);
        return read > MaxCharacters
            ? text[..MaxCharacters] + "\n\n[truncated at 512 KB]"
            : text;
    }
}
