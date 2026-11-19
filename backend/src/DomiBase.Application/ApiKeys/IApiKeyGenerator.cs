using DomiBase.Domain.Projects;

namespace DomiBase.Application.ApiKeys;

public sealed record GeneratedApiKey(string Prefix, string SecretHash, string Value);

/// <summary>
/// Generates cryptographically secure API keys and their verifiable hashes.
/// </summary>
public interface IApiKeyGenerator
{
    GeneratedApiKey Generate(ApiKeyKind kind);

    /// <summary>Computes the same HMAC-SHA-256 hex hash used at creation time (active pepper).</summary>
    string ComputeHash(string value);

    /// <summary>Every hash of <paramref name="value"/> that may match a stored key during pepper rotation.</summary>
    IReadOnlyList<string> ComputeVerificationHashes(string value);

    /// <summary>Constant-time verification against any pepper still present in the keyring.</summary>
    bool Verify(string value, string storedHash);
}
