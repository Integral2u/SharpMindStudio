namespace SharpMind.Studio.Contracts.Auth;

/// <summary>Self-service registration for a top-level account.</summary>
/// <param name="Email">Login identifier. Lower-cased and trimmed by the server.</param>
/// <param name="Handle">Public name used in URLs.</param>
/// <param name="Password">Plain text, over TLS only. Never logged or stored.</param>
/// <param name="DisplayName">Optional friendly name.</param>
public sealed record RegisterRequest(string Email, string Handle, string Password, string? DisplayName = null);

/// <summary>Result of a successful registration.</summary>
public sealed record RegisterResponse(string AccountId, string Handle, string Email);

/// <summary>Password login. Guests use their own email, never the owner's.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>Rotating refresh token exchange.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Issued credentials plus just enough account context to render a header.</summary>
/// <param name="ExpiresIn">Access-token lifetime in seconds.</param>
public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    AccountSummary Account);

/// <summary>Account view shared with clients. Never includes secrets.</summary>
/// <param name="OwnerHandle">Set for guests: the account that invited them.</param>
public sealed record AccountSummary(
    string Id,
    string Handle,
    string Email,
    string? DisplayName,
    string Tier,
    string Role,
    string? OwnerHandle = null);
