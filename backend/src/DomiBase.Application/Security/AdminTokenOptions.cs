namespace DomiBase.Application.Security;

/// <summary>
/// Configuration for the Control Plane administrative authentication.
/// The token is a server-side secret and must never be sent to clients.
/// </summary>
public sealed class AdminTokenOptions
{
    /// <summary>Minimum accepted token length.</summary>
    public const int MinimumLength = 16;

    public string Token { get; init; } = string.Empty;
}
