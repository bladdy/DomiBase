namespace DomiBase.Application.Security;

/// <summary>
/// Credential and signing settings for the single-owner administrative login.
/// This is deliberately separate from end-user Auth and from the long-lived
/// service token used by automation and tests.
/// </summary>
public sealed class AdminLoginOptions
{
    public const int MinimumPasswordLength = 12;
    public const int MinimumSecretLength = 32;
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>Secret used only to HMAC-sign short-lived panel sessions.</summary>
    public string SigningSecret { get; init; } = string.Empty;

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(12);
}
