namespace DomiBase.Application.ApiKeys;

/// <summary>
/// Server-side pepper used to derive verifiable API key hashes.
/// The pepper is a deployment secret and is never stored with the keys.
/// </summary>
public sealed class ApiKeyGeneratorOptions
{
    public const int MinimumPepperLength = 16;

    /// <summary>Active pepper used to hash newly generated keys.</summary>
    public required string Pepper { get; init; }

    /// <summary>
    /// Retired peppers still accepted when verifying existing keys. The active pepper
    /// is always tried first; entries must be distinct.
    /// </summary>
    public IReadOnlyList<string> AdditionalPeppers { get; init; } = [];
}
