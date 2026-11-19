using System.Security.Cryptography;
using System.Text;
using DomiBase.Application.Security;

namespace DomiBase.Infrastructure.Security;

/// <summary>
/// AES-256-GCM authenticated encryption for secrets at rest, with a versioned keyring.
/// <list type="bullet">
///   <item>Legacy: <c>v1.&lt;base64url(nonce | tag | ciphertext)&gt;</c>.</item>
///   <item>Current: <c>v2.&lt;key-id&gt;.&lt;base64url(nonce | tag | ciphertext)&gt;</c>.</item>
/// </list>
/// New values always use the active key. Any key in the ring can decrypt older values,
/// so the encryption key can be rotated without rewriting every stored secret.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string LegacyVersion = "v1";
    private const string Version = "v2";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;
    private readonly string _activeKeyId;
    private readonly Dictionary<string, byte[]> _keys;
    private readonly byte[] _legacyKey;

    public AesGcmSecretProtector(SecretProtectionOptions options)
    {
        ValidateKey(options.Key, nameof(options));
        if (string.IsNullOrWhiteSpace(options.ActiveKeyId) || options.ActiveKeyId.Length > 32)
        {
            throw new ArgumentException("Active key id must be 1-32 characters.", nameof(options));
        }

        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [options.ActiveKeyId] = options.Key
        };

        foreach (var entry in options.AdditionalKeys)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 32)
            {
                throw new ArgumentException("Secret key ids must be 1-32 characters.", nameof(options));
            }

            ValidateKey(entry.Key, nameof(options));
            if (!_keys.TryAdd(entry.Id, entry.Key))
            {
                throw new ArgumentException($"Duplicate secret key id '{entry.Id}'.", nameof(options));
            }
        }

        if (options.LegacyKey is not null)
        {
            ValidateKey(options.LegacyKey, nameof(options));
        }

        _key = options.Key;
        _activeKeyId = options.ActiveKeyId;
        _legacyKey = options.LegacyKey ?? options.Key;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        var payload = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, payload, NonceSize + TagSize, ciphertext.Length);

        return Version + "." + _activeKeyId + "." + Base64UrlEncode(payload);
    }

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        plaintext = string.Empty;

        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        var parts = protectedValue.Split('.');
        byte[] payload;
        byte[] key;

        byte[]? decoded;
        switch (parts.Length)
        {
            case 2 when parts[0] == LegacyVersion:
                decoded = Decode(parts[1]);
                key = _legacyKey;
                break;

            case 3 when parts[0] == Version && _keys.TryGetValue(parts[1], out var versionedKey):
                decoded = Decode(parts[2]);
                key = versionedKey;
                break;

            default:
                return false;
        }

        if (decoded is null)
        {
            return false;
        }

        payload = decoded;

        if (payload.Length < NonceSize + TagSize)
        {
            return false;
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        var plaintextBytes = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }
        catch (CryptographicException)
        {
            return false;
        }

        plaintext = Encoding.UTF8.GetString(plaintextBytes);
        return true;
    }

    private static byte[]? Decode(string value)
    {
        try
        {
            return Base64UrlDecode(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void ValidateKey(byte[] key, string parameterName)
    {
        if (key.Length != SecretProtectionOptions.KeySize)
        {
            throw new ArgumentException(
                $"Secret protection key must be {SecretProtectionOptions.KeySize} bytes.",
                parameterName);
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }
}
