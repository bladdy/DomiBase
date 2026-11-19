namespace DomiBase.Application.Auth;

/// <summary>
/// Hashes end-user passwords with a memory-hard algorithm (Argon2id).
/// Only the resulting PHC string is ever stored; plaintext passwords are never logged.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Computes a new salted PHC hash for the given password.</summary>
    string Hash(string password);

    /// <summary>
    /// Verifies a password against a stored PHC hash using a constant-time comparison.
    /// Returns <c>false</c> for malformed hashes instead of throwing.
    /// </summary>
    bool Verify(string passwordHash, string password);
}
