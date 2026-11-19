namespace DomiBase.Domain.Projects;

/// <summary>
/// ES256 signing key of a project. The public key is stored in clear (SPKI base64);
/// the PKCS#8 private key is stored encrypted with AES-256-GCM and is never returned by the API.
/// </summary>
public sealed class ProjectAuthKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ProjectId { get; set; }

    /// <summary>Stable key id (<c>kid</c>) derived from the public key.</summary>
    public required string KeyId { get; set; }

    /// <summary>SubjectPublicKeyInfo base64 of the P-256 public key.</summary>
    public required string PublicKey { get; set; }

    /// <summary>Encrypted PKCS#8 private key; decrypted only in memory.</summary>
    public required string PrivateKeyCiphertext { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the key stops issuing tokens. Valid tokens keep their <c>kid</c>.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    public Project? Project { get; set; }
}
