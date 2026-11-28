namespace SharpMind.Studio.Contracts.Invitations;

/// <summary>Owner creates a guest identity and a single-use signup link.</summary>
/// <param name="GuestHandle">Scoped to the owner's namespace, not globally unique.</param>
/// <param name="Email">The guest's own email; used for login and recovery.</param>
/// <param name="Scopes">What the owner shares. Omitted means a minimal read-only set.</param>
/// <param name="ExpiresInHours">Link lifetime. Clamped by the server.</param>
public sealed record CreateInvitationRequest(
    string GuestHandle,
    string Email,
    string[]? Scopes = null,
    int? ExpiresInHours = null);

/// <summary>The link is shown once; only its hash is stored.</summary>
public sealed record InvitationCreated(string InvitationId, string Link, DateTimeOffset ExpiresAt);

/// <summary>Owner-facing list item.</summary>
public sealed record InvitationSummary(
    string Id,
    string GuestHandle,
    string Email,
    string[] Scopes,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Public preview shown at the signup link, before the guest sets a password.</summary>
public sealed record InvitationDetails(
    string OwnerHandle,
    string GuestHandle,
    string[] Scopes,
    DateTimeOffset ExpiresAt);

/// <summary>Guest accepts the invitation and chooses their own password.</summary>
public sealed record AcceptInvitationRequest(string Password, string? DisplayName = null);

/// <summary>An accepted guest, as the owner sees them.</summary>
public sealed record GuestSummary(
    string AccountId,
    string Handle,
    string Email,
    string[] Scopes,
    string State,
    DateTimeOffset CreatedAt);
