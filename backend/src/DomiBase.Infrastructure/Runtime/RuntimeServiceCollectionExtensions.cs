using DomiBase.Application.Auth;
using DomiBase.Application.Realtime;
using DomiBase.Domain.Projects;
using DomiBase.Application.Runtime;
using DomiBase.Infrastructure.Auth;
using DomiBase.Infrastructure.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DomiBase.Infrastructure.Runtime;

public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Runtime Plane services. The Runtime uses only per-project logins;
    /// no administrative connection string is registered here.
    /// </summary>
    public static IServiceCollection AddDomiBaseRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        RuntimeOptions? options = null)
    {
        services.AddSingleton(options ?? CreateRuntimeOptions(configuration));
        services.AddSingleton<RuntimeConnectionFactory>();
        services.AddScoped<IRuntimeProjectResolver, RuntimeProjectResolver>();
        services.AddScoped<IRuntimeDataService, RuntimeDataService>();
        services.AddScoped<IAuthUserRepository, NpgsqlAuthUserRepository>();

        services.AddSignalR();
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

        return services;
    }

    public static RuntimeOptions CreateRuntimeOptions(IConfiguration configuration)
    {
        var host = configuration["RUNTIME_DB_HOST"];
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "127.0.0.1";
        }

        var port = ReadInt(
            configuration["RUNTIME_DB_PORT"] ?? configuration["DOMIBASE_POSTGRES_PORT"],
            fallback: 5433,
            min: 1,
            max: 65535,
            name: "RUNTIME_DB_PORT");

        var sslModeText = configuration["RUNTIME_DB_SSL_MODE"];
        if (string.IsNullOrWhiteSpace(sslModeText))
        {
            sslModeText = "Prefer";
        }

        if (!Enum.TryParse<SslMode>(sslModeText, ignoreCase: true, out var sslMode))
        {
            throw new InvalidOperationException(
                "RUNTIME_DB_SSL_MODE must be one of: Disable, Allow, Prefer, Require, VerifyCA, VerifyFull.");
        }

        return new RuntimeOptions
        {
            Host = host,
            Port = port,
            SslMode = sslMode,
            MaxPoolSize = ReadInt(configuration["RUNTIME_DB_MAX_POOL_SIZE"], 20, 1, 500, "RUNTIME_DB_MAX_POOL_SIZE"),
            ConnectionTimeoutSeconds = ReadInt(configuration["RUNTIME_DB_CONNECT_TIMEOUT_SECONDS"], 10, 1, 120, "RUNTIME_DB_CONNECT_TIMEOUT_SECONDS"),
            CommandTimeoutSeconds = ReadInt(configuration["RUNTIME_DB_COMMAND_TIMEOUT_SECONDS"], 15, 1, 300, "RUNTIME_DB_COMMAND_TIMEOUT_SECONDS"),
            MaxBodyBytes = ReadInt(configuration["RUNTIME_MAX_BODY_BYTES"], 1_048_576, 1_024, 16_777_216, "RUNTIME_MAX_BODY_BYTES"),
            MaxInsertRows = ReadInt(configuration["RUNTIME_MAX_INSERT_ROWS"], 100, 1, 1_000, "RUNTIME_MAX_INSERT_ROWS"),
            RateLimitPermits = ReadInt(
                configuration["RUNTIME_RATE_LIMIT_PERMITS"],
                RuntimeOptions.DefaultRateLimitPermits,
                ProjectRateLimits.MinPermits,
                ProjectRateLimits.MaxPermits,
                "RUNTIME_RATE_LIMIT_PERMITS"),
            RateLimitWindowSeconds = ReadInt(
                configuration["RUNTIME_RATE_LIMIT_WINDOW_SECONDS"],
                RuntimeOptions.DefaultRateLimitWindowSeconds,
                ProjectRateLimits.MinWindowSeconds,
                ProjectRateLimits.MaxWindowSeconds,
                "RUNTIME_RATE_LIMIT_WINDOW_SECONDS")
        };
    }

    private static int ReadInt(string? raw, int fallback, int min, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException(
                $"{name} must be an integer between {min} and {max}.");
        }

        return value;
    }
}
