namespace DomiBase.Application.Runtime;

/// <summary>
/// Result of resolving the public API key once per HTTP request. It is stored in
/// <see cref="HttpItemKey"/> so the rate limiter and endpoints share the same decision
/// without resolving the key twice or leaking its value into logs.
/// </summary>
public sealed record RuntimeRequestResolution(string? ApiKey, RuntimeResolveResult Result)
{
    public const string HttpItemKey = "domibase:runtime-resolution";

    public static RuntimeRequestResolution Missing() => new(null, RuntimeResolveResult.InvalidKey());
}
