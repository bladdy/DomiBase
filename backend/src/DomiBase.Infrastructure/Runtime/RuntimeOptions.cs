using DomiBase.Domain.Projects;
using Npgsql;

namespace DomiBase.Infrastructure.Runtime;

/// <summary>
/// Runtime Plane database settings. Only the per-project runtime login is used here;
/// no administrative credentials or connection string are available to the Runtime.
/// </summary>
public sealed class RuntimeOptions
{
    public const int DefaultRateLimitPermits = 300;
    public const int DefaultRateLimitWindowSeconds = 60;

    public required string Host { get; init; }

    public required int Port { get; init; }

    public required SslMode SslMode { get; init; }

    public required int MaxPoolSize { get; init; }

    public required int ConnectionTimeoutSeconds { get; init; }

    public required int CommandTimeoutSeconds { get; init; }

    public required int MaxBodyBytes { get; init; }

    public required int MaxInsertRows { get; init; }

    /// <summary>Platform default used when a project has no rate-limit override.</summary>
    public required int RateLimitPermits { get; init; }

    /// <summary>Platform default window length in seconds.</summary>
    public required int RateLimitWindowSeconds { get; init; }
}
