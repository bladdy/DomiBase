namespace DomiBase.Infrastructure.Security;

/// <summary>One versioned AES-256-GCM key in the deployment keyring.</summary>
public sealed record SecretKeyEntry(string Id, byte[] Key);

/// <summary>
/// Key material for encrypting secrets at rest (for example per-project runtime database
/// passwords). Keys come from the deployment environment and are never persisted in the
/// database. <see cref="Key"/> is active; <see cref="AdditionalKeys"/> and
/// <see cref="LegacyKey"/> only decrypt existing values.
/// </summary>
public sealed class SecretProtectionOptions
{
    public const int KeySize = 32;

    public required byte[] Key { get; init; }

    public string ActiveKeyId { get; init; } = "v2";

    public IReadOnlyList<SecretKeyEntry> AdditionalKeys { get; init; } = [];

    /// <summary>Key used by legacy <c>v1</c> ciphertexts, which carry no key id.</summary>
    public byte[]? LegacyKey { get; init; }
}
