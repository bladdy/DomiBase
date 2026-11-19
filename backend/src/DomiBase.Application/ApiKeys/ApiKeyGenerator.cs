using System.Security.Cryptography;
using System.Text;
using DomiBase.Domain.Projects;

namespace DomiBase.Application.ApiKeys;

/// <summary>
/// Generates <c>db_public_</c> / <c>db_secret_</c> keys with 256 bits of entropy and
/// stores only an HMAC-SHA-256 hash keyed with a server-side pepper.
/// </summary>
public sealed class ApiKeyGenerator : IApiKeyGenerator
{
    private const int SecretByteLength = 32;
    private const int DisplaySuffixLength = 8;
    private const string PublicKeyPrefix = "db_public_";
    private const string SecretKeyPrefix = "db_secret_";

    private readonly byte[][] _peppers;

    public ApiKeyGenerator(ApiKeyGeneratorOptions options)
    {
        var peppers = new List<string> { options.Pepper };
        peppers.AddRange(options.AdditionalPeppers);

        if (peppers.Any(pepper =>
                string.IsNullOrWhiteSpace(pepper) ||
                pepper.Length < ApiKeyGeneratorOptions.MinimumPepperLength))
        {
            throw new ArgumentException(
                $"Every API key pepper must be at least {ApiKeyGeneratorOptions.MinimumPepperLength} characters long.",
                nameof(options));
        }

        if (peppers.Count != peppers.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("API key peppers must be distinct.", nameof(options));
        }

        _peppers = peppers.Select(Encoding.UTF8.GetBytes).ToArray();
    }

    public GeneratedApiKey Generate(ApiKeyKind kind)
    {
        var keyPrefix = kind switch
        {
            ApiKeyKind.Public => PublicKeyPrefix,
            ApiKeyKind.Secret => SecretKeyPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported API key kind.")
        };

        var randomBytes = RandomNumberGenerator.GetBytes(SecretByteLength);
        var randomPart = Base64UrlEncode(randomBytes);
        var value = keyPrefix + randomPart;

        return new GeneratedApiKey(
            Prefix: keyPrefix + randomPart[..DisplaySuffixLength],
            SecretHash: ComputeHash(value),
            Value: value);
    }

    public string ComputeHash(string value) => ComputeHash(value, _peppers[0]);

    public IReadOnlyList<string> ComputeVerificationHashes(string value) =>
        _peppers.Select(pepper => ComputeHash(value, pepper)).ToArray();

    public bool Verify(string value, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(storedHash.ToLowerInvariant());
        var matched = false;

        // Always walk the whole keyring: verification time must not reveal which
        // pepper matched.
        foreach (var pepper in _peppers)
        {
            var candidate = Encoding.UTF8.GetBytes(ComputeHash(value, pepper));
            matched |= CryptographicOperations.FixedTimeEquals(candidate, expected);
        }

        return matched;
    }

    private static string ComputeHash(string value, byte[] pepper) =>
        Convert.ToHexString(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
