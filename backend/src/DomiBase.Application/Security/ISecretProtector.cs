namespace DomiBase.Application.Security;

/// <summary>
/// Protects secrets at rest (for example per-project runtime database passwords).
/// Implementations must use authenticated encryption and never log plaintext values.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a plaintext secret into a self-contained, versioned string.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a value produced by <see cref="Protect"/>. Returns <c>false</c> when the
    /// value is malformed or was encrypted with a different key.
    /// </summary>
    bool TryUnprotect(string protectedValue, out string plaintext);
}
