using DomiBase.Application.Runtime;

namespace DomiBase.Application.Auth;

/// <summary>Public, secret-free view of an end user.</summary>
public sealed record AuthUser(Guid Id, string Email, DateTimeOffset CreatedAt);

/// <summary>Stored user row, including the Argon2id PHC hash (never returned by the API).</summary>
public sealed record AuthUserRecord(
    Guid Id,
    string Email,
    string PasswordHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt)
{
    public AuthUser ToPublic() => new(Id, Email, CreatedAt);
}

/// <summary>Refresh token row without the token value (only its SHA-256 hash is stored).</summary>
public sealed record RefreshTokenRecord(
    Guid Id,
    Guid UserId,
    Guid FamilyId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// SQL access to the per-project <c>auth</c> schema. It always connects with the project's
/// restricted runtime login; the schema is never exposed through the public data API.
/// </summary>
public interface IAuthUserRepository
{
    Task<AuthUserRecord?> FindByEmailAsync(
        RuntimeProjectContext context,
        string normalizedEmail,
        CancellationToken cancellationToken = default);

    Task<AuthUserRecord?> FindByIdAsync(
        RuntimeProjectContext context,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a user, or returns <c>null</c> when the email is already registered.</summary>
    Task<AuthUserRecord?> CreateUserAsync(
        RuntimeProjectContext context,
        string normalizedEmail,
        string passwordHash,
        CancellationToken cancellationToken = default);

    Task CreateRefreshTokenAsync(
        RuntimeProjectContext context,
        Guid userId,
        Guid familyId,
        string tokenHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<RefreshTokenRecord?> FindRefreshTokenAsync(
        RuntimeProjectContext context,
        string tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes the old token and inserts its successor in the same family.
    /// Returns <c>false</c> when the old token was already consumed or revoked (concurrent
    /// rotation or reuse), in which case the caller must revoke the whole family.
    /// </summary>
    Task<bool> RotateRefreshTokenAsync(
        RuntimeProjectContext context,
        Guid tokenId,
        Guid userId,
        Guid familyId,
        string newTokenHash,
        DateTimeOffset consumedAt,
        DateTimeOffset newExpiresAt,
        CancellationToken cancellationToken = default);

    Task RevokeFamilyAsync(
        RuntimeProjectContext context,
        Guid familyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);

    Task RevokeRefreshTokenAsync(
        RuntimeProjectContext context,
        string tokenHash,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
}
