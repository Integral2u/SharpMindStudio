namespace SharpMind.Studio.Contracts.Errors;

/// <summary>Stable machine-readable error codes. Clients branch on these, never on messages.</summary>
public static class ErrorCodes
{
    /// <summary>Malformed or missing input.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>Email already registered.</summary>
    public const string EmailTaken = "email_taken";

    /// <summary>Handle already used at this level.</summary>
    public const string HandleTaken = "handle_taken";

    /// <summary>Handle is reserved or offensive.</summary>
    public const string HandleUnavailable = "handle_unavailable";

    /// <summary>Handle failed the format rules.</summary>
    public const string HandleInvalid = "handle_invalid";

    /// <summary>Password failed the minimum policy.</summary>
    public const string PasswordTooWeak = "password_too_weak";

    /// <summary>Email or password is wrong. Deliberately does not say which.</summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>Too many attempts; retry later.</summary>
    public const string TooManyAttempts = "too_many_attempts";

    /// <summary>Missing, expired, or revoked credentials.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>Authenticated but not allowed to perform the action.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>Invitation token not found.</summary>
    public const string InvitationNotFound = "invitation_not_found";

    /// <summary>Invitation passed its expiry.</summary>
    public const string InvitationExpired = "invitation_expired";

    /// <summary>Invitation was already accepted.</summary>
    public const string InvitationConsumed = "invitation_consumed";

    /// <summary>Invitation was revoked by the owner.</summary>
    public const string InvitationRevoked = "invitation_revoked";

    /// <summary>The resource does not exist, or the caller may not see it.</summary>
    public const string NotFound = "not_found";

    /// <summary>Temporarily unavailable; retry.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>Body shape for non-validation errors. Validation errors use ProblemDetails.</summary>
public sealed record ApiError(string Code, string Message);
