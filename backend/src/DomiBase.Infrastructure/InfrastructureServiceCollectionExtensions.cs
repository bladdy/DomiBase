using DomiBase.Application.Auth;
using DomiBase.Application.Projects;
using DomiBase.Application.Security;
using AuthOptions = DomiBase.Application.Auth.AuthOptions;
using DomiBase.Infrastructure.Auth;
using DomiBase.Infrastructure.Configuration;
using DomiBase.Infrastructure.ControlPlane;
using DomiBase.Infrastructure.HealthChecks;
using DomiBase.Infrastructure.Persistence;
using DomiBase.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Control Plane metadata database (EF Core/Npgsql) and its readiness probe.
    /// Fails fast when <c>CONTROL_DB_CONNECTION_STRING</c> is missing.
    /// </summary>
    public static IServiceCollection AddDomiBaseInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration["CONTROL_DB_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "CONTROL_DB_CONNECTION_STRING is not configured. Add it to the local .env file or set it as an environment variable.");
        }

        services.AddSingleton(new ControlDatabaseOptions { ConnectionString = connectionString });

        services.AddSingleton(ReadSecretProtectionOptions(configuration));
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        services.AddDbContext<DomiBaseDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IProjectRepository, EfProjectRepository>();

        services.AddScoped<IAuthSigningKeyService, EfAuthSigningKeyService>();
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IAccessTokenService, JwtAccessTokenService>();
        services.AddSingleton(ReadAuthOptions(configuration));
        services.AddScoped<IAuthService, AuthService>();

        services.AddHealthChecks()
            .AddCheck<ControlDatabaseHealthCheck>(
                name: "control-database",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Reads the AES keyring. Entries are <c>id:base64-key</c> separated by commas; the
    /// first entry encrypts new secrets. The legacy single key is kept only to decrypt
    /// <c>v1</c> ciphertexts already stored in the control database.
    /// </summary>
    private static SecretProtectionOptions ReadSecretProtectionOptions(IConfiguration configuration)
    {
        var entries = (configuration["DOMIBASE_SECRETS_KEYS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseSecretKeyEntry)
            .ToList();

        var encodedLegacy = configuration["DOMIBASE_SECRETS_KEY"];
        var legacy = string.IsNullOrWhiteSpace(encodedLegacy)
            ? null
            : DecodeSecretKey(encodedLegacy, "DOMIBASE_SECRETS_KEY");

        if (entries.Count == 0 && legacy is null)
        {
            throw new InvalidOperationException(
                "No secret protection key is configured. Set DOMIBASE_SECRETS_KEYS or DOMIBASE_SECRETS_KEY.");
        }

        return new SecretProtectionOptions
        {
            Key = entries.Count > 0 ? entries[0].Key : legacy!,
            ActiveKeyId = entries.Count > 0 ? entries[0].Id : "v2",
            AdditionalKeys = entries.Skip(1).ToArray(),
            LegacyKey = legacy
        };
    }

    private static SecretKeyEntry ParseSecretKeyEntry(string entry)
    {
        var separator = entry.IndexOf(':');
        if (separator <= 0 || separator == entry.Length - 1)
        {
            throw new InvalidOperationException(
                "DOMIBASE_SECRETS_KEYS entries must have the form id:base64-key.");
        }

        var id = entry[..separator];
        if (id.Length > 32 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new InvalidOperationException(
                "Secret key ids must be 1-32 characters using letters, digits, '-' or '_'.");
        }

        return new SecretKeyEntry(id, DecodeSecretKey(entry[(separator + 1)..], $"DOMIBASE_SECRETS_KEYS[{id}]"));
    }

    private static byte[] DecodeSecretKey(string encoded, string variable)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{variable} must be a base64 encoded 32-byte key.");
        }

        if (key.Length != SecretProtectionOptions.KeySize)
        {
            throw new InvalidOperationException(
                $"{variable} must decode to exactly {SecretProtectionOptions.KeySize} bytes.");
        }

        return key;
    }

    private static AuthOptions ReadAuthOptions(IConfiguration configuration)
    {
        var options = new AuthOptions();

        if (int.TryParse(configuration["AUTH_ACCESS_TOKEN_LIFETIME_MINUTES"], out var accessMinutes) && accessMinutes > 0)
        {
            options.AccessTokenLifetime = TimeSpan.FromMinutes(accessMinutes);
        }

        if (int.TryParse(configuration["AUTH_REFRESH_TOKEN_LIFETIME_DAYS"], out var refreshDays) && refreshDays > 0)
        {
            options.RefreshTokenLifetime = TimeSpan.FromDays(refreshDays);
        }

        return options;
    }
}
