using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpMind.Studio.Core.Mcp;

/// <summary>An OAuth discovery, registration or token failure with a useful message.</summary>
public sealed class McpOAuthException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The OAuth 2.1 pieces MCP HTTP servers use: protected-resource discovery,
/// authorization-server metadata, dynamic client registration, PKCE and token
/// exchange. It does not open a browser or store tokens; the UI service does.
/// </summary>
public sealed partial class McpOAuthClient
{
    private readonly HttpClient _http;

    public McpOAuthClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<McpOAuthDiscovery> DiscoverAsync(
        string resourceUrl,
        string? authorizationServerOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out Uri? resource) ||
            resource.Scheme is not ("http" or "https"))
        {
            throw new McpOAuthException(
                $"OAuth needs an absolute http or https server URL, not '{resourceUrl}'.");
        }

        var scopes = new List<string>();
        string? authorizationServer = null;

        if (string.IsNullOrWhiteSpace(authorizationServerOverride))
        {
            JsonObject? metadata = await FindResourceMetadataAsync(resource, cancellationToken)
                .ConfigureAwait(false);
            if (metadata is not null)
            {
                authorizationServer = FirstString(metadata, "authorization_servers", "authorization_server");
                scopes.AddRange(Strings(metadata["scopes_supported"]));
            }
        }

        authorizationServer = string.IsNullOrWhiteSpace(authorizationServerOverride)
            ? authorizationServer ?? resource.GetLeftPart(UriPartial.Authority)
            : authorizationServerOverride.Trim();

        JsonObject? server = await FindAuthorizationServerMetadataAsync(
            authorizationServer, cancellationToken).ConfigureAwait(false);
        if (server is null)
        {
            throw new McpOAuthException(
                $"Could not read OAuth authorization-server metadata from '{authorizationServer}'.");
        }

        string? authorizationEndpoint = String(server, "authorization_endpoint");
        string? tokenEndpoint = String(server, "token_endpoint");
        if (string.IsNullOrWhiteSpace(authorizationEndpoint) ||
            string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            throw new McpOAuthException(
                "The authorization server metadata does not include authorization and token endpoints.");
        }

        IReadOnlyList<string> methods = Strings(server["code_challenge_methods_supported"]);
        if (methods.Count > 0 &&
            !methods.Any(method => method.Equals("S256", StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpOAuthException(
                "The authorization server does not advertise PKCE S256, which Studio requires.");
        }

        scopes.AddRange(Strings(server["scopes_supported"]));

        return new McpOAuthDiscovery(
            resource.ToString(),
            authorizationServer,
            authorizationEndpoint,
            tokenEndpoint,
            String(server, "registration_endpoint"),
            [.. scopes.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    public async Task<McpOAuthClientRegistration> RegisterAsync(
        McpOAuthDiscovery discovery,
        string redirectUri,
        string? scopes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discovery.RegistrationEndpoint))
        {
            throw new McpOAuthException(
                "This server does not support dynamic client registration. Enter a client ID.");
        }

        var body = new JsonObject
        {
            ["client_name"] = "SharpMind Studio",
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };
        if (!string.IsNullOrWhiteSpace(scopes)) body["scope"] = scopes.Trim();

        JsonObject response = await PostJsonAsync(
            discovery.RegistrationEndpoint, body, cancellationToken).ConfigureAwait(false);
        string? clientId = String(response, "client_id");
        if (string.IsNullOrWhiteSpace(clientId))
            throw new McpOAuthException("The registration response did not include a client_id.");

        return new McpOAuthClientRegistration(
            clientId,
            String(response, "client_secret"),
            String(response, "registration_access_token"));
    }

    public string BuildAuthorizationUrl(
        McpOAuthDiscovery discovery,
        string clientId,
        string redirectUri,
        string? scopes,
        string state,
        string codeChallenge)
    {
        var query = new List<string>
        {
            "response_type=code",
            "client_id=" + Uri.EscapeDataString(clientId),
            "redirect_uri=" + Uri.EscapeDataString(redirectUri),
            "state=" + Uri.EscapeDataString(state),
            "code_challenge=" + Uri.EscapeDataString(codeChallenge),
            "code_challenge_method=S256",
            "resource=" + Uri.EscapeDataString(discovery.Resource),
        };
        if (!string.IsNullOrWhiteSpace(scopes))
            query.Add("scope=" + Uri.EscapeDataString(scopes.Trim()));

        string separator = discovery.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal)
            ? "&"
            : "?";
        return discovery.AuthorizationEndpoint + separator + string.Join("&", query);
    }

    public async Task<McpOAuthToken> ExchangeCodeAsync(
        McpOAuthDiscovery discovery,
        string clientId,
        string? clientSecret,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
            ["resource"] = discovery.Resource,
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret;

        return await PostTokenAsync(discovery.TokenEndpoint, form, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<McpOAuthToken> RefreshAsync(
        McpOAuthSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.RefreshToken))
            throw new McpSignInRequiredException("The OAuth session has no refresh token.");

        if (string.IsNullOrWhiteSpace(session.TokenEndpoint) ||
            string.IsNullOrWhiteSpace(session.ClientId))
        {
            throw new McpSignInRequiredException("The OAuth session is missing its client or token endpoint.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.RefreshToken,
            ["client_id"] = session.ClientId,
            ["resource"] = session.Resource,
        };
        if (!string.IsNullOrWhiteSpace(session.ClientSecret))
            form["client_secret"] = session.ClientSecret;

        return await PostTokenAsync(
            session.TokenEndpoint,
            form,
            session.RefreshToken,
            cancellationToken).ConfigureAwait(false);
    }

    public static (string Verifier, string Challenge) CreatePkce()
    {
        byte[] verifierBytes = RandomNumberGenerator.GetBytes(32);
        string verifier = Base64Url(verifierBytes);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return (verifier, Base64Url(hash));
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private async Task<JsonObject?> FindResourceMetadataAsync(
        Uri resource,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        string? challenged = await ChallengeMetadataUrlAsync(resource, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(challenged)) candidates.Add(challenged);

        string origin = resource.GetLeftPart(UriPartial.Authority);
        candidates.Add(origin + "/.well-known/oauth-protected-resource");

        string path = resource.AbsolutePath.Trim('/');
        if (path.Length > 0)
            candidates.Add(origin + "/.well-known/oauth-protected-resource/" + path);

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            JsonObject? metadata = await TryGetJsonAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (metadata is not null) return metadata;
        }

        return null;
    }

    private async Task<string?> ChallengeMetadataUrlAsync(
        Uri resource,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, resource);
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            string challenge = response.Headers.WwwAuthenticate.ToString();
            Match match = ResourceMetadataRegex().Match(challenge);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonObject?> FindAuthorizationServerMetadataAsync(
        string authorizationServer,
        CancellationToken cancellationToken)
    {
        string issuer = authorizationServer.TrimEnd('/');
        var candidates = new List<string>
        {
            issuer + "/.well-known/oauth-authorization-server",
            issuer + "/.well-known/openid-configuration",
        };

        if (Uri.TryCreate(issuer, UriKind.Absolute, out Uri? issuerUri))
        {
            string origin = issuerUri.GetLeftPart(UriPartial.Authority);
            string path = issuerUri.AbsolutePath.Trim('/');
            if (path.Length > 0)
            {
                candidates.Add(origin + "/.well-known/oauth-authorization-server/" + path);
                candidates.Add(origin + "/.well-known/openid-configuration/" + path);
            }
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            JsonObject? metadata = await TryGetJsonAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (metadata is not null && String(metadata, "token_endpoint") is not null)
                return metadata;
        }

        return null;
    }

    private async Task<JsonObject?> TryGetJsonAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return JsonNode.Parse(body) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonObject> PostJsonAsync(
        string url,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string text = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpOAuthException(
                $"OAuth request to '{url}' failed with {(int)response.StatusCode}: {FirstLine(text)}");
        }

        return JsonNode.Parse(text) as JsonObject
            ?? throw new McpOAuthException($"OAuth response from '{url}' was not JSON.");
    }

    private async Task<McpOAuthToken> PostTokenAsync(
        string url,
        Dictionary<string, string> form,
        string? fallbackRefreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string text = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpOAuthException(
                $"OAuth token request failed with {(int)response.StatusCode}: {FirstLine(text)}");
        }

        JsonObject json = JsonNode.Parse(text) as JsonObject
            ?? throw new McpOAuthException("The OAuth token response was not JSON.");

        string? accessToken = String(json, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new McpOAuthException("The OAuth token response did not include an access_token.");

        int expiresIn = Int(json["expires_in"]) ?? 3600;
        return new McpOAuthToken(
            accessToken,
            String(json, "refresh_token") ?? fallbackRefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)),
            String(json, "token_type") ?? "Bearer",
            String(json, "scope"));
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? [.. array
                .Select(item => item?.GetValue<string>() ?? "")
                .Where(value => value.Length > 0)]
            : [];

    private static string? FirstString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj[name] is JsonArray array)
            {
                string? first = array
                    .Select(item => item?.GetValue<string>())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (first is not null) return first;
            }

            string? value = String(obj, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? String(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out int number)) return number;
        if (value.TryGetValue(out double real)) return (int)real;
        if (value.TryGetValue(out string? text) && int.TryParse(text, out int parsed))
            return parsed;
        return null;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int index = text.IndexOfAny(['\r', '\n']);
        return (index >= 0 ? text[..index] : text).Trim();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    [GeneratedRegex("resource_metadata=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceMetadataRegex();
}
