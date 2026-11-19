using System.Security.Cryptography;

namespace DomiBase.Application.Security;

/// <summary>
/// Generates high-entropy passwords for per-project runtime PostgreSQL logins.
/// The value is only stored encrypted and is never returned by the Control API.
/// </summary>
public static class RuntimePasswordGenerator
{
    private const int PasswordByteLength = 32;

    public static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(PasswordByteLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
