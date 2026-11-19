using System.Security.Cryptography;
using System.Text;
using DomiBase.Infrastructure.Security;

namespace DomiBase.UnitTests;

public class SecretProtectionKeyringTests
{
    private static readonly byte[] LegacyKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] ActiveKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] RetiredKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Protect_UsesActiveKeyIdAndRoundTrips()
    {
        var protector = CreateProtector();

        var protectedValue = protector.Protect("runtime-password");

        Assert.StartsWith("v2.active.", protectedValue);
        Assert.True(protector.TryUnprotect(protectedValue, out var plaintext));
        Assert.Equal("runtime-password", plaintext);
    }

    [Fact]
    public void TryUnprotect_AcceptsValueEncryptedByRetiredKey()
    {
        var oldProtector = new AesGcmSecretProtector(new SecretProtectionOptions
        {
            Key = RetiredKey,
            ActiveKeyId = "retired"
        });
        var value = oldProtector.Protect("old-secret");

        Assert.True(CreateProtector().TryUnprotect(value, out var plaintext));
        Assert.Equal("old-secret", plaintext);
    }

    [Fact]
    public void TryUnprotect_AcceptsLegacyV1CiphertextOnlyWithLegacyKey()
    {
        var value = CreateLegacyValue(LegacyKey, "legacy-secret");
        Assert.StartsWith("v1.", value);

        Assert.True(CreateProtector().TryUnprotect(value, out var plaintext));
        Assert.Equal("legacy-secret", plaintext);

        var withoutLegacy = new AesGcmSecretProtector(new SecretProtectionOptions
        {
            Key = ActiveKey,
            ActiveKeyId = "active",
            AdditionalKeys = [new SecretKeyEntry("retired", RetiredKey)]
        });
        Assert.False(withoutLegacy.TryUnprotect(value, out _));
    }

    [Fact]
    public void TryUnprotect_RejectsUnknownKeyIdTamperingAndMalformedValues()
    {
        var protector = CreateProtector();
        var value = protector.Protect("secret");

        var parts = value.Split('.');
        var unknown = $"v2.unknown.{parts[2]}";

        // Mutate a decoded ciphertext byte: changing the last base64url character can
        // affect only unused padding bits and would not always invalidate the tag.
        var payload = DecodeBase64Url(parts[2]);
        payload[^1] ^= 0x01;
        var tampered = $"{parts[0]}.{parts[1]}.{EncodeBase64Url(payload)}";

        Assert.False(protector.TryUnprotect(unknown, out _));
        Assert.False(protector.TryUnprotect(tampered, out _));
        Assert.False(protector.TryUnprotect("v2.active.not-base64!", out _));
        Assert.False(protector.TryUnprotect("v9.active.abc", out _));
        Assert.False(protector.TryUnprotect(string.Empty, out _));
    }

    [Fact]
    public void Constructor_RejectsDuplicateIdsAndWrongKeySizes()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new SecretProtectionOptions
        {
            Key = ActiveKey,
            ActiveKeyId = "active",
            AdditionalKeys = [new SecretKeyEntry("active", RetiredKey)]
        }));

        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new SecretProtectionOptions
        {
            Key = new byte[16]
        }));
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CreateLegacyValue(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[bytes.Length];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, bytes, ciphertext, tag);
        }

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);

        return "v1." + EncodeBase64Url(payload);
    }

    private static AesGcmSecretProtector CreateProtector() => new(new SecretProtectionOptions
    {
        Key = ActiveKey,
        ActiveKeyId = "active",
        AdditionalKeys = [new SecretKeyEntry("retired", RetiredKey)],
        LegacyKey = LegacyKey
    });
}
