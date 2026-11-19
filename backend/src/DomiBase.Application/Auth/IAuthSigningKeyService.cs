namespace DomiBase.Application.Auth;

/// <summary>
/// Per-project ES256 key material resolved in memory. The private key is returned as
/// decrypted PKCS#8 base64 and must never be logged or sent over HTTP.
/// </summary>
public sealed record AuthSigningKeyMaterial(
    string KeyId,
    string PublicKeySpkiBase64,
    string PrivateKeyPkcs8Base64);

/// <summary>
/// Loads or creates the active signing key of a project. Creation is idempotent:
/// a partial unique index on the control database guarantees one active key per project.
/// </summary>
public interface IAuthSigningKeyService
{
    /// <summary>Returns the active key, or <c>null</c> when the project has none yet.</summary>
    Task<AuthSigningKeyMaterial?> FindActiveAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Returns the active key, creating and protecting a new one when missing.</summary>
    Task<AuthSigningKeyMaterial> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
}
