using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpMind.Studio.Core.Mcp;

namespace SharpMind.Studio.Services;

public sealed record McpOAuthSignInResult(bool SignedIn, string Detail);

/// <summary>Opens the system browser for an OAuth authorization request.</summary>
public interface IMcpOAuthBrowser
{
    Task OpenAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>Waits for the OAuth redirect on a loopback address.</summary>
public interface IMcpOAuthLoopback
{
    string RedirectUri { get; }

    Task<string> WaitForCodeAsync(
        string expectedState,
        CancellationToken cancellationToken = default);
}

/// <summary>Uses the default browser; the URL is shown if it cannot be opened.</summary>
public sealed class SystemOAuthBrowser : IMcpOAuthBrowser
{
    public Task OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new McpOAuthException(
                $"Could not open the browser. Open this URL to authorize: {url}", ex);
        }
    }
}

/// <summary>
/// A small loopback listener for the authorization code. A fixed port makes the
/// redirect URI stable for servers that require it registered ahead of time.
/// </summary>
public sealed class LoopbackOAuthListener : IMcpOAuthLoopback
{
    public const int Port = 33418;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string RedirectUri => $"http://127.0.0.1:{Port}/callback";

    public async Task<string> WaitForCodeAsync(
        string expectedState,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new McpOAuthException(
                "Another OAuth sign-in is already waiting for its browser callback.");
        }

        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                throw new McpOAuthException(
                    $"Could not listen on 127.0.0.1:{Port} for the OAuth callback. "
                    + "Close any other sign-in and try again.", ex);
            }

            while (true)
            {
                using TcpClient client = await listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(
                    stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024, leaveOpen: true);

                string? requestLine = await reader
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine)) continue;

                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2) continue;
                string path = parts[1];
                if (!path.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(stream, 404, "Not found.", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                Dictionary<string, string> query = ParseQuery(path);
                if (query.TryGetValue("error", out string? error))
                {
                    string description = query.TryGetValue("error_description", out string? text)
                        ? text
                        : error;
                    await RespondAsync(stream, 400, "Authorization was refused.", cancellationToken)
                        .ConfigureAwait(false);
                    throw new McpOAuthException($"OAuth authorization failed: {description}");
                }

                if (!query.TryGetValue("state", out string? state) ||
                    !state.Equals(expectedState, StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 400, "The OAuth state did not match.", cancellationToken)
                        .ConfigureAwait(false);
                    throw new McpOAuthException("The OAuth callback state did not match. Try signing in again.");
                }

                if (!query.TryGetValue("code", out string? code) ||
                    string.IsNullOrWhiteSpace(code))
                {
                    await RespondAsync(stream, 400, "No authorization code was returned.", cancellationToken)
                        .ConfigureAwait(false);
                    throw new McpOAuthException("The OAuth callback did not include a code.");
                }

                await RespondAsync(
                    stream,
                    200,
                    "Authorization received. You can close this window and return to Studio.",
                    cancellationToken).ConfigureAwait(false);
                return code;
            }
        }
        finally
        {
            listener.Stop();
            _gate.Release();
        }
    }

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int question = path.IndexOf('?');
        if (question < 0 || question == path.Length - 1) return values;

        foreach (string pair in path[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            string name = Uri.UnescapeDataString(pair[..equals]);
            string value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            values[name] = value;
        }
        return values;
    }

    private static async Task RespondAsync(
        NetworkStream stream,
        int status,
        string message,
        CancellationToken cancellationToken)
    {
        string body = $"<!doctype html><html><body><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string reason = status == 200 ? "OK" : "Bad Request";
        string headers =
            $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {bodyBytes.Length}\r\n"
            + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Runs the authorization-code flow and supplies access tokens to the HTTP
/// transport. Tokens are kept in the encrypted <see cref="McpOAuthStore"/>.
/// </summary>
public sealed class McpOAuthService : IMcpAccessTokenProvider
{
    private readonly McpOAuthClient _client;
    private readonly McpOAuthStore _store;
    private readonly IMcpOAuthBrowser _browser;
    private readonly IMcpOAuthLoopback _loopback;

    public McpOAuthService(
        McpOAuthClient client,
        McpOAuthStore store,
        IMcpOAuthBrowser browser,
        IMcpOAuthLoopback loopback)
    {
        _client = client;
        _store = store;
        _browser = browser;
        _loopback = loopback;
    }

    /// <summary>The redirect URI the user must allow if the server needs a pre-registered client.</summary>
    public string RedirectUri => _loopback.RedirectUri;

    public bool IsSignedIn(string serverId)
    {
        McpOAuthSession? session = _store.Get(serverId);
        return session is not null &&
               (!string.IsNullOrWhiteSpace(session.AccessToken) ||
                !string.IsNullOrWhiteSpace(session.RefreshToken));
    }

    public async Task<McpOAuthSignInResult> SignInAsync(
        McpServerSettings server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsHttp)
            throw new McpOAuthException("OAuth is only available for HTTP MCP servers.");
        if (!server.UsesOAuth)
            throw new McpOAuthException("Choose OAuth for this server before signing in.");

        McpOAuthDiscovery discovery = await _client.DiscoverAsync(
            server.Url, server.OAuthAuthorizationServer, cancellationToken).ConfigureAwait(false);

        McpOAuthSession? existing = _store.Get(server.Id);
        string? scopes = string.IsNullOrWhiteSpace(server.OAuthScopes)
            ? discovery.ScopesSupported.Count > 0
                ? string.Join(' ', discovery.ScopesSupported)
                : null
            : server.OAuthScopes.Trim();

        string? clientId = string.IsNullOrWhiteSpace(server.OAuthClientId)
            ? existing?.ClientId
            : server.OAuthClientId.Trim();
        string? clientSecret = existing?.ClientSecret;
        string? registrationAccessToken = existing?.RegistrationAccessToken;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            McpOAuthClientRegistration registration = await _client.RegisterAsync(
                discovery, _loopback.RedirectUri, scopes, cancellationToken).ConfigureAwait(false);
            clientId = registration.ClientId;
            clientSecret = registration.ClientSecret;
            registrationAccessToken = registration.RegistrationAccessToken;
        }

        (string verifier, string challenge) = McpOAuthClient.CreatePkce();
        string state = McpOAuthClient.CreateState();
        string authorizationUrl = _client.BuildAuthorizationUrl(
            discovery, clientId, _loopback.RedirectUri, scopes, state, challenge);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        Task<string> codeTask = _loopback.WaitForCodeAsync(state, timeout.Token);
        try
        {
            await _browser.OpenAsync(authorizationUrl, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            timeout.Cancel();
            try { await codeTask.ConfigureAwait(false); } catch { }
            throw;
        }

        string code;
        try
        {
            code = await codeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpOAuthException(
                "OAuth sign-in timed out. The browser did not return a code.");
        }

        McpOAuthToken token = await _client.ExchangeCodeAsync(
            discovery,
            clientId,
            clientSecret,
            code,
            _loopback.RedirectUri,
            verifier,
            cancellationToken).ConfigureAwait(false);

        var session = new McpOAuthSession(
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresAtUtc,
            token.TokenType,
            token.Scope ?? scopes,
            clientId,
            clientSecret,
            registrationAccessToken,
            discovery.AuthorizationServer,
            discovery.TokenEndpoint,
            discovery.Resource);
        _store.Save(server.Id, session);

        return new McpOAuthSignInResult(
            true,
            $"Signed in to {new Uri(server.Url).Host}.");
    }

    public async Task<string?> GetAccessTokenAsync(
        McpServerSettings server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.UsesOAuth) return null;

        McpOAuthSession? session = _store.Get(server.Id);
        if (session is null ||
            (string.IsNullOrWhiteSpace(session.AccessToken) &&
             string.IsNullOrWhiteSpace(session.RefreshToken)))
        {
            throw new McpSignInRequiredException(
                $"Sign in is required for MCP server '{server.Name}'.");
        }

        if (!string.IsNullOrWhiteSpace(session.AccessToken) &&
            session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(60))
        {
            return session.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            throw new McpSignInRequiredException(
                $"The OAuth token for '{server.Name}' has expired. Sign in again.");
        }

        try
        {
            McpOAuthToken refreshed = await _client
                .RefreshAsync(session, cancellationToken)
                .ConfigureAwait(false);

            var updated = session with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? session.RefreshToken,
                ExpiresAtUtc = refreshed.ExpiresAtUtc,
                TokenType = refreshed.TokenType,
                Scope = refreshed.Scope ?? session.Scope,
            };
            _store.Save(server.Id, updated);
            return updated.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpSignInRequiredException(
                $"The OAuth session for '{server.Name}' could not be refreshed. Sign in again.");
        }
    }

    public void SignOut(string serverId) => _store.Remove(serverId);
}
