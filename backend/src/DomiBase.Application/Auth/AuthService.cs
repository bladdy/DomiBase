using DomiBase.Application.Runtime;
using DomiBase.Application.Security;
using Microsoft.Extensions.Logging;

namespace DomiBase.Application.Auth;

/// <summary>
/// Server-side orchestration of end-user registration, login, token refresh and logout.
/// All operations are scoped to a single project resolved by its public API key.
/// </summary>
public sealed class AuthService(
    IPasswordHasher passwordHasher,
    IAccessTokenService accessTokens,
    IAuthSigningKeyService signingKeys,
    IAuthUserRepository users,
    AuthOptions options,
    ILogger<AuthService> logger) : IAuthService
{
    public async Task<AuthResult> RegisterAsync(
        RuntimeProjectContext context,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !email.Contains('@', StringComparison.Ordinal))
        {
            return AuthResult.Fail(AuthError.InvalidInput, "A valid email is required.");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            return AuthResult.Fail(AuthError.InvalidInput, "Password must be at least 8 characters.");
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var passwordHash = passwordHasher.Hash(password);

        var user = await users.CreateUserAsync(context, normalizedEmail, passwordHash, cancellationToken);
        if (user is null)
        {
            return AuthResult.Fail(AuthError.EmailAlreadyRegistered, "This email is already registered.");
        }

        return await IssueSessionAsync(context, user, cancellationToken);
    }

    public async Task<AuthResult> LoginAsync(
        RuntimeProjectContext context,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return AuthResult.Fail(AuthError.InvalidInput, "Email and password are required.");
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await users.FindByEmailAsync(context, normalizedEmail, cancellationToken);

        if (user is null ||
            !passwordHasher.Verify(user.PasswordHash, password) ||
            user.DisabledAt is not null)
        {
            // Same error whether the user does not exist, the password is wrong or the account is disabled.
            return AuthResult.Fail(AuthError.InvalidCredentials, "Invalid email or password.");
        }

        return await IssueSessionAsync(context, user, cancellationToken);
    }

    public async Task<AuthResult> RefreshAsync(
        RuntimeProjectContext context,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return AuthResult.Fail(AuthError.InvalidRefreshToken, "Refresh token is required.");
        }

        var tokenHash = RefreshToken.Hash(refreshToken);
        var stored = await users.FindRefreshTokenAsync(context, tokenHash, cancellationToken);

        if (stored is null ||
            stored.ExpiresAt <= DateTimeOffset.UtcNow ||
            stored.RevokedAt is not null)
        {
            return AuthResult.Fail(AuthError.InvalidRefreshToken, "Invalid or expired refresh token.");
        }

        var newRefreshToken = RefreshToken.Generate();
        var newHash = RefreshToken.Hash(newRefreshToken);
        var now = DateTimeOffset.UtcNow;

        var rotated = await users.RotateRefreshTokenAsync(
            context,
            stored.Id,
            stored.UserId,
            stored.FamilyId,
            newHash,
            now,
            now.Add(options.RefreshTokenLifetime),
            cancellationToken);

        if (!rotated)
        {
            // Reuse detected: the presented token was already consumed. Revoke the whole family.
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId} in project {ProjectId}; revoking family {FamilyId}.",
                stored.UserId,
                context.ProjectId,
                stored.FamilyId);

            await users.RevokeFamilyAsync(context, stored.FamilyId, now, cancellationToken);
            return AuthResult.Fail(AuthError.InvalidRefreshToken, "Invalid or expired refresh token.");
        }

        var user = await users.FindByIdAsync(context, stored.UserId, cancellationToken);
        if (user is null || user.DisabledAt is not null)
        {
            await users.RevokeFamilyAsync(context, stored.FamilyId, now, cancellationToken);
            return AuthResult.Fail(AuthError.InvalidRefreshToken, "Invalid or expired refresh token.");
        }

        var accessToken = await IssueAccessTokenAsync(context, user, cancellationToken);

        return AuthResult.Ok(new AuthSession(
            accessToken.Token,
            accessToken.ExpiresAt,
            newRefreshToken,
            user.ToPublic()));
    }

    public async Task<AuthResult> LogoutAsync(
        RuntimeProjectContext context,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return AuthResult.Fail(AuthError.InvalidInput, "Refresh token is required.");
        }

        var tokenHash = RefreshToken.Hash(refreshToken);
        await users.RevokeRefreshTokenAsync(
            context,
            tokenHash,
            DateTimeOffset.UtcNow,
            cancellationToken);

        // Logout is always idempotent: even an unknown token succeeds.
        return AuthResult.Ok(new AuthSession(string.Empty, DateTimeOffset.UtcNow, string.Empty, null));
    }

    private async Task<AuthResult> IssueSessionAsync(
        RuntimeProjectContext context,
        AuthUserRecord user,
        CancellationToken cancellationToken)
    {
        var accessToken = await IssueAccessTokenAsync(context, user, cancellationToken);

        var refreshToken = RefreshToken.Generate();
        var familyId = Guid.CreateVersion7();

        await users.CreateRefreshTokenAsync(
            context,
            user.Id,
            familyId,
            RefreshToken.Hash(refreshToken),
            DateTimeOffset.UtcNow.Add(options.RefreshTokenLifetime),
            cancellationToken);

        return AuthResult.Ok(new AuthSession(
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken,
            user.ToPublic()));
    }

    private async Task<IssuedAccessToken> IssueAccessTokenAsync(
        RuntimeProjectContext context,
        AuthUserRecord user,
        CancellationToken cancellationToken)
    {
        var keyMaterial = await signingKeys.GetOrCreateAsync(context.ProjectId, cancellationToken);

        var claims = new AccessTokenClaims(
            UserId: user.Id,
            ProjectId: context.ProjectId,
            ApiKeyId: context.ApiKeyId,
            Role: AccessTokenClaims.AuthenticatedRole);

        return accessTokens.Issue(keyMaterial, claims, options.AccessTokenLifetime);
    }
}
