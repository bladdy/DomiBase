using System.Globalization;
using DomiBase.Application.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.Api.Security;

public static class AdminAuthenticationExtensions
{
    /// <summary>
    /// Registers both administrative credentials: the long-lived service token
    /// (also used by automation and tests) and the single-owner panel login that
    /// issues short-lived signed sessions. Fails fast on missing or weak settings.
    /// </summary>
    public static IServiceCollection AddDomiBaseAdminAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var token = configuration["DOMIBASE_ADMIN_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "DOMIBASE_ADMIN_TOKEN is not configured. Add it to the local .env file or set it as an environment variable.");
        }

        if (token.Length < AdminTokenOptions.MinimumLength)
        {
            throw new InvalidOperationException(
                $"DOMIBASE_ADMIN_TOKEN must be at least {AdminTokenOptions.MinimumLength} characters long.");
        }

        var username = Require(configuration, "DOMIBASE_ADMIN_USERNAME");
        var password = Require(configuration, "DOMIBASE_ADMIN_PASSWORD");
        var signingSecret = Require(configuration, "DOMIBASE_ADMIN_SESSION_SECRET");

        if (password.Length < AdminLoginOptions.MinimumPasswordLength)
        {
            throw new InvalidOperationException(
                $"DOMIBASE_ADMIN_PASSWORD must be at least {AdminLoginOptions.MinimumPasswordLength} characters long.");
        }

        if (signingSecret.Length < AdminLoginOptions.MinimumSecretLength)
        {
            throw new InvalidOperationException(
                $"DOMIBASE_ADMIN_SESSION_SECRET must be at least {AdminLoginOptions.MinimumSecretLength} characters long.");
        }

        var lifetimeHours = 12d;
        var configuredHours = configuration["DOMIBASE_ADMIN_SESSION_HOURS"];
        if (!string.IsNullOrWhiteSpace(configuredHours) &&
            (!double.TryParse(configuredHours, NumberStyles.Float, CultureInfo.InvariantCulture, out lifetimeHours) ||
             lifetimeHours <= 0))
        {
            throw new InvalidOperationException("DOMIBASE_ADMIN_SESSION_HOURS must be a positive number.");
        }

        services.AddSingleton(new AdminTokenOptions { Token = token });
        services.AddSingleton<AdminTokenValidator>();
        services.AddSingleton(new AdminLoginOptions
        {
            Username = username,
            Password = password,
            SigningSecret = signingSecret,
            Lifetime = TimeSpan.FromHours(lifetimeHours)
        });
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<AdminSessionService>();

        return services;
    }

    public static IApplicationBuilder UseDomiBaseAdminAuthentication(this IApplicationBuilder app) =>
        app.UseMiddleware<AdminAuthenticationMiddleware>();

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] ?? throw new InvalidOperationException(
            $"{key} is not configured. Add it to the local .env file or set it as an environment variable.");
}
