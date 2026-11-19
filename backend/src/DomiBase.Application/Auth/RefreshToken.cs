using System.Security.Cryptography;
using System.Text;

namespace DomiBase.Application.Auth;

/// <summary>
/// Generates opaque refresh tokens and their storage hash.
/// Tokens carry 256 bits of entropy, so a plain SHA-256 digest is sufficient at rest:
/// rotating the API-key pepper never invalidates existing sessions.
/// </summary>
public static class RefreshToken
{
    private const int TokenByteLength = 32;

    /// <summary>Creates a new base64url token (43 characters).</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Computes the lowercase hex SHA-256 digest stored in <c>auth.refresh_tokens</c>.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(digest);
    }
}
