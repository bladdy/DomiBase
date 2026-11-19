namespace DomiBase.Domain.Projects;

/// <summary>
/// A project API key. Only a verifiable hash of the key is persisted;
/// the clear-text value is shown once at creation/rotation.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public ApiKeyKind Kind { get; set; } = ApiKeyKind.Public;

    /// <summary>Visible, non-secret prefix used for identification (for example <c>db_public_</c>).</summary>
    public required string Prefix { get; set; }

    /// <summary>Hex encoded HMAC-SHA-256 hash of the key value. Never the original key.</summary>
    public required string SecretHash { get; set; }

    /// <summary>Optional label to distinguish keys of the same project.</summary>
    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Optional server-enforced expiry. Never supplied by Runtime callers.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Hash of the immediately previous key value during a rotation overlap. It lets the
    /// older value keep resolving until <see cref="PreviousHashExpiresAt"/>, while the old
    /// row itself stays revoked.
    /// </summary>
    public string? PreviousSecretHash { get; set; }

    public DateTimeOffset? PreviousHashExpiresAt { get; set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is not null && ExpiresAt <= now;
}
