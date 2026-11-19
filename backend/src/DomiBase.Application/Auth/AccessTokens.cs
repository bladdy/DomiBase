namespace DomiBase.Application.Auth;

/// <summary>Claims carried by a DomiBase end-user access token.</summary>
public sealed record AccessTokenClaims(
    Guid UserId,
    Guid ProjectId,
    Guid ApiKeyId,
    string Role)
{
    /// <summary>PostgreSQL functional role applied when the token is valid.</summary>
    public const string AuthenticatedRole = "authenticated";
}

/// <summary>An issued compact JWT together with its absolute expiration.</summary>
public sealed record IssuedAccessToken(
    string Token,
    DateTimeOffset ExpiresAt,
    string KeyId);

/// <summary>Result of validating a bearer token. Never exposes internal exception details.</summary>
public sealed record AccessTokenValidationResult(
    bool Succeeded,
    AccessTokenClaims? Claims,
    string? Error)
{
    public static AccessTokenValidationResult Success(AccessTokenClaims claims) => new(true, claims, null);

    public static AccessTokenValidationResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Issues and validates asymmetric (ES256) access tokens. Keys are supplied per project;
/// the service never trusts role/project claims that were not signed by that project's key.
/// </summary>
public interface IAccessTokenService
{
    /// <summary>Issues a token using the project's private key. The key material is never logged.</summary>
    IssuedAccessToken Issue(
        AuthSigningKeyMaterial keyMaterial,
        AccessTokenClaims claims,
        TimeSpan lifetime);

    /// <summary>Validates a token using the project's public key.</summary>
    AccessTokenValidationResult Validate(
        AuthSigningKeyMaterial keyMaterial,
        string token);
}
