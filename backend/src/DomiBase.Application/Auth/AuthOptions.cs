namespace DomiBase.Application.Auth;

/// <summary>Configurable lifetimes and limits for end-user sessions.</summary>
public sealed class AuthOptions
{
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);
}
