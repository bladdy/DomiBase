using DomiBase.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DomiBase.Infrastructure.HealthChecks;

/// <summary>
/// Readiness probe for the control database. Reports only a generic status,
/// never the connection string or driver exception details.
/// </summary>
public sealed class ControlDatabaseHealthCheck(
    ControlDatabaseOptions options,
    ILogger<ControlDatabaseHealthCheck> logger) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString)
            {
                Timeout = (int)Timeout.TotalSeconds,
                CommandTimeout = (int)Timeout.TotalSeconds
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(timeout.Token);

            return HealthCheckResult.Healthy("Control database reachable.");
        }
        catch (Exception exception) when (
            exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            logger.LogWarning("Control database readiness check failed: {ExceptionType}", exception.GetType().Name);
            return HealthCheckResult.Unhealthy("Control database unreachable.");
        }
    }
}
