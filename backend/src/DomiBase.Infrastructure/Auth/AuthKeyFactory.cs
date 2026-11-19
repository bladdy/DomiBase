using System.Security.Cryptography;

namespace DomiBase.Infrastructure.Auth;

/// <summary>Serializable ECDSA P-256 key pair for per-project JWT signing.</summary>
public sealed record AuthKeyPair(
    string KeyId,
    string PublicKeySpkiBase64,
    string PrivateKeyPkcs8Base64);

/// <summary>
/// Generates, exports and reloads per-project ES256 signing keys.
/// Only the PKCS#8 private key is ever persisted, and it is stored encrypted with
/// <c>DOMIBASE_SECRETS_KEY</c>; the public key is stored in clear.
/// </summary>
public static class AuthKeyFactory
{
    private const int KeyIdByteLength = 12;

    /// <summary>Generates a new P-256 pair and its stable key id.</summary>
    public static AuthKeyPair Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var publicKey = key.ExportSubjectPublicKeyInfo();
        var privateKey = key.ExportPkcs8PrivateKey();

        return new AuthKeyPair(
            ComputeKeyId(publicKey),
            Convert.ToBase64String(publicKey),
            Convert.ToBase64String(privateKey));
    }

    /// <summary>Reloads a public key from its SubjectPublicKeyInfo encoding.</summary>
    public static ECDsa LoadPublic(string publicKeySpkiBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicKeySpkiBase64);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
        return key;
    }

    /// <summary>Reloads a private key from its decrypted PKCS#8 encoding.</summary>
    public static ECDsa LoadPrivate(string privateKeyPkcs8Base64)
    {
        ArgumentException.ThrowIfNullOrEmpty(privateKeyPkcs8Base64);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        return key;
    }

    /// <summary>Stable 16-character base64url id derived from the public key.</summary>
    public static string ComputeKeyId(byte[] publicKeySpki)
    {
        var digest = SHA256.HashData(publicKeySpki);
        return Convert.ToBase64String(digest[..KeyIdByteLength])
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
