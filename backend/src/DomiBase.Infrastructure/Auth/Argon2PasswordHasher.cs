using System.Security.Cryptography;
using System.Text;
using DomiBase.Application.Auth;
using Konscious.Security.Cryptography;

namespace DomiBase.Infrastructure.Auth;

/// <summary>
/// Argon2id password hashing (64 MiB, 3 iterations, parallelism 1, 16-byte salt, 32-byte hash).
/// Stored format is the standard PHC string:
/// <c>$argon2id$v=19$m=65536,t=3,p=1$&lt;salt&gt;$&lt;hash&gt;</c>.
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemorySizeKb = 65_536;
    private const int Iterations = 3;
    private const int Parallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Version = 19;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeHash(password, salt);

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"$argon2id$v={Version}$m={MemorySizeKb},t={Iterations},p={Parallelism}${Encode(salt)}${Encode(hash)}");
    }

    public bool Verify(string passwordHash, string password)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
        {
            return false;
        }

        if (!TryParse(passwordHash, out var salt, out var expectedHash))
        {
            return false;
        }

        var actualHash = ComputeHash(password, salt);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static byte[] ComputeHash(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = MemorySizeKb,
            Iterations = Iterations,
            DegreeOfParallelism = Parallelism
        };

        return argon2.GetBytes(HashSize);
    }

    private static bool TryParse(string passwordHash, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];

        var parts = passwordHash.Split('$');
        if (parts.Length != 6 ||
            parts[0].Length != 0 ||
            !string.Equals(parts[1], "argon2id", StringComparison.Ordinal) ||
            !string.Equals(parts[2], $"v={Version}", StringComparison.Ordinal) ||
            !string.Equals(parts[3], $"m={MemorySizeKb},t={Iterations},p={Parallelism}", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            salt = Decode(parts[4]);
            hash = Decode(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length == SaltSize && hash.Length == HashSize;
    }

    // PHC strings use base64 without padding.
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] Decode(string value)
    {
        var padded = value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
