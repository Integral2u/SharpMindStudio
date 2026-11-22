namespace SharpMind.Studio.Core.Mcp;

/// <summary>Discovered endpoints and resource metadata for one MCP server.</summary>
public sealed record McpOAuthDiscovery(
    string Resource,
    string AuthorizationServer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> ScopesSupported);

/// <summary>A dynamically registered public client.</summary>
public sealed record McpOAuthClientRegistration(
    string ClientId,
    string? ClientSecret,
    string? RegistrationAccessToken);

/// <summary>One authorization-code or refresh-token response.</summary>
public sealed record McpOAuthToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string TokenType,
    string? Scope);

/// <summary>What Studio keeps encrypted about one server's OAuth session.</summary>
public sealed record McpOAuthSession(
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string TokenType,
    string? Scope,
    string? ClientId,
    string? ClientSecret,
    string? RegistrationAccessToken,
    string AuthorizationServer,
    string TokenEndpoint,
    string Resource);

/// <summary>
/// Raised when an OAuth server has no usable token. The UI turns this into a
/// Sign in action instead of a raw 401.
/// </summary>
public sealed class McpSignInRequiredException(string message) : InvalidOperationException(message);

/// <summary>Supplies a bearer token for a configured HTTP MCP server.</summary>
public interface IMcpAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(
        McpServerSettings server,
        CancellationToken cancellationToken = default);
}
