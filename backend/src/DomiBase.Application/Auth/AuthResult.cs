namespace DomiBase.Application.Auth;

/// <summary>A freshly issued session: short-lived ES256 access token plus opaque refresh token.</summary>
public sealed record AuthSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    AuthUser? User);

public enum AuthError
{
    None,
    InvalidInput,
    EmailAlreadyRegistered,
    InvalidCredentials,
    InvalidRefreshToken,
    Unavailable
}

/// <summary>Result of an Auth operation; contains no SQL or internal error details.</summary>
public sealed record AuthResult(AuthError Error, AuthSession? Session, string? Message)
{
    public static AuthResult Ok(AuthSession session) => new(AuthError.None, session, null);

    public static AuthResult Fail(AuthError error, string message) => new(error, null, message);
}
