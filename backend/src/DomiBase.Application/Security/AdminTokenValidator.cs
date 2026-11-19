using System.Security.Cryptography;
using System.Text;

namespace DomiBase.Application.Security;

/// <summary>
/// Validates the administrative token in constant time.
/// Candidate and expected values are hashed first so that comparisons
/// do not leak information through value length.
/// </summary>
public sealed class AdminTokenValidator
{
    private readonly byte[] _expectedHash;

    public AdminTokenValidator(AdminTokenOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new ArgumentException("Admin token must be configured.", nameof(options));
        }

        if (options.Token.Length < AdminTokenOptions.MinimumLength)
        {
            throw new ArgumentException(
                $"Admin token must be at least {AdminTokenOptions.MinimumLength} characters long.",
                nameof(options));
        }

        _expectedHash = Hash(options.Token);
    }

    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        Span<byte> candidateHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(candidate), candidateHash);

        return CryptographicOperations.FixedTimeEquals(candidateHash, _expectedHash);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
